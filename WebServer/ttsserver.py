import io
import os
import random
import hashlib
import asyncio
import logging
from datetime import datetime
from functools import lru_cache
from fastapi import FastAPI, Query, HTTPException
from fastapi.responses import StreamingResponse, FileResponse
import edge_tts

# 📋 Настройка логирования
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s | %(levelname)-8s | %(message)s',
    datefmt='%Y-%m-%d %H:%M:%S',
    handlers=[
        logging.StreamHandler(),  # Вывод в консоль
        logging.FileHandler("tts_server.log", encoding="utf-8")  # Вывод в файл
    ]
)
logger = logging.getLogger("tts-server")

app = FastAPI(title="Edge-TTS сервер (Random Mode)")

# 🎭 Пул голосов для рандома (смешанные языки и тембры)
VOICE_POOL = [
    "ru-RU-DmitryNeural",		# RU Мужской
    "ru-RU-SvetlanaNeural",	    # RU Женский
    "kk-KZ-AigulNeural",		# KZ Женский
    "uk-UA-OstapNeural",		# UA Мужской
    "uk-UA-PolinaNeural",		# UA Женский
	"kk-KZ-DauletNeural",		# KZ Мужской
	"bg-BG-BorislavNeural",     # BG Мужской
    "bg-BG-KalinaNeural",       # BG Женский
    "sr-RS-SophieNeural",       # SR Женский
    "sr-RS-NicholasNeural",     # SR Мужской
    "mk-MK-AleksandarNeural",   # MK Мужской
    "mk-MK-MarijaNeural",       # MK Женский
    "mn-MN-BataaNeural",        # MN Мужской
    "mn-MN-YesuiNeural",        # MN Женский
]

DEFAULT_VOICE = "ru-RU-DmitryNeural"

# 🗄 Кэширование (путь к файлу)
@lru_cache(maxsize=200)
def get_cached_audio_path(text_hash: str) -> str:
    cache_dir = "tts_cache"
    os.makedirs(cache_dir, exist_ok=True)
    path = os.path.join(cache_dir, f"{text_hash}.mp3")
    return path if os.path.exists(path) else None

def save_to_cache(text_hash: str, audio_bytes: bytes):
    cache_dir = "tts_cache"
    os.makedirs(cache_dir, exist_ok=True)
    path = os.path.join(cache_dir, f"{text_hash}.mp3")
    with open(path, "wb") as f:
        f.write(audio_bytes)
    return path

async def generate_edge_tts(text: str, voice: str, rate: str, pitch: str) -> bytes:
    """Генерация аудио с логированием процесса"""
    logger.debug(f"🎵 Запрос к Edge API: voice={voice}, rate={rate}, pitch={pitch}")
    
    communicate = edge_tts.Communicate(
        text=text,
        voice=voice,
        rate=rate,
        volume="+0%",
        pitch=pitch
    )
    audio_buffer = io.BytesIO()
    
    try:
        async for chunk in communicate.stream():
            if chunk["type"] == "audio":
                audio_buffer.write(chunk["data"])
        
        audio_size = audio_buffer.tell()
        if audio_size == 0:
            logger.warning(f"⚠️ Edge API вернул пустой аудио-поток! voice={voice}, text='{text[:50]}...'")
            raise ValueError("Пустой аудио-поток от Edge API")
        
        logger.debug(f"✅ Аудио получено: {audio_size} байт")
        return audio_buffer.getvalue()
        
    except Exception as e:
        logger.error(f"❌ Ошибка при генерации аудио: {type(e).__name__}: {e}")
        raise

@app.get("/say")
async def say(
    text: str = Query(..., min_length=1, max_length=1000, description="Текст (макс 1000 символов)"),
    voice: str = Query(None, description="Конкретный голос (приоритет над randomize)"),
    rate: str = Query(None, description="Скорость (например, +10%)"),
    pitch: str = Query(None, description="Питч (например, +5Hz)"),
    randomize: bool = Query(False, description="🎲 Случайный голос и параметры")
):
    """
    🎙 Генерация TTS. 
    Если randomize=true, игнорирует voice/rate/pitch и выбирает случайно.
    """
    request_id = hashlib.md5(f"{datetime.now().isoformat()}{text}".encode()).hexdigest()[:8]
    logger.info(f"📥 [{request_id}] Запрос: text='{text[:50]}{'...' if len(text) > 50 else ''}'")
    
    # 1. Настройка параметров
    if randomize:
        voice = random.choice(VOICE_POOL)
        rate_val = random.randint(-15, 20)
        rate = f"{rate_val:+d}%"
        pitch_val = random.randint(-30, 100)
        pitch = f"{pitch_val:+d}Hz"
        logger.info(f"🎲 [{request_id}] Random: voice={voice}, rate={rate}, pitch={pitch}")
    else:
        if not voice: voice = DEFAULT_VOICE
        if not rate: rate = "+0%"
        if not pitch: pitch = "+0Hz"
        logger.debug(f"🔧 [{request_id}] Параметры: voice={voice}, rate={rate}, pitch={pitch}")

    # 2. Проверка кэша (только если НЕ рандом)
    if not randomize:
        cache_key = hashlib.md5(f"{text}_{voice}_{rate}_{pitch}".encode()).hexdigest()
        cached_path = get_cached_audio_path(cache_key)
        if cached_path:
            logger.info(f"✅ [{request_id}] Кэш-хит! Возвращаем из файла")
            return FileResponse(cached_path, media_type="audio/mpeg", filename="tts.mp3")
    else:
        cache_key = None

    try:
        if len(text) > 1000:
            logger.warning(f"⚠️ [{request_id}] Текст слишком длинный: {len(text)} символов")
            raise HTTPException(status_code=400, detail="Текст слишком длинный (макс 1000 символов)")

        logger.info(f"🎤 [{request_id}] Генерация: voice={voice}")
        
        audio_bytes = await asyncio.wait_for(
            generate_edge_tts(text, voice, rate, pitch),
            timeout=30.0
        )
        
        if cache_key:
            asyncio.create_task(asyncio.to_thread(save_to_cache, cache_key, audio_bytes))
        
        logger.info(f"✅ [{request_id}] Успешно! Размер: {len(audio_bytes)} байт")
        
        return StreamingResponse(
            io.BytesIO(audio_bytes),
            media_type="audio/mpeg",
            headers={"Content-Disposition": "inline; filename=tts.mp3"}
        )
        
    except asyncio.TimeoutError:
        logger.error(f"❌ [{request_id}] Таймаут генерации (>30 сек)")
        raise HTTPException(status_code=504, detail="Таймаут генерации")
    except HTTPException:
        raise  # Пропускаем наши HTTP-ошибки
    except ValueError as e:
        logger.error(f"❌ [{request_id}] Ошибка валидации: {e}")
        raise HTTPException(status_code=400, detail=f"Ошибка генерации: {str(e)}")
    except Exception as e:
        logger.error(f"❌ [{request_id}] Неожиданная ошибка: {type(e).__name__}: {e}")
        raise HTTPException(status_code=500, detail=f"Ошибка сервера: {str(e)}")

@app.get("/voices")
def list_voices():
    logger.info("📋 Запрос списка голосов")
    return {
        "default": DEFAULT_VOICE,
        "random_pool_size": len(VOICE_POOL),
        "available_voices": VOICE_POOL
    }

@app.get("/")
def index():
    logger.debug("🏠 Запрос статуса сервера")
    return {
        "status": "online",
        "endpoints": {
            "/say?text=...": "Обычный режим",
            "/say?text=...&randomize=true": "🎲 Случайный голос и тон",
            "/voices": "Список голосов"
        },
        "limits": {
            "max_chars": 1000,
            "recommended_chars": 250
        }
    }

# 🧹 События старта/остановки сервера
@app.on_event("startup")
async def startup_event():
    logger.info("🚀 Сервер запускается...")
    logger.info(f"🎭 Пул голосов: {len(VOICE_POOL)}")
    logger.info(f"📁 Кэш-директория: tts_cache/")
    logger.info(f"🌐 Доступен на: http://0.0.0.0:5005")

@app.on_event("shutdown")
async def shutdown_event():
    logger.info("🛑 Сервер останавливается")

if __name__ == "__main__":
    import uvicorn
    logger.info("🔥 Прямой запуск через uvicorn.run()")
    uvicorn.run("ttsserver:app", host="0.0.0.0", port=5005, reload=False)