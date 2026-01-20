// server.js — TikTok + Twitch + YouTube → Terraria (FINAL)
import fs from 'fs';
import TelegramBot from 'node-telegram-bot-api';
import open from 'open';
import WebSocket from 'ws';
import tmi from 'tmi.js';
import express from 'express';
import crypto from 'crypto';
import * as dotenv from 'dotenv';
import {
    TikTokLiveConnection,
    WebcastEvent
} from 'tiktok-live-connector';
import {
    LiveChat
} from 'youtube-chat';
import path from 'path';
import {
    fileURLToPath
} from 'url';
import TwitchAnnouncer from './TwitchAnnouncer.js';
import yts from 'yt-search';
import getYouTubeId from 'get-youtube-id';
import fetch from 'node-fetch';

dotenv.config();

/* =======================
CONFIG
======================= */

class SongQueue {
    constructor() {
        this.queue = [];
        this.current = null;
        this.lastRequest = new Map(); // антиспам
    }

    add(song, isVIP = false) {
        if (isVIP) {
            if (this.current) this.queue.unshift(song);
            else this.current = song; // сразу воспроизводим
        } else {
            this.queue.push(song);
        }
    }

    next() {
        this.current = this.queue.shift() || null;
        return this.current;
    }

    // Новый метод: получает следующий трек без удаления
    peekNext() {
        return this.queue[0] || null;
    }

    clearCurrent() {
        this.current = null;
    }

    list() {
        return this.queue.map((s, i) => `${i + 1}. ${s.title}`).join(' | ');
    }

    // Новый метод: проверяет, есть ли что-то в очереди
    isEmpty() {
        return this.queue.length === 0 && this.current === null;
    }
}

const songQueue = new SongQueue();
const telegramVIPs = new Set(); // можно заполнять вручную или динамически
const STREAMER = process.env.TWITCH_USERNAME;
const TWITCH_CLIENT_ID = process.env.TWITCH_CLIENT_ID;
const TWITCH_OAUTH = process.env.TWITCH_TOKEN;
const TIKTOK_USERNAME = process.env.TIKTOK_USERNAME;
const YT_CHANNEL_ID = process.env.YT_CHANNEL_ID; // правильный YouTube channel ID
console.log('Connecting to YouTube channel:', YT_CHANNEL_ID);
const EVENTSUB_SECRET = 'terramodsecret123';
const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const app = express();
let wss;
const tiktokLikes = new Map();
const ytMessageCache = new Set();
const YT_CACHE_LIMIT = 500;
let ytStarted = false;
const TELEGRAM_COMMAND_MAP = {
    '/song': '!song',
    '/skip': '!skip',
    '/queue': '!queue',
    '/pause': '!pause',
    '/play': '!play'
};
const VIP_FILE = './vip.json';
const TELEGRAM_CHANNEL_ID = process.env.TELEGRAM_CHANNEL_ID;
let streamAnnounced = {
    twitch: false,
    youtube: false,
    tiktok: false
};
let tgBot;
const OWNER_ID = Number(process.env.OWNER_ID);
// ===== История чата =====
const chatHistory = []; // последние 50 сообщений
const CHAT_HISTORY_LIMIT = 50;
const aggregatedGifts = new Map();
let ytRetryTimer = null;
let ytRetryDelay = 30_000; // 30 секунд
let ytLastErrorMessage = null;
let tiktokLive = false;       // отслеживаем состояние TikTok стрима
let announceMessageId = null;  // хранит ID Telegram-сообщения с единым статусом
let cachedUpload = { value: null, ts: 0 };
let twitchLiveCache = { value: null, ts: 0 };


// Статические файлы
app.use(express.static(path.join(__dirname, 'public')));
app.use(express.json());
app.listen(3000, () => {
    console.log('🌐 HTTP → :3000');
    //open('http://localhost:3000/yt-obs-debug.html');
});
/* =======================
WEBSOCKET → TERRARIA
======================= */

async function isTwitchLiveCached() {
    if (Date.now() - twitchLiveCache.ts < 30_000) {
        return twitchLiveCache.value;
    }
    const v = await isTwitchLive();
    twitchLiveCache = { value: v, ts: Date.now() };
    return v;
}

async function getUploadSpeedMbps() {
    try {
        const sizeBytes = 512 * 1024; // 512 KB
        const buffer = Buffer.alloc(sizeBytes, 'a');

        const start = Date.now();

        await fetch('https://httpbin.org/post', {
            method: 'POST',
            body: buffer,
            headers: {
                'Content-Type': 'application/octet-stream'
            },
            timeout: 8000
        });

        const durationSec = (Date.now() - start) / 1000;
        const mbps = (sizeBytes * 8) / (durationSec * 1_000_000);

        return mbps.toFixed(2);
    } catch (e) {
        return null;
    }
}

async function getCachedUploadSpeed() {
    if (Date.now() - cachedUpload.ts < 60_000) {
        return cachedUpload.value;
    }

    const v = await getUploadSpeedMbps();
    cachedUpload = { value: v, ts: Date.now() };
    return v;
}

function uploadIndicator(mbps) {
    if (!mbps) return '⚪';
    if (mbps >= 8) return '🟢';
    if (mbps >= 5) return '🟡';
    return '🔴';
}

class RetryManager {
    constructor() {
        this.timers = new Map();
        this.attempts = new Map();
    }

    async retry(key, fn, {
        delay = 30_000,
        maxDelay = 5 * 60_000,
        factor = 1.5
    } = {}) {
        if (this.timers.has(key)) return;

        const attempt = (this.attempts.get(key) || 0) + 1;
        this.attempts.set(key, attempt);

        const currentDelay = Math.min(
            Math.round(delay * Math.pow(factor, attempt - 1)),
            maxDelay
        );

        console.log(`🔁 Retry [${key}] attempt ${attempt} in ${currentDelay / 1000}s`);

        const timer = setTimeout(async () => {
            this.timers.delete(key);
            try {
                await fn();
                console.log(`✅ ${key} reconnected`);
                this.attempts.delete(key);
            } catch (err) {
                console.error(`❌ ${key} retry failed:`, err.message);
                this.retry(key, fn, { delay, maxDelay, factor });
            }
        }, currentDelay);

        this.timers.set(key, timer);
    }

    clear(key) {
        if (this.timers.has(key)) {
            clearTimeout(this.timers.get(key));
            this.timers.delete(key);
        }
        this.attempts.delete(key);
    }
}

const retryManager = new RetryManager();

// ===== Централизованное обновление статуса платформ =====
async function setPlatformStatus(platform, value) {
    switch (platform) {
        case 'tiktok':
            tiktokLive = value;
            break;
        case 'youtube':
            ytStarted = value;
            break;
        case 'twitch':
            // Twitch статус проверяется динамически через API
            break;
    }
    await updateStreamStatusMessage();
}

async function getTwitchAppToken() {
    const params = new URLSearchParams({
        client_id: process.env.TWITCH_CLIENT_ID,
        client_secret: process.env.TWITCH_CLIENT_SECRET,
        grant_type: 'client_credentials'
    });

    const res = await fetch(
        `https://id.twitch.tv/oauth2/token`,
        {
            method: 'POST',
            body: params
        }
    );
    const json = await res.json();

    if (!json.access_token) {
        throw new Error('Failed to get Twitch App token');
    }
    process.env.TWITCH_APP_ACCESS_TOKEN = json.access_token;
    console.log('🔐 Twitch App Access Token updated');
}

function formatTimeHHMMSS(date = new Date()) {
    return date.toLocaleTimeString('ru-RU', {
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit'
    });
}

function canUserSkipCurrentSong({ platform, user, userId, tags = null, role = null }) {
    // Нет трека — нечего скипать
    if (!songQueue.current) return false;

    // Модераторы / стример — всегда можно
    if (tags) {
        if (isModerator(tags) || isBroadcaster(tags)) return true;
    }

    if (role) {
        if (role === 'moderator' || role === 'broadcaster') return true;
    }

    // Обычный пользователь может скипнуть ТОЛЬКО свой трек
    return songQueue.current.requesterId === `${platform}:${userId}`;
}

function parseYouTubeMessageText(raw) {
    if (!Array.isArray(raw.message)) return '';

    return raw.message
        .map(part => {
            if (part.text) return part.text;
            if (part.emoji?.shortcode) return part.emoji.shortcode;
            return '';
        })
        .join('');
}

function getYouTubeRoles(raw) {
    return {
        isAnchor: raw.isOwner === true,
        isMod: raw.isModerator === true,
        isSubscriber: raw.isMembership === true,
        isFollower: false // YouTube не даёт follower
    };
}

function addToChatHistory(platform, data) {
    chatHistory.push({ platform, ...data });
    if (chatHistory.length > CHAT_HISTORY_LIMIT) chatHistory.shift();
}

async function isTwitchLive() {
    const res = await fetch(
        `https://api.twitch.tv/helix/streams?user_login=${STREAMER}`,
        {
            headers: {
                'Client-ID': process.env.TWITCH_APP_CLIENT_ID,
                'Authorization': `Bearer ${process.env.TWITCH_TOKEN}`
            }
        }
    );

    const json = await res.json();
    console.log('[DEBUG] Twitch streams API response:', json);
    return Array.isArray(json.data) && json.data.length > 0;
}

async function updateStreamStatusMessage() {
    try {
        const twitchLive = await isTwitchLiveCached();
        const anyLive = twitchLive || ytStarted || tiktokLive;
        const uploadMbps = anyLive ? await getCachedUploadSpeed() : null;
        const speedLine = uploadMbps
        ? `${uploadIndicator(uploadMbps)} ${uploadMbps} Mbps`
        : `⚪ n/a`;

        const status = {
            twitch: twitchLive ? '🟩' : '🟥',
            youtube: ytStarted ? '🟩' : '🟥',
            tiktok: tiktokLive ? '🟩' : '🟥'
        };

        const text = `Стрим начался 🗣️:\n\n` +
        `📤 Скорость отдачи: ${speedLine}\n\n` +    
        `🟣 Twitch ${status.twitch}\n` +
        `🔴 YouTube ${status.youtube}\n` +
        `⚫ TikTok ${status.tiktok}\n\n` +
        `💬 Чат трансляции:\n👉 https://t.me/+q9BrXnjmFCFmMmQy`;

        if (announceMessageId) {
            // Редактируем существующее сообщение
            await tgBot.editMessageText(text, {
                chat_id: TELEGRAM_CHANNEL_ID,
                message_id: announceMessageId
            });
        } else {
            // Отправляем новое сообщение
            const msg = await tgBot.sendMessage(TELEGRAM_CHANNEL_ID, text, {
                disable_web_page_preview: true
            });
            console.log(`Запущенные стримы: ${text}`);
            announceMessageId = msg.message_id;
        }
    } catch (e) {
        console.error('Telegram announce error:', e.message);
    }
}

// Загружаем VIP из файла при старте
function loadVIPs() {
    if (fs.existsSync(VIP_FILE)) {
        const data = JSON.parse(fs.readFileSync(VIP_FILE));
        data.forEach(id => telegramVIPs.add(id));
        console.log(`🌟 Загружено VIP пользователей: ${data.join(', ')}`);
    }
}

// Сохраняем VIP в файл
function saveVIPs() {
    fs.writeFileSync(VIP_FILE, JSON.stringify([...telegramVIPs]));
}

// Проверка VIP
function isVIPTelegram(userId) {
    return telegramVIPs.has(userId);
}

// Загружаем при старте сервера
loadVIPs();

function broadcastQueue() {
    broadcast({
        event: 'queue',
        data: {
            list: songQueue.queue,
            current: songQueue.current // Добавьте текущий трек для отладки
        }
    });
}

function stopYouTube(forceStop = false) {
    // forceStop = true - принудительная остановка (для команды !skip)
    // forceStop = false - обычная остановка (когда трек сам закончился)
    
    // Если принудительная остановка ИЛИ (очередь пуста И нет текущего трека)
    if (forceStop || (songQueue.queue.length === 0 && songQueue.current === null)) {
        // Отправляем пустой трек для гарантированной остановки
        broadcast({
            event: 'music',
            platform: 'system',
            data: {
                videoId: '',
                author: '',
                title: ''
            }
        });
    }
    
    // Всегда отправляем команду остановки
    broadcast({
        event: 'music_stop'
    });
}

function formatCooldown(ms) {
    const totalSec = Math.ceil(ms / 1000);
    const min = Math.floor(totalSec / 60);
    const sec = totalSec % 60;

    if (min > 0 && sec > 0) return `${min} мин ${sec} сек`;
    if (min > 0) return `${min} мин`;
    return `${sec} сек`;
}

function pauseYouTube() {
    broadcast({
        event: 'music_pause'
    });
}

function resumeYouTube() {
    broadcast({
        event: 'music_play'
    });
}

function broadcast(event) {
    if (!wss) return;
    const msg = JSON.stringify(event);
    console.log('📤 Broadcasting to Terraria:', event); // Добавьте эту строку для отладки
    wss.clients.forEach(c => c.readyState === WebSocket.OPEN && c.send(msg));
}

function emit(event, platform, data = {}) {
    if (event === 'chat') {
        data.timestamp = Date.now();                 // ⏱ unix
        data.time = formatTimeHHMMSS();              // ⌚ HH:MM:SS
        addToChatHistory(platform, data);
    }

    broadcast({
        event,
        platform,
        data
    });
}

function playYouTube(song) {
    if (!song) return;
    
    songQueue.current = song;
    
    broadcast({
        event: 'music',
        platform: 'system',
        data: {
            videoId: song.videoId,
            author: song.author,
            title: song.title,
            requester: song.requester,
            duration: song.duration
        }
    });
}

function extractYouTubeID(input) {
    try {
        return getYouTubeId(input) || null;
    } catch (err) {
        console.error('Error extracting YouTube ID:', err);
        return null;
    }
}

function formatNickname(platform, nickname, userId = null) {
    if (platform === 'tiktok' && userId && tiktokLikes.has(userId)) {
        return `[TikTok] ${nickname} ❤️×${tiktokLikes.get(userId)}`;
    }

    switch (platform) {
        case 'tiktok':
            return `[TikTok] ${nickname}`;
        case 'youtube':
            return `[YouTube] ${nickname}`;
        case 'twitch':
            return `[Twitch] ${nickname}`;
        default:
            return nickname;
    }
}

// Функции проверки прав
function isSubscriber(tags) {
    return tags.subscriber || false;
}

function isModerator(tags) {
    return tags.mod || false;
}

function isBroadcaster(tags) {
    return tags.badges?.broadcaster === '1' || tags.username === STREAMER;
}

function isVIP(tags) {
    return tags.badges?.vip === '1';
}

function hasModeratorPrivileges(tags) {
    return isModerator(tags) || isBroadcaster(tags) || isVIP(tags);
}

function canSkipOrStop(tags) {
    return isModerator(tags) || isBroadcaster(tags);
}

function canRequestSongs(tags) {
    return true;
}

function getUnifiedCooldown({
    isAnchor = false,
    isMod = false,
    isSubscriber = false,
    isFollower = false
}) {
    if (isAnchor || isMod) return 0;
    if (isSubscriber) return 1 * 60 * 1000;
    if (isFollower) return 1 * 60 * 1000;
    return 1 * 60 * 1000;
}

function getTikTokCooldown(userId, {
    isAnchor = false,
    isMod = false,
    isSubscriber = false,
    isFollower = false
}) {
    if (isAnchor || isMod) return 0;
    if (isSubscriber) return 1 * 60 * 1000;
    if (isFollower) return 1 * 60 * 1000; // 👈 фолловеры
    return 1 * 60 * 1000;
}

async function handleSongRequest({
    platform,
    user,
    userId,
    text,
    cooldownMs,
    isAllowed = true
}) {
    if (!isAllowed) return;
    const query = text.slice(6).trim();
    if (!query) return;
    const last = songQueue.lastRequest.get(`${platform}:${user}`) || 0;
    const now = Date.now();
    if (cooldownMs > 0 && now - last < cooldownMs) {
        const remainingMs = cooldownMs - (now - last);
        emit('chat', platform, {
            userId,
            nickname: user,
            text: `⏳ ${user}, сможешь заказать ещё через ⏱: ${formatCooldown(remainingMs)}`
        });
        return;
    }
    let foundVideo;
    const videoId = extractYouTubeID(query);
    try {
        if (videoId) {
            const r = await yts({ videoId });
            foundVideo = r.video || r;
        } else {
            const r = await yts({ query });
            foundVideo = r.videos?.[0];
        }
    } catch {
        return;
    }

    if (!foundVideo) return;
    if (foundVideo.seconds > 10 * 60) return;

    songQueue.lastRequest.set(`${platform}:${user}`, now);

    const song = {
        requesterId: `${platform}:${userId}`,
        requester: user,
        title: foundVideo.title,
        videoId: foundVideo.videoId,
        author: foundVideo.author?.name || 'Unknown',
        duration: foundVideo.seconds || 0 // ⏱ ДЛИТЕЛЬНОСТЬ В СЕКУНДАХ
    };

    songQueue.add(song, isVIPTelegram(userId));

    emit('chat', platform, {
        userId,
        nickname: user,
        text: `🎵 Добавлено: ${song.author} — ${song.title}`
    });

    if (!songQueue.current && !songQueue.isEmpty()) {
        const nextSong = songQueue.next();
        if (nextSong) playYouTube(nextSong);
    } else {
        broadcastQueue();
    }
}

/* =======================
TWITCH EVENTSUB
======================= */

function verifyTwitchSignature(req) {
    const message =
        req.get('Twitch-Eventsub-Message-Id') +
        req.get('Twitch-Eventsub-Message-Timestamp') +
        JSON.stringify(req.body);
    const expected =
        'sha256=' +
        crypto.createHmac('sha256', EVENTSUB_SECRET).update(message).digest('hex');
    return expected === req.get('Twitch-Eventsub-Message-Signature');
}

/* =======================
MAIN
======================= */

async function main() {
    /* ---------- WS → Terraria ---------- */
    wss = new WebSocket.Server({
        port: 21214
    });
    console.log('✅ Terraria WS → ws://localhost:21214');
    await getTwitchAppToken();
    wss.on('connection', ws => {
        ws.send(JSON.stringify({ event: 'chatHistory', data: chatHistory }));

        ws.on('message', message => {
            try {
                const d = JSON.parse(message);
                if (d.event === 'trackEnded') {
                    const next = songQueue.next();
                    if (next) playYouTube(next);
                    else stopYouTube(false);
                    broadcastQueue();
                }
            } catch (err) {
                console.error('WS message error:', err);
            }
        });

        ws.on('close', (code) => {
            if (code !== 1000) {
                console.log('⚠ WS disconnected:', code);
            }
        });
    });

    /* ---------- HTTP (Twitch EventSub) ---------- */
    app.post('/twitch/eventsub', (req, res) => {
        const type = req.get('Twitch-Eventsub-Message-Type');

        if (type === 'webhook_callback_verification')
            return res.send(req.body.challenge);

        if (!verifyTwitchSignature(req))
            return res.status(403).end();

        if (type === 'notification') {
            const {
                subscription,
                event
            } = req.body;

            switch (subscription.type) {
                case 'channel.follow':
                    emit('follow', 'twitch', {
                        userId: event.user_id,
                        nickname: formatNickname('twitch', event.user_name)
                    });
                    break;

                case 'channel.subscribe':
                    emit('subscribe', 'twitch', {
                        userId: event.user_id,
                        nickname: formatNickname('twitch', event.user_name)
                    });
                    break;

                case 'channel.subscription.gift':
                    emit('gift', 'twitch', {
                        userId: event.user_id,
                        nickname: formatNickname('twitch', event.user_name),
                        amount: event.total
                    });
                    break;
            }
        }

        res.status(200).end();
    });

    async function getTelegramRole(msg) {
        // Личка = broadcaster
        if (msg.chat.type === 'private' && msg.from.id === OWNER_ID) {
            return 'broadcaster';
        }

        try {
            const member = await tgBot.getChatMember(
                msg.chat.id,
                msg.from.id
            );

            if (member.status === 'creator') return 'broadcaster';
            if (member.status === 'administrator') return 'moderator';

        } catch (e) {
            console.error('TG role check error:', e.message);
        }

        return 'user';
    }

    /* ---------- Telegram Bot ---------- */
    try {
        tgBot = new TelegramBot(process.env.TG_BOT_TOKEN, {
            polling: true
        });

        console.log('✅ Telegram Bot connected');
        setTimeout(() => {
            emit('chat', 'telegram', {
                userId: `system`,
                nickname: `Telegram`,
                text: '✅ Telegram Bot connected'
            });
        }, 2000);

        tgBot.on('message', async msg => {
            if (!msg.text) return;
            const chatId = msg.chat.id;   // ✅ ДОБАВИТЬ
            const fromId = msg.from.id;   // ✅ ДОБАВИТЬ
            const userId = fromId;        // можно оставить для читаемости
            const user = msg.from.username || msg.from.first_name;
            let text = msg.text.trim();
            const role = await getTelegramRole(msg);

            // === Команды управления VIP ===
            if (role === 'broadcaster' || role === 'moderator') {
                // Добавить VIP
                if (text.startsWith('/vip ')) {
                    const targetId = parseInt(text.split(' ')[1]);
                    if (!isNaN(targetId)) {
                        telegramVIPs.add(targetId);
                        saveVIPs();
                        tgBot.sendMessage(chatId, `✅ Пользователь ${targetId} теперь VIP!`);
                    } else {
                        tgBot.sendMessage(chatId, `❌ Неверный ID`);
                    }
                    return;
                }

                // Удалить VIP
                if (text.startsWith('/unvip ')) {
                    const targetId = parseInt(text.split(' ')[1]);
                    if (!isNaN(targetId) && telegramVIPs.has(targetId)) {
                        telegramVIPs.delete(targetId);
                        saveVIPs();
                        tgBot.sendMessage(chatId, `❌ Пользователь ${targetId} больше не VIP`);
                    } else {
                        tgBot.sendMessage(chatId, `❌ Пользователь не найден в VIP`);
                    }
                    return;
                }

                // Список VIP
                if (text === '/viplist') {
                    if (telegramVIPs.size === 0) {
                        tgBot.sendMessage(chatId, `VIP-пользователей нет`);
                    } else {
                        tgBot.sendMessage(chatId, `🌟 VIP:\n${[...telegramVIPs].join('\n')}`);
                    }
                    return;
                }
            }

            // --- Telegram → обычные команды ---
            for (const tgCmd in TELEGRAM_COMMAND_MAP) {
                if (text === tgCmd || text.startsWith(tgCmd + ' ')) {
                    text = text.replace(tgCmd, TELEGRAM_COMMAND_MAP[tgCmd]);
                    break;
                }
            }

            /* ===== SONG REQUEST ===== */
            if (text.startsWith('!song ')) {
                // VIP обходит кулдаун
                const cooldownMs = isVIPTelegram(fromId) ? 0 : getUnifiedCooldown({
                    isAnchor: role === 'broadcaster',
                    isMod: role === 'moderator',
                    isSubscriber: false,
                    isFollower: false
                });

                await handleSongRequest({
                    platform: 'telegram',
                    user: msg.from.username || msg.from.first_name,
                    userId: fromId,
                    role,
                    text,
                    cooldownMs
                });
                return;
            }

            /* ===== SKIP ===== */
            if (text === '!skip') {
                const allowed = canUserSkipCurrentSong({
                    platform: 'telegram',
                    user,
                    userId,
                    role
                });
                if (!allowed) {
                    await tgBot.sendMessage(
                        TELEGRAM_CHANNEL_ID,
                        `❌ ${user}, ты можешь скипать только свой текущий трек`,
                        { disable_web_page_preview: false }
                    );
                    return;
                }

                stopYouTube(true);
                songQueue.current = null;

                const next = songQueue.next();
                if (next) playYouTube(next);

                broadcastQueue();
                return;
            }

            /* ===== PAUSE ===== */
            if (text === '!pause' || text === '!play') {
                if (role === 'user') {
                    tgBot.sendMessage(msg.chat.id, '⛔ Недостаточно прав');
                    return;
                }

                text === '!pause'
                    ? pauseYouTube()
                    : resumeYouTube();

                return;
            }

            /* ===== обычный чат ===== */
            emit('chat', 'telegram', {
                userId,
                nickname: `[TG] ${user}`,
                text
            });
        });

    } catch (err) {
        console.error('⚠ Telegram connection failed:', err.message);
        setTimeout(() => {
            emit('chat', 'telegram', {
                userId: `system`,
                nickname: `Telegram`,
                text: `⚠ Telegram connection failed: ${err.message}`
            });
        }, 2000);
    }

    /* ---------- Twitch Chat ---------- */
    try {
        const twitch = new tmi.Client({
            identity: {
                username: STREAMER,
                password: TWITCH_OAUTH
            },
            channels: [STREAMER]
        });
        const twitchSeen = new Set();
        await twitch.connect();
        const announcer = new TwitchAnnouncer(twitch, STREAMER);
        setInterval(() => {
            announcer.sendRandom();
        }, 10 * 60 * 1000);
        console.log('✅ Twitch Chat connected');
        await updateStreamStatusMessage();
        emit('chat', 'twitch', {
            userId: `system`,
            nickname: `Twitch`,
            text: `✅ Twitch Chat connected`
        });

        twitch.on('message', async (_, tags, msg, self) => {
            if (self) return;

            const user = tags.username;
            const text = msg.trim();

            // ===== SONG REQUEST =====
            if (text.startsWith('!song ')) {
                const cooldownMs = getUnifiedCooldown({
                    isAnchor: isBroadcaster(tags),
                    isMod: isModerator(tags),
                    isSubscriber: isSubscriber(tags),
                    isFollower: false // Twitch follower через чат не определить
                });

                await handleSongRequest({
                    platform: 'twitch',
                    user,
                    userId: tags['user-id'],
                    text,
                    cooldownMs
                });

                return;
            }

            // ===== SKIP =====
            if (text === '!skip') {
                // Проверяем права на использование команды
                const allowed = canUserSkipCurrentSong({
                    platform: 'twitch',
                    user,
                    userId: tags['user-id'],
                    tags
                });

                if (!allowed) {
                    twitch.say(
                        STREAMER,
                        `❌ ${user}, ты можешь скипать только свой текущий трек`
                    );
                    return;
                }

                // Останавливаем текущий трек с флагом принудительной остановки
                stopYouTube(true);
                // Очищаем текущий трек
                songQueue.current = null;
                // Получаем следующий трек из очереди
                const next = songQueue.next();
    
                if (next) {
                    // Если есть следующий трек - воспроизводим его
                    playYouTube(next);
                    twitch.say(
                        STREAMER,
                        `⏭ Следующий трек: ${next.author} — ${next.title}`
                    );
                } else {
                    // Если очередь пуста
                    twitch.say(STREAMER, `⏹ Очередь пуста, воспроизведение остановлено`);
                }
    
                // Обновляем очередь
                broadcastQueue();
                return;
            }

            // ===== QUEUE =====
            if (text === '!queue') {
                if (songQueue.queue.length > 0) {
                    const list = songQueue.list();
                    const current = songQueue.current ? 
                        `🎶 Сейчас: ${songQueue.current.author} — ${songQueue.current.title}\n📜 Очередь: ${list}` :
                        `📜 Очередь: ${list}`;
        
                    // Разбиваем на части, если слишком длинное
                    if (current.length > 400) {
                        twitch.say(STREAMER, current.substring(0, 400));
                        if (current.length > 400) {
                            setTimeout(() => {
                                twitch.say(STREAMER, current.substring(400, 800));
                            }, 500);
                        }
                    } else {
                        twitch.say(STREAMER, current);
                    }
                } else {
                    if (songQueue.current) {
                        twitch.say(
                            STREAMER,
                            `🎶 Сейчас: ${songQueue.current.author} — ${songQueue.current.title}\n📭 Очередь пуста`
                        );
                    } else {
                        twitch.say(STREAMER, `📭 Очередь пуста`);
                    }
                }
                return;
            }

            // ===== STOP (опционально) =====
            if (text === '!stop') {
                // Проверяем права на использование команды
                if (!canSkipOrStop(tags)) {
                    twitch.say(STREAMER, `❌ ${user}, команда !stop доступна только модераторам и стримеру!`);
                    return;
                }

                stopYouTube();
                songQueue.clearCurrent();
                songQueue.queue = []; // Очищаем всю очередь
                songQueue.lastRequest.clear(); // Очищаем таймеры антиспама
                broadcastQueue();
                twitch.say(STREAMER, `⏹ Воспроизведение остановлено, очередь очищена`);
                return;
            }

            // ===== PAUSE =====
            if (text === '!pause') {
                if (!canSkipOrStop(tags)) {
                    twitch.say(STREAMER, `❌ ${user}, команда !pause доступна только модераторам и стримеру!`);
                    return;
                }

                pauseYouTube();
                twitch.say(STREAMER, `⏸ Трек поставлен на паузу`);
                return;
            }

            // ===== PLAY =====
            if (text === '!play') {
                if (!canSkipOrStop(tags)) {
                    twitch.say(STREAMER, `❌ ${user}, команда !play доступна только модераторам и стримеру!`);
                    return;
                }

                resumeYouTube();
                twitch.say(STREAMER, `▶️ Продолжаем воспроизведение`);
                return;
            }

            // ===== обычный чат =====
            if (!twitchSeen.has(user)) {
                twitchSeen.add(user);
                if (twitchSeen.size > 1000) {
                    const first = twitchSeen.values().next().value;
                    twitchSeen.delete(first);
                }
                emit('join', 'twitch', {
                    userId: tags['user-id'],
                    nickname: formatNickname('twitch', user)
                });
            }

            emit('chat', 'twitch', {
                userId: tags['user-id'],
                nickname: formatNickname('twitch', user),
                text: msg
            });
        });

        twitch.on('cheer', (_, u) => {
            emit('gift', 'twitch', {
                userId: u['user-id'],
                nickname: formatNickname('twitch', u.username),
                amount: u.bits
            });
        });

        twitch.on('raided', (_, raider) =>
            emit('chat', 'twitch', {
                userId: raider.username,
                nickname: formatNickname('twitch', raider.username),
                text: `[РЕЙД] ${raider.viewers} зрителей`
            })
        );
        twitch.on('disconnected', async (reason) => {
            console.error('⚠ Twitch disconnected:', reason);
            retryManager.retry('twitch-chat', async () => {
                await twitch.connect();
                console.log('✅ Twitch chat reconnected');
            });
        });
    } catch (err) {
        console.error('⚠ Twitch connection failed:', err.message);
        emit('chat', 'twitch', {
            userId: `system`,
            nickname: `Twitch`,
            text: `⚠ Twitch connection failed: ${err.message}`
        });
    }

    /* ---------- TikTok ---------- */
    async function connectTikTok() {
        try {
            const tt = new TikTokLiveConnection(TIKTOK_USERNAME, {
                enableExtendedGiftInfo: true
            });
            await tt.connect();
            await setPlatformStatus('tiktok', true);
            console.log('✅ TikTok connected');
            emit('chat', 'tiktok', {
                userId: `system`,
                nickname: `TikTok`,
                text: `Connected`
            });

            tt.on(WebcastEvent.MEMBER, d => {
                if (!tiktokLikes.has(d.user.userId)) {
                    tiktokLikes.set(d.user.userId, 0);
                }
                emit('join', 'tiktok', {
                    userId: d.user.userId,
                    nickname: formatNickname('tiktok', d.user.nickname, d.user.userId)
                })
            });

            tt.on(WebcastEvent.CHAT, d => {
                const text = d.comment;
                const userId = d.user.userId;
                const user = d.user.nickname;

                // ⚡ Новый способ определения ролей
                const identity = d.userIdentity || {};
                const isAnchor = identity.isAnchor || false;
                const isMod = identity.isModeratorOfAnchor || isAnchor;
                const isSubscriber = identity.isSubscriberOfAnchor || false;
                const isFollower = Boolean(identity.isFollower);

                // Проверка команд на модератора
                const canSkipStop = isMod;

                // ===== SONG REQUEST =====
                if (text.startsWith('!song ')) {
                    const cooldownMs = getTikTokCooldown(userId, {
                        isAnchor,
                        isMod,
                        isSubscriber,
                        isFollower
                    });

                    handleSongRequest({
                        platform: 'tiktok',
                        user,
                        userId,
                        text,
                        cooldownMs
                    });
                    return;
                }

                // ===== SKIP =====
                if (text === '!skip') {
                    const allowed = canUserSkipCurrentSong({
                        platform: 'tiktok',
                        user,
                        userId,
                        role: isMod ? 'moderator' : isAnchor ? 'broadcaster' : 'user'
                    });
                    if (!allowed) {
                        emit('chat', 'tiktok', {
                            userId,
                            nickname: formatNickname('tiktok', user, userId),
                            text: `❌ ${user}, ты можешь скипать только свой текущий трек`
                        });
                        return; // 🔴 ОБЯЗАТЕЛЬНО
                    }

                    stopYouTube(true);
                    songQueue.current = null;
                    const next = songQueue.next();
                    if (next) playYouTube(next);

                    broadcastQueue();
                    emit('chat', 'tiktok', {
                        userId,
                        nickname: formatNickname('tiktok', user, userId),
                        text: next
                            ? `⏭ Следующий трек: ${next.author} — ${next.title}`
                            : `⏹ Очередь пуста, воспроизведение остановлено`
                    });
                    return;
                }

                // ===== STOP =====
                if (text === '!stop') {
                    if (!canSkipStop) {
                        emit('chat', 'tiktok', {
                            userId,
                            nickname: formatNickname('tiktok', user, userId),
                            text: `❌ ${user}, команда !stop доступна только модераторам и стримеру!`
                        });
                        return;
                    }

                    stopYouTube();
                    songQueue.clearCurrent();
                    songQueue.queue = [];
                    songQueue.lastRequest.clear();
                    broadcastQueue();

                    emit('chat', 'tiktok', {
                        userId,
                        nickname: formatNickname('tiktok', user, userId),
                        text: `⏹ Воспроизведение остановлено, очередь очищена`
                    });
                    return;
                }

                // ===== PAUSE =====
                if (text === '!pause') {
                    if (!canSkipStop) {
                        emit('chat', 'tiktok', {
                            userId,
                            nickname: formatNickname('tiktok', user, userId),
                            text: `❌ Команда !pause доступна только модераторам и стримеру`
                        });
                        return;
                    }

                    pauseYouTube();
                    emit('chat', 'tiktok', {
                        userId,
                        nickname: formatNickname('tiktok', user, userId),
                        text: `⏸ Трек поставлен на паузу`
                    });
                    return;
                }

                // ===== PLAY =====
                if (text === '!play') {
                    if (!canSkipStop) {
                        emit('chat', 'tiktok', {
                            userId,
                            nickname: formatNickname('tiktok', user, userId),
                            text: `❌ Команда !play доступна только модераторам и стримеру`
                        });
                        return;
                    }

                    resumeYouTube();
                    emit('chat', 'tiktok', {
                        userId,
                        nickname: formatNickname('tiktok', user, userId),
                        text: `▶️ Продолжаем воспроизведение`
                    });
                    return;
                }

                // ===== обычный чат =====
                emit('chat', 'tiktok', {
                    userId,
                    nickname: formatNickname('tiktok', user, userId),
                    text
                });
            });

            tt.on(WebcastEvent.GIFT, d => {
                const giftName =
                    d.giftDetails?.giftName ||
                    d.extendedGiftInfo?.name ||
                    'Подарок';

                const giftIconUri =
                    d.giftDetails?.giftIcon?.uri ||
                    d.extendedGiftInfo?.icon?.uri;

                const giftIcon = giftIconUri
                    ? `https://p16-webcast.tiktokcdn.com/img/maliva/${giftIconUri}~tplv-obj.webp`
                    : null;

                broadcast({
                    event: 'gift',
                    platform: 'tiktok',
                    data: {
                        userId: d.user.userId,
                        nickname: d.user.nickname,
                        gift: {
                            name: giftName,
                            icon: giftIcon
                        },
                        amount: d.repeatCount || 1
                    }
                });
            });

            tt.on(WebcastEvent.LIKE, d => {
                const userId = d.user.userId;
                const prev = tiktokLikes.get(userId) || 0;
                const total = prev + d.likeCount;
                tiktokLikes.set(userId, total);
                emit('like', 'tiktok', {
                    userId,
                    nickname: d.user.nickname,
                    amount: d.likeCount
                });
            });

            tt.on(WebcastEvent.FOLLOW, d => {
                if (!tiktokLikes.has(d.user.userId)) {
                    tiktokLikes.set(d.user.userId, 0);
                }
                emit('follow', 'tiktok', {
                    userId: d.user.userId,
                    nickname: formatNickname('tiktok', d.user.nickname, d.user.userId)
                })
            });

            tt.on(WebcastEvent.SHARE, d => {
                if (!tiktokLikes.has(d.user.userId)) {
                    tiktokLikes.set(d.user.userId, 0);
                }
                emit('share', 'tiktok', {
                    userId: d.user.userId,
                    nickname: formatNickname('tiktok', d.user.nickname, d.user.userId)
                })
            });

            tt.on(WebcastEvent.SUBSCRIBE, d => {
                if (!tiktokLikes.has(d.user.userId)) {
                    tiktokLikes.set(d.user.userId, 0);
                }
                emit('subscribe', 'tiktok', {
                    userId: d.user.userId,
                    nickname: formatNickname('tiktok', d.user.nickname, d.user.userId)
                })
            });
        } catch (err) {
            console.error('⚠ TikTok connection failed:', err.message);
            await setPlatformStatus('tiktok', false);
            retryManager.retry('tiktok', connectTikTok);

        }
    }

    await connectTikTok();

    /* ---------- YouTube Chat ---------- */
    try {
        const yt = new LiveChat({
            channelId: YT_CHANNEL_ID
        });

        yt.on('start', async () => {
            ytLastErrorMessage = null;
            console.log('✅ YouTube Live Chat started');
            // Обновляем Telegram / внутренний статус
            await setPlatformStatus('youtube', true);
            emit('chat', 'youtube', {
                userId: `system`,
                nickname: `YouTube`,
                text: `✅ YouTube Live Chat started`
            });
        });

        yt.on('end', async () => {
            console.warn('⚠ YouTube Live Chat ended');
            await setPlatformStatus('youtube', false);
            retryManager.retry('youtube', async () => {
                await yt.start();
                // статус выставится уже в обработчике 'start'
            });
        });

        yt.on('error', async (err) => {
            console.error('⚠ YouTube error:', err.message);
            await setPlatformStatus('youtube', false);
            // Пробуем переподключиться через 30 секунд
            retryManager.retry(
                'youtube',
                async () => {
                    console.log('🔄 Retrying YouTube Live Chat connection...');
                    await yt.start();
                },
                { delay: 30_000 }
            );
        });

        console.log('🔄 Starting YouTube Live Chat...');
        await yt.start(); // если упадёт, перейдёт в catch

        yt.on('chat', chatItem => {
            console.log('📦 YT RAW MESSAGE:', JSON.stringify(chatItem, null, 2));

            const msgId = chatItem.id;
            if (ytMessageCache.has(msgId)) return;

            ytMessageCache.add(msgId);
            if (ytMessageCache.size > YT_CACHE_LIMIT) {
                const first = ytMessageCache.values().next().value;
                ytMessageCache.delete(first);
            }

            const userId = chatItem.author?.channelId;
            const username = chatItem.author?.name || 'YouTubeUser';

            const messageText = parseYouTubeMessageText(chatItem);
            if (!messageText) return;

            const {
                isAnchor,
                isMod,
                isSubscriber,
                isFollower
            } = getYouTubeRoles(chatItem);

            /* ===== SONG REQUEST ===== */
            if (messageText.startsWith('!song ')) {
                const cooldownMs = getUnifiedCooldown({
                    isAnchor,
                    isMod,
                    isSubscriber,
                    isFollower
                });

                handleSongRequest({
                    platform: 'youtube',
                    user: username,
                    userId,
                    text: messageText,
                    cooldownMs
                });
                return;
            }

            /* ===== SKIP ===== */
            if (messageText === '!skip') {
                const allowed = canUserSkipCurrentSong({
                    platform: 'youtube',
                    user: username,
                    userId,
                    role: isAnchor ? 'broadcaster' : isMod ? 'moderator' : 'user'
                });
                if (!allowed) {
                    emit('chat', 'youtube', {
                        userId,
                        nickname: formatNickname('youtube', username),
                        text: `❌ ${username}, ты можешь скипать только свой текущий трек`
                    });
                    return;
                }

                stopYouTube(true);
                songQueue.current = null;

                const next = songQueue.next();
                if (next) playYouTube(next);

                broadcastQueue();

                emit('chat', 'youtube', {
                    userId,
                    nickname: formatNickname('youtube', username),
                    text: next
                        ? `⏭ Следующий трек: ${next.author} — ${next.title}`
                        : `⏹ Очередь пуста, воспроизведение остановлено`
                });
                return;
            }

            /* ===== STOP ===== */
            if (messageText === '!stop') {
                if (!isAnchor && !isMod) {
                    emit('chat', 'youtube', {
                        userId,
                        nickname: formatNickname('youtube', username),
                        text: `❌ ${username}, команда !stop доступна только модераторам и стримеру`
                    });
                    return;
                }

                stopYouTube();
                songQueue.clearCurrent();
                songQueue.queue = [];
                songQueue.lastRequest.clear();

                broadcastQueue();

                emit('chat', 'youtube', {
                    userId,
                    nickname: formatNickname('youtube', username),
                    text: `⏹ Воспроизведение остановлено, очередь очищена`
                });
                return;
            }

            /* ===== PAUSE ===== */
            if (messageText === '!pause') {
                if (!isAnchor && !isMod) {
                    emit('chat', 'youtube', {
                        userId,
                        nickname: formatNickname('youtube', username),
                        text: `❌ Команда !pause доступна только модераторам и стримеру`
                    });
                    return;
                }

                pauseYouTube();
                emit('chat', 'youtube', {
                    userId,
                    nickname: formatNickname('youtube', username),
                    text: `⏸ Трек поставлен на паузу`
                });
                return;
            }

            /* ===== PLAY ===== */
            if (messageText === '!play') {
                if (!isAnchor && !isMod) {
                    emit('chat', 'youtube', {
                        userId,
                        nickname: formatNickname('youtube', username),
                        text: `❌ Команда !play доступна только модераторам и стримеру`
                    });
                    return;
                }

                resumeYouTube();
                emit('chat', 'youtube', {
                    userId,
                    nickname: formatNickname('youtube', username),
                    text: `▶️ Продолжаем воспроизведение`
                });
                return;
            }

            /* ===== обычный чат ===== */
            emit('chat', 'youtube', {
                userId,
                nickname: formatNickname('youtube', username),
                text: messageText
            });
        });

        yt.on('superchat', scItem => {
            emit('gift', 'youtube', {
                userId: scItem.author.channelId,
                nickname: formatNickname('youtube', scItem.author.name),
                amount: scItem.amount
            });
        });

        yt.on('membership', m =>
            emit('follow', 'youtube', {
                userId: m.author.channelId,
                nickname: formatNickname('youtube', m.author.name)
            })
        );
    } catch (err) {
        console.error('⚠ YouTube connection failed:', err.message);
        emit('chat', 'youtube', {
            userId: `system`,
            nickname: `YouTube`,
            text: `⚠ YouTube connection failed`
        });
    }

    // 🔄 Обновление статуса каждые 30 секунд
    setInterval(async () => {
        try {
            await updateStreamStatusMessage();
        } catch (err) {
            console.error('⚠ Telegram updateStreamStatusMessage failed:', err.message);
        }
    }, 30_000);

}

main().catch(console.error);