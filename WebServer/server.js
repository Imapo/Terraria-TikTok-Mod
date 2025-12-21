// server.js — TikTok + Twitch + YouTube → Terraria (FIXED)

import WebSocket from 'ws';
import tmi from 'tmi.js';
import express from 'express';
import axios from 'axios';
import crypto from 'crypto';
import fs from 'fs';
import * as dotenv from 'dotenv';
import { TikTokLiveConnection, WebcastEvent } from 'tiktok-live-connector';
import { google } from 'googleapis';

dotenv.config();

/* =======================
   CONFIG
======================= */

const STREAMER = process.env.TWITCH_USERNAME;
const TWITCH_OAUTH = process.env.TWITCH_TOKEN;
const TWITCH_CLIENT_ID = process.env.TWITCH_CLIENT_ID;
const TWITCH_CLIENT_SECRET = process.env.TWITCH_CLIENT_SECRET;
const NGROK_URL = process.env.NGROK_URL;

const TIKTOK_USERNAME = process.env.TIKTOK_USERNAME;
const YT_VIDEO_ID = process.env.YT_VIDEO_ID;

const YT_CREDENTIALS_PATH = './yt-credentials.json';
const YT_TOKEN_PATH = './yt-token.json';
const YT_SCOPES = ['https://www.googleapis.com/auth/youtube.readonly'];

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
  broadcast({
    event,        // chat / join / gift / follow / subscribe
    platform,     // twitch / tiktok / youtube
    data
  });
}

function formatNickname(platform, nickname) {
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
    crypto
      .createHmac('sha256', EVENTSUB_SECRET)
      .update(message)
      .digest('hex');

  return expected === req.get('Twitch-Eventsub-Message-Signature');
}

/* =======================
   YOUTUBE AUTH
======================= */

async function youtubeAuth() {
  const creds = JSON.parse(await fs.promises.readFile(YT_CREDENTIALS_PATH));
  const oauth = new google.auth.OAuth2(
    creds.installed.client_id,
    creds.installed.client_secret,
    creds.installed.redirect_uris[0]
  );

  try {
    const token = JSON.parse(await fs.promises.readFile(YT_TOKEN_PATH));
    oauth.setCredentials(token);
    return oauth;
  } catch {
    const url = oauth.generateAuthUrl({
      access_type: 'offline',
      scope: YT_SCOPES
    });
    console.log('🔑 YouTube auth:', url);
    throw new Error('YouTube not authorized');
  }
}

/* =======================
   MAIN
======================= */

async function main() {
  /* ---------- WebSocket ---------- */
  wss = new WebSocket.Server({ port: 21213 });
  console.log('✅ WS → ws://localhost:21213');

  /* ---------- HTTP Server ---------- */
  const app = express();
  app.use(express.json());

  /* Twitch EventSub */
  app.post('/twitch/eventsub', (req, res) => {
    const type = req.get('Twitch-Eventsub-Message-Type');

    if (type === 'webhook_callback_verification') {
      return res.send(req.body.challenge);
    }

    if (!verifyTwitchSignature(req)) {
      return res.status(403).end();
    }

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

      console.log('🔔 EventSub:', subscription.type);
    }

    res.status(200).end();
  });

  /* YouTube OAuth */
  app.get('/youtube-auth', async (req, res) => {
    if (!req.query.code) return res.send('No code');

    const creds = JSON.parse(fs.readFileSync(YT_CREDENTIALS_PATH));
    const oauth = new google.auth.OAuth2(
      creds.installed.client_id,
      creds.installed.client_secret,
      creds.installed.redirect_uris[0]
    );

    const { tokens } = await oauth.getToken(req.query.code);
    fs.writeFileSync(YT_TOKEN_PATH, JSON.stringify(tokens));
    res.send('✅ YouTube authorized');
  });

  app.listen(3000, () => console.log('🌐 HTTP → :3000'));

  /* ---------- Twitch Chat ---------- */
  const twitch = new tmi.Client({
    identity: { username: STREAMER, password: TWITCH_OAUTH },
    channels: [STREAMER]
  });
  const twitchSeenUsers = new Set();

  await twitch.connect();

  twitch.on('message', (_, tags, msg, self) => {
      if (self) return;

      const user = tags.username;

      if (!twitchSeenUsers.has(user)) {
        twitchSeenUsers.add(user);
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

  /* ---------- TikTok ---------- */
  const tt = new TikTokLiveConnection(TIKTOK_USERNAME);
  await tt.connect();

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

  /* ---------- YouTube ---------- */
  if (YT_VIDEO_ID) {
    const auth = await youtubeAuth();
    const yt = google.youtube({ version: 'v3', auth });
    const ytSeenUsers = new Set();

    const vid = await yt.videos.list({
      part: ['liveStreamingDetails'],
      id: [YT_VIDEO_ID]
    });

    const chatId =
      vid.data.items[0]?.liveStreamingDetails?.activeLiveChatId;

    if (!chatId) {
      console.warn('⚠️ No YT live chat');
      return;
    }

    let pageToken = '';

    async function poll() {
      const res = await yt.liveChatMessages.list({
        liveChatId: chatId,
        part: ['snippet', 'authorDetails'],
        pageToken
      });

      pageToken = res.data.nextPageToken;

      for (const m of res.data.items) {
        if (m.snippet.type === 'textMessageEvent') {
          const userId = m.authorDetails.channelId;

          if (!ytSeenUsers.has(userId)) {
            ytSeenUsers.add(userId);
            emit('join', 'youtube', {
              userId,
              nickname: formatNickname('youtube', m.authorDetails.displayName)
            });
          }

          emit('chat', 'youtube', {
            userId,
            nickname: formatNickname('youtube', m.authorDetails.displayName),
            text: m.snippet.displayMessage
          });
        }
      }

      setTimeout(poll, res.data.pollingIntervalMillis);
    }

    poll();
  }
}

/* ======================= */

main().catch(console.error);
