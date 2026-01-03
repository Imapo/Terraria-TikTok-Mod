// server.js — TikTok + Twitch + YouTube → Terraria (FINAL)

import open from 'open';
import WebSocket from 'ws';
import tmi from 'tmi.js';
import express from 'express';
import crypto from 'crypto';
import * as dotenv from 'dotenv';
import { TikTokLiveConnection, WebcastEvent } from 'tiktok-live-connector';
import { LiveChat } from 'youtube-chat';
import path from 'path';
import { fileURLToPath } from 'url';
import TwitchAnnouncer from './TwitchAnnouncer.js';
import yts from 'yt-search';

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

  canRequest(user) {
    const last = this.lastRequest.get(user) || 0;
    return Date.now() - last > 2 * 60 * 1000; // 2 мин
  }

  add(song) {
    this.queue.push(song);
    this.lastRequest.set(song.user, Date.now());
  }

  next() {
    this.current = this.queue.shift() || null;
    return this.current;
  }

  list() {
    return this.queue.map((s, i) => `${i + 1}. ${s.title}`).join(' | ');
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
  broadcast({ event: 'queue', data: { list: songQueue.queue } });
}

function broadcast(event) {
    if (!wss) return;
    const msg = JSON.stringify(event);
    wss.clients.forEach(c => c.readyState === WebSocket.OPEN && c.send(msg));
}

function emit(event, platform, data = {}) {
    broadcast({ event, platform, data });
}

function playYouTube(song) {
  if (!song) return;
  broadcast({
    event: 'music',
    platform: 'system',
    data: {
      videoId: song.videoId,
      author: song.author,
      title: song.title
    }
  });
}


function extractYouTubeID(url) {
  try {
    const u = new URL(url);

    // 1) Если есть параметр v → обычный URL
    const v = u.searchParams.get('v');
    if (v) return v;

    // 2) Короткие ссылки youtu.be
    if (u.hostname === 'youtu.be') {
      const parts = u.pathname.split('/');
      if (parts.length > 1 && parts[1].length > 0) {
        return parts[1];
      }
    }

    return null;
  } catch {
    return null;
  }
}


function formatNickname(platform, nickname, userId = null) {
  if (platform === 'tiktok' && userId && tiktokLikes.has(userId)) {
    return `[TikTok] ${nickname} ❤️×${tiktokLikes.get(userId)}`;
  }

  switch (platform) {
    case 'tiktok': return `[TikTok] ${nickname}`;
    case 'youtube': return `[YouTube] ${nickname}`;
    case 'twitch': return `[Twitch] ${nickname}`;
    default: return nickname;
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
  wss = new WebSocket.Server({ port: 21214 });
  console.log('✅ Terraria WS → ws://localhost:21214');
  wss.on('connection', ws => {
      ws.on('message', message => {
        try {
          const d = JSON.parse(message);

          if (d.event === 'trackEnded') {
            const next = songQueue.next(); // берём следующий трек
            playYouTube(next); // обновляем current на фронтенде
            broadcastQueue();  // синхронизируем очередь
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
      const { subscription, event } = req.body;

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
      identity: { username: STREAMER, password: TWITCH_OAUTH },
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
        const query = text.slice(6).trim();

        // пробуем распарсить как прямой YouTube-URL
        const maybeId = extractYouTubeID(query);

        let videoId, title, authorName;

        if (maybeId) {
          // нашли ID — делаем поиск через yt-search
          const r = await yts(maybeId);
          const video = r.videos?.[0];

          if (!video) {
            twitch.say(STREAMER, `❌ ${user}, не удалось найти трек по ссылке`);
            return;
          }

          videoId = video.videoId;
          title = video.title;
          authorName = video.author?.name || 'Unknown';

        } else {
          // обычный поиск
          const r = await yts(query);
          const video = r.videos?.[0];

          if (!video) {
            twitch.say(STREAMER, `❌ ${user}, не удалось найти трек по запросу`);
            return;
          }

          videoId = video.videoId;
          title = video.title;
          authorName = video.author?.name || 'Unknown';
        }

        // антиспам
        const lastRequestTime = songQueue.lastRequest.get(user) || 0;
        const now = Date.now();
        const COOLDOWN = 1 * 10 * 1000; // 2 минуты в миллисекундах

        if (now - lastRequestTime < COOLDOWN) {
          const remainingMs = COOLDOWN - (now - lastRequestTime);
          const remainingSec = Math.ceil(remainingMs / 1000);
          const min = Math.floor(remainingSec / 60);
          const sec = remainingSec % 60;

          twitch.say(
            STREAMER,
            `⏳ ${user}, подожди ${min > 0 ? min + 'м ' : ''}${sec}s перед следующим заказом`
          );
          return;
        }

        // добавляем трек в очередь
        songQueue.add({
          user,
          title,
          videoId,
          author: authorName
        });

        broadcastQueue();

        twitch.say(STREAMER, `🎵 ${user} добавил: ${authorName} — ${title}`);

        // если сейчас ничего не играет — запускаем
       if (!songQueue.current) {
          songQueue.current = songQueue.queue.shift(); // достаём первый элемент
          if (songQueue.current) playYouTube(songQueue.current);
        }
        broadcastQueue(); // обновляем очередь после изменения

        return;
      }

      // ===== SKIP =====
      if (text === '!skip') {
        const next = songQueue.next();
        if (next) {
          playYouTube(next.videoId);
          const nextAuthor = next.author || 'Unknown';
          twitch.say(STREAMER, `⏭ Следующий трек: ${nextAuthor} — ${next.title}`);
        } else {
          twitch.say(STREAMER, `📭 Очередь пуста`);
        }
        return;
      }

      // ===== NOW PLAYING =====
      if (text === '!np' && songQueue.current) {
        const currentAuthor = songQueue.current.author || 'Unknown';
        twitch.say(STREAMER, `🎶 Сейчас играет: ${currentAuthor} — ${songQueue.current.title}`);
        return;
      }

      // ===== QUEUE =====
      if (text === '!queue') {
        const list = songQueue.list();
        twitch.say(
          STREAMER,
          list ? `📜 Очередь: ${list}` : `📭 Очередь пуста`
        );
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
        if (!tiktokLikes.has(d.user.userId)) {
            tiktokLikes.set(d.user.userId, 0);
        }
      emit('chat', 'tiktok', {
        userId: d.user.userId,
        nickname: formatNickname('tiktok', d.user.nickname, d.user.userId),
        text: d.comment
      })
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
      const giftIcon = giftIconUri
        ? `https://p16-webcast.tiktokcdn.com/img/maliva/${giftIconUri}` + `~tplv-obj.webp`
        : null;
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
    const yt = new LiveChat({ channelId: YT_CHANNEL_ID });

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

      emit('chat', 'youtube', {
        userId,
        nickname: formatNickname('youtube', chatItem.author.name),
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
