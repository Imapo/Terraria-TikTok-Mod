// server.js — TikTok + Twitch + YouTube → Terraria (FINAL)

import WebSocket from 'ws';
import tmi from 'tmi.js';
import express from 'express';
import crypto from 'crypto';
import * as dotenv from 'dotenv';
import { TikTokLiveConnection, WebcastEvent } from 'tiktok-live-connector';
import { LiveChat } from 'youtube-chat';

dotenv.config();

/* =======================
CONFIG
======================= */

const STREAMER = process.env.TWITCH_USERNAME;
const TWITCH_OAUTH = process.env.TWITCH_TOKEN;
const TIKTOK_USERNAME = process.env.TIKTOK_USERNAME;
const YT_CHANNEL_ID = process.env.YT_CHANNEL_ID; // правильный YouTube channel ID
console.log('Connecting to YouTube channel:', YT_CHANNEL_ID);
const EVENTSUB_SECRET = 'terramodsecret123';

/* =======================
WEBSOCKET → TERRARIA
======================= */

let wss;

function broadcast(event) {
if (!wss) return;
const msg = JSON.stringify(event);
wss.clients.forEach(c => c.readyState === WebSocket.OPEN && c.send(msg));
}

function emit(event, platform, data = {}) {
broadcast({ event, platform, data });
}

function formatNickname(platform, nickname) {
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
  wss = new WebSocket.Server({ port: 21213 });
  console.log('✅ Terraria WS → ws://localhost:21213');

  /* ---------- HTTP (Twitch EventSub) ---------- */
  const app = express();
  app.use(express.json());

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

  app.listen(3000, () => console.log('🌐 HTTP → :3000'));

  /* ---------- Twitch Chat ---------- */
  try {
    const twitch = new tmi.Client({
      identity: { username: STREAMER, password: TWITCH_OAUTH },
      channels: [STREAMER]
    });

    const twitchSeen = new Set();
    await twitch.connect();
    console.log('✅ Twitch Chat connected');
    emit('chat', 'twitch', {
        userId: `system`,
        nickname: `Twitch`,
        text: `Connected`
      });

    twitch.on('message', (_, tags, msg, self) => {
      if (self) return;
      const user = tags.username;

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
    const tt = new TikTokLiveConnection(TIKTOK_USERNAME);
    await tt.connect();
    console.log('✅ TikTok connected');
    emit('chat', 'tiktok', {
        userId: `system`,
        nickname: `TikTok`,
        text: `Connected`
      });

    tt.on(WebcastEvent.MEMBER, d =>
      emit('join', 'tiktok', {
        userId: d.user.userId,
        nickname: formatNickname('tiktok', d.user.nickname)
      })
    );

    tt.on(WebcastEvent.CHAT, d =>
      emit('chat', 'tiktok', {
        userId: d.user.userId,
        nickname: formatNickname('tiktok', d.user.nickname),
        text: d.comment
      })
    );

    tt.on(WebcastEvent.GIFT, d =>
      emit('gift', 'tiktok', {
        userId: d.user.userId,
        nickname: formatNickname('tiktok', d.user.nickname),
        amount: d.gift.diamondCount
      })
    );

    tt.on(WebcastEvent.LIKE, d =>
      emit('like', 'tiktok', {
        userId: d.user.userId,
        nickname: formatNickname('tiktok', d.user.nickname),
        amount: d.likeCount
      })
    );

    tt.on(WebcastEvent.FOLLOW, d =>
      emit('follow', 'tiktok', {
        userId: d.user.userId,
        nickname: formatNickname('tiktok', d.user.nickname)
      })
    );

    tt.on(WebcastEvent.SHARE, d =>
      emit('share', 'tiktok', {
        userId: d.user.userId,
        nickname: formatNickname('tiktok', d.user.nickname)
      })
    );

    tt.on(WebcastEvent.SUBSCRIBE, d =>
      emit('subscribe', 'tiktok', {
        userId: d.user.userId,
        nickname: formatNickname('tiktok', d.user.nickname)
      })
    );
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
    const ytSeen = new Set();

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
        emit('chat', 'youtube', {
            userId: `system`,
            nickname: `YouTube`,
            text: `⚠ YouTube error: ${err}`
          });
    });

    yt.on('chat', chatItem => {
      const userId = chatItem.author.channelId;
      if (!ytSeen.has(userId)) {
        ytSeen.add(userId);
        emit('join', 'youtube', {
          userId,
          nickname: formatNickname('youtube', chatItem.author.name)
        });
      }

      let messageText = chatItem.message;
      if (Array.isArray(messageText)) {
        messageText = messageText.map(part => part.text).join('');
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

    await yt.start();
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
