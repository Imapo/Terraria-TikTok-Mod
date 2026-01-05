// server.js — TikTok + Twitch + YouTube → Terraria (FINAL)

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

    add(song) {
        this.queue.push(song);
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
const STREAMER = process.env.TWITCH_USERNAME;
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


// Статические файлы
app.use(express.static(path.join(__dirname, 'public')));
app.use(express.json());
app.listen(3000, () => {
    console.log('🌐 HTTP → :3000');
    open('http://localhost:3000/yt-obs-debug.html');
});
/* =======================
WEBSOCKET → TERRARIA
======================= */

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
    if (isFollower) return 5 * 60 * 1000;
    return 10 * 60 * 1000;
}

function getTikTokCooldown(userId, {
    isAnchor = false,
    isMod = false,
    isSubscriber = false,
    isFollower = false
}) {
    if (isAnchor || isMod) return 0;
    if (isSubscriber) return 1 * 60 * 1000;
    if (isFollower) return 5 * 60 * 1000; // 👈 фолловеры
    return 10 * 60 * 1000;
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
        const sec = Math.ceil((cooldownMs - (now - last)) / 1000);
        emit('chat', platform, {
            userId,
            nickname: user,
            text: `⏳ Подожди ${Math.ceil(sec / 60)} мин перед следующим заказом`
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
        user,
        requester: user,
        title: foundVideo.title,
        videoId: foundVideo.videoId,
        author: foundVideo.author?.name || 'Unknown',
        duration: foundVideo.seconds || 0 // ⏱ ДЛИТЕЛЬНОСТЬ В СЕКУНДАХ
    };

    songQueue.add(song);

    emit('chat', platform, {
        userId,
        nickname: user,
        text: `🎵 Добавлено: ${song.author} — ${song.title}`
    });

    if (!songQueue.current) {
        playYouTube(songQueue.next());
    } else {
        broadcastQueue();
    }
}

function getCooldownForUser(user, tags) {
    // Для стримера и модераторов - без кулдауна
    if (isBroadcaster(tags) || isModerator(tags)) {
        return 0;
    }
    // Для VIP - уменьшенный кулдаун
    if (isVIP(tags)) {
        return 0; // 0 секунд
    }
    // Для подписчиков - стандартный кулдаун
    if (isSubscriber(tags)) {
    const tier = tags['badges']?.subscriber || '1';
    switch(tier) {
        case '3000': // Tier 3
            return 10 * 1000; // 10 секунд
        case '2000': // Tier 2
            return 30 * 1000; // 30 секунд
        case '1000': // Tier 1
        default:
            return 60 * 1000; // 1 минута
    }
}
    // Для всех остальных - стандартный кулдаун (но они не смогут использовать !song)
    return 5 * 60 * 1000;
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
    wss.on('connection', ws => {
        ws.on('message', message => {
            try {
                const d = JSON.parse(message);

                if (d.event === 'trackEnded') {
                    console.log('Трек завершился, ищем следующий...');
                
                    // Получаем следующий трек из очереди
                    const next = songQueue.next();
                
                    if (next) {
                        console.log('Воспроизводим следующий:', next.title);
                        playYouTube(next);
                    } else {
                        console.log('Очередь пуста, останавливаем воспроизведение');
                        // Обычная остановка (без флага принудительной)
                        stopYouTube(false);
                    }
                
                    broadcastQueue();
                }
            } catch (err) {
                console.error('WS message error:', err);
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
        emit('chat', 'twitch', {
            userId: `system`,
            nickname: `Twitch`,
            text: `Connected`
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
                if (!canSkipOrStop(tags)) {
                    twitch.say(STREAMER, `❌ ${user}, команда !skip доступна только модераторам и стримеру!`);
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
    } catch (err) {
        console.error('⚠ Twitch connection failed:', err.message);
        emit('chat', 'twitch', {
            userId: `system`,
            nickname: `Twitch`,
            text: `⚠ Twitch connection failed: ${err.message}`
        });
    }

    /* ---------- TikTok ---------- */
    try {
        const tt = new TikTokLiveConnection(TIKTOK_USERNAME, {
            enableExtendedGiftInfo: true
        });
        await tt.connect();
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
                if (!canSkipStop) {
                    emit('chat', 'tiktok', {
                        userId,
                        nickname: formatNickname('tiktok', user, userId),
                        text: `❌ ${user}, команда !skip доступна только модераторам и стримеру!`
                    });
                    return;
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
            const userId = d.user.userId;
            const baseName = d.user.nickname;
            // Основные источники: giftDetails + extendedGiftInfo
            const giftName =
                d.giftDetails?.giftName ||
                d.extendedGiftInfo?.name ||
                'Подарок';
            // Иконка подарка — строим полный URL
            let giftIconUri =
                d.giftDetails?.icon?.uri ||
                d.extendedGiftInfo?.icon?.uri ||
                null;
            // TikTok CDN требует базовый URL
            const giftIcon = giftIconUri ?
                `https://p16-webcast.tiktokcdn.com/img/maliva/${giftIconUri}` + `~tplv-obj.webp` :
                null;
            // Добавляем пользователя в Map лайков, если ещё нет
            if (!tiktokLikes.has(userId)) tiktokLikes.set(userId, 0);
            // Отправляем через WebSocket
            emit('gift', 'tiktok', {
                userId,
                nickname: formatNickname('tiktok', baseName, userId),
                gift: {
                    name: giftName,
                    icon: giftIcon
                },
                amount: d.repeatCount || 1
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
        emit('chat', 'tiktok', {
            userId: `system`,
            nickname: `TikTok`,
            text: `⚠ TikTok connection failed: ${err.message}`
        });

    }

    /* ---------- YouTube Chat ---------- */
    try {
        const yt = new LiveChat({
            channelId: YT_CHANNEL_ID
        });

        yt.on('start', () => {
            console.log('✅ YouTube Live Chat started');
            emit('chat', 'youtube', {
                userId: `system`,
                nickname: `YouTube`,
                text: `✅ YouTube Live Chat started`
            });
        });
        yt.on('end', () => {
            console.log('❌ YouTube Live Chat ended');
            emit('chat', 'youtube', {
                userId: `system`,
                nickname: `YouTube`,
                text: `❌ YouTube Live Chat ended`
            });
        });
        yt.on('error', err => {
            console.error('⚠ YouTube error:', err);
            ytStarted = false;
            emit('chat', 'youtube', {
                userId: `system`,
                nickname: `YouTube`,
                text: `⚠ YouTube error: ${err?.message || err}`
            });
        });

        yt.on('chat', chatItem => {
            const author = chatItem.author;
            const isAnchor = author.isChatOwner === true;
            const isMod = author.isChatModerator === true;
            const isSubscriber = author.isChatSponsor === true;
            // YouTube НЕ поддерживает follower
            const isFollower = false;
            const msgId = chatItem.id;
            if (ytMessageCache.has(msgId)) return;

            ytMessageCache.add(msgId);
            if (ytMessageCache.size > YT_CACHE_LIMIT) {
                const first = ytMessageCache.values().next().value;
                ytMessageCache.delete(first);
            }

            const userId = chatItem.author.channelId;

            let messageText = chatItem.message;
            if (Array.isArray(messageText)) {
                messageText = messageText.map(p => p.text).join('');
            }

            if (messageText.startsWith('!song ')) {
                const cooldownMs = getUnifiedCooldown({
                    isAnchor,
                    isMod,
                    isSubscriber,
                    isFollower
                });

                handleSongRequest({
                    platform: 'youtube',
                    user: chatItem.author.name,
                    userId: chatItem.author.channelId,
                    text: messageText,
                    cooldownMs
                });
                return;
            }

            if (messageText === '!skip') {
                if (!isAnchor && !isMod) {
                    emit('chat', 'youtube', {
                        userId,
                        nickname: formatNickname('youtube', chatItem.author.name),
                        text: `❌ ${chatItem.author.name}, команда !skip доступна только модераторам и стримеру!`
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
                    nickname: formatNickname('youtube', chatItem.author.name),
                    text: next
                        ? `⏭ Следующий трек: ${next.author} — ${next.title}`
                        : `⏹ Очередь пуста, воспроизведение остановлено`
                });
                return;
            }

            // ===== STOP =====
            if (messageText === '!stop') {
                if (!isAnchor && !isMod) {
                    emit('chat', 'youtube', {
                        userId,
                        nickname: formatNickname('youtube', chatItem.author.name),
                        text: `❌ ${chatItem.author.name}, команда !stop доступна только модераторам и стримеру!`
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
                    nickname: formatNickname('youtube', chatItem.author.name),
                    text: `⏹ Воспроизведение остановлено, очередь очищена`
                });
                return;
            }

            emit('chat', 'youtube', {
                userId,
                nickname: formatNickname('youtube', chatItem.author.name),
                text: messageText
            });
        });

        // ===== PAUSE =====
        if (messageText === '!pause') {
            if (!isAnchor && !isMod) {
                emit('chat', 'youtube', {
                    userId,
                    nickname: formatNickname('youtube', chatItem.author.name),
                    text: `❌ Команда !pause доступна только модераторам и стримеру`
                });
                return;
            }

            pauseYouTube();
            emit('chat', 'youtube', {
                userId,
                nickname: formatNickname('youtube', chatItem.author.name),
                text: `⏸ Трек поставлен на паузу`
            });
            return;
        }

        // ===== PLAY =====
        if (messageText === '!play') {
            if (!isAnchor && !isMod) {
                emit('chat', 'youtube', {
                    userId,
                    nickname: formatNickname('youtube', chatItem.author.name),
                    text: `❌ Команда !play доступна только модераторам и стримеру`
                });
                return;
            }

            resumeYouTube();
            emit('chat', 'youtube', {
                userId,
                nickname: formatNickname('youtube', chatItem.author.name),
                text: `▶️ Продолжаем воспроизведение`
            });
            return;
        }

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

        if (!ytStarted) {
            ytStarted = true;
            await yt.start();
        }
    } catch (err) {
        console.error('⚠ YouTube connection failed:', err.message);
        emit('chat', 'youtube', {
            userId: `system`,
            nickname: `YouTube`,
            text: `⚠ YouTube connection failed`
        });

    }

}

main().catch(console.error);