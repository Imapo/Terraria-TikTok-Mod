// server.js — TikTok + Twitch + YouTube → Terraria (FINAL)
import fs from 'fs';
import { Client, GatewayIntentBits } from 'discord.js';
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
import { EventEmitter } from 'events'; // Уже импортирован здесь

dotenv.config();

/* =======================
CONFIG
======================= */

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const STREAMER = process.env.TWITCH_USERNAME;
const TIKTOK_USERNAME = process.env.TIKTOK_USERNAME;
const YT_CHANNEL_ID = process.env.YT_CHANNEL_ID;
const EVENTSUB_SECRET = 'terramodsecret123';
const VIP_FILE = './vip.json';
const TELEGRAM_CHANNEL_ID = process.env.TELEGRAM_CHANNEL_ID;
const OWNER_ID = Number(process.env.OWNER_ID);

/* =======================
GLOBAL STATE
======================= */

class SongQueue {
    constructor() {
        this.queue = [];
        this.current = null;
        this.lastRequest = new Map();
    }

    add(song, isVIP = false) {
        if (isVIP) {
            if (this.current) this.queue.unshift(song);
            else this.current = song;
        } else {
            this.queue.push(song);
        }
    }

    next() {
        this.current = this.queue.shift() || null;
        return this.current;
    }

    peekNext() {
        return this.queue[0] || null;
    }

    clearCurrent() {
        this.current = null;
    }

    list() {
        return this.queue.map((s, i) => `${i + 1}. ${s.title}`).join(' | ');
    }

    isEmpty() {
        return this.queue.length === 0 && this.current === null;
    }
}

class GlobalState {
    constructor() {
        this.songQueue = new SongQueue();
        this.chatHistory = [];
        this.platformStatus = {
            tiktok: false,
            youtube: false,
            twitch: false,
            telegram: false,
            discord: false
        };
        this.tiktokLikes = new Map();
        this.ytMessageCache = new Set();
        this.tiktokGiftProgress = new Map();
        this.cachedUpload = { value: null, ts: 0 };
        this.twitchLiveCache = { value: null, ts: 0 };
        this.streamStartTs = null;
        this.telegramVIPs = new Set();
        this.loadVIPs();
        // Глобальная очередь модерации для всех платформ
        this.moderationQueue = new Map(); // ID -> { user, userId, song, isVIP, timestamp, platformName, messageId }
        this.moderationIdCounter = 0;
    }

    loadVIPs() {
        if (fs.existsSync(VIP_FILE)) {
            const data = JSON.parse(fs.readFileSync(VIP_FILE));
            data.forEach(id => this.telegramVIPs.add(id));
            console.log(`🌟 Загружено VIP пользователей: ${data.join(', ')}`);
        }
    }

    saveVIPs() {
        fs.writeFileSync(VIP_FILE, JSON.stringify([...this.telegramVIPs]));
    }

    isVIPTelegram(userId) {
        return this.telegramVIPs.has(userId);
    }

    addToChatHistory(platform, data) {
        this.chatHistory.push({ platform, ...data });
        if (this.chatHistory.length > 50) this.chatHistory.shift();
    }

    setPlatformStatus(platform, status) {
        this.platformStatus[platform] = status;
        console.log(`🌐 ${platform} status: ${status}`);
    }

    getPlatformStatus(platform) {
        return this.platformStatus[platform];
    }
}

/* =======================
UTILS
======================= */

class Utils {
    static formatTimeHHMMSS(date = new Date()) {
        return date.toLocaleTimeString('ru-RU', {
            hour: '2-digit',
            minute: '2-digit',
            second: '2-digit'
        });
    }

    static formatCooldown(ms) {
        const totalSec = Math.ceil(ms / 1000);
        const min = Math.floor(totalSec / 60);
        const sec = totalSec % 60;

        if (min > 0 && sec > 0) return `${min} мин ${sec} сек`;
        if (min > 0) return `${min} мин`;
        return `${sec} сек`;
    }

    static formatUptime(streamStartTs) {
        if (!streamStartTs) return '—';

        const sec = Math.floor((Date.now() - streamStartTs) / 1000);
        const h = Math.floor(sec / 3600);
        const m = Math.floor((sec % 3600) / 60);
        const s = sec % 60;

        if (h > 0) return `${h}ч ${m}м`;
        if (m > 0) return `${m}м ${s}с`;
        return `${s}с`;
    }

    static extractYouTubeID(input) {
        try {
            return getYouTubeId(input) || null;
        } catch (err) {
            console.error('Error extracting YouTube ID:', err);
            return null;
        }
    }

    static parseYouTubeMessageText(raw) {
        if (!Array.isArray(raw.message)) return '';
        return raw.message
            .map(part => {
                if (part.text) return part.text;
                if (part.emoji?.shortcode) return part.emoji.shortcode;
                return '';
            })
            .join('');
    }

    static getYouTubeRoles(raw) {
        return {
            isAnchor: raw.isOwner === true,
            isMod: raw.isModerator === true,
            isSubscriber: raw.isMembership === true,
            isFollower: false
        };
    }

    static uploadIndicator(mbps) {
        if (!mbps) return '⚪';
        if (mbps >= 8) return '🟢';
        if (mbps >= 5) return '🟡';
        return '🔴';
    }

    static canUserSkipCurrentSong(songQueue, platform, userId, tags = null, role = null) {
        if (!songQueue.current) return false;
        if (tags) {
            if (Utils.isModerator(tags) || Utils.isBroadcaster(tags, STREAMER)) return true;
        }
        if (role) {
            if (role === 'moderator' || role === 'broadcaster') return true;
        }
        return songQueue.current.requesterId === `${platform}:${userId}`;
    }

    static isBroadcaster(tags, streamer) {
        return tags.badges?.broadcaster === '1' || tags.username === streamer;
    }

    static isModerator(tags) {
        return tags.mod || false;
    }

    static isSubscriber(tags) {
        return tags.subscriber || false;
    }

    static isVIP(tags) {
        return tags.badges?.vip === '1';
    }

    static hasModeratorPrivileges(tags, streamer) {
        return Utils.isModerator(tags) || Utils.isBroadcaster(tags, streamer) || Utils.isVIP(tags);
    }

    static canSkipOrStop(tags, streamer) {
        return Utils.isModerator(tags) || Utils.isBroadcaster(tags, streamer);
    }

    static getUnifiedCooldown({
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

    static getTikTokCooldown(userId, {
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
}

/* =======================
RETRY MANAGER
======================= */

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

/* =======================
BASE PLATFORM CLASS
======================= */

class Platform {
    constructor(name, globalState, eventEmitter) {
        this.name = name;
        this.globalState = globalState;
        this.eventEmitter = eventEmitter;
        this.isConnected = false;
        this.retryManager = new RetryManager();
        
        // Счётчик для уникальных ID сообщений
        this.messageIdCounter = 0;
        
        // Запускаем очистку старых заявок
        this.startModerationCleanup();
    }
    
    // Генерация уникального ID сообщения
    generateMessageId() {
        return `${this.name}_${Date.now()}_${this.messageIdCounter++}`;
    }
    
    startModerationCleanup() {
        setInterval(() => {
            const now = Date.now();
            const maxAge = 30 * 60 * 1000; // 30 минут
            
            for (const [id, request] of this.globalState.moderationQueue.entries()) {
                if (now - request.timestamp > maxAge) {
                    this.globalState.moderationQueue.delete(id);
                    this.sendMessageToChat(`⏰ Заявка #${id} удалена (время вышло)`);
                }
            }
        }, 60 * 1000);
    }

    async connect() {
        try {
            await this._connect();
            this.isConnected = true;
            this.globalState.setPlatformStatus(this.name, true);
            console.log(`✅ ${this.name} connected`);
        } catch (error) {
            console.error(`❌ ${this.name} connection failed:`, error.message);
            this.isConnected = false;
            this.globalState.setPlatformStatus(this.name, false);
            this.scheduleReconnect();
        }
    }

    async disconnect() {
        try {
            await this._disconnect();
        } catch (error) {
            console.error(`❌ ${this.name} disconnect error:`, error.message);
        } finally {
            this.isConnected = false;
            this.globalState.setPlatformStatus(this.name, false);
            this.retryManager.clear(`${this.name}-connection`);
        }
    }

    scheduleReconnect() {
        this.retryManager.retry(`${this.name}-connection`, async () => {
            await this.connect();
        });
    }

    // Абстрактный метод для проверки прав модератора/стримера
    isUserModeratorOrBroadcaster(userData) {
        throw new Error('isUserModeratorOrBroadcaster must be implemented by subclass');
    }

    // Обработка команд премодерации (!yes ID, !no ID, !modqueue)
    async handleModerationCommand({ user, userId, text, userData }) {
        const trimmed = text.trim();
        
        if (trimmed === '!modqueue') {
            if (!this.isUserModeratorOrBroadcaster(userData)) {
                this.sendMessageToChat(`❌ ${user}, только модераторы и стример могут видеть очередь модерации!`);
                return;
            }
            this.showModerationQueue();
            return;
        }
        
        if (trimmed.startsWith('!yes ') || trimmed.startsWith('!no ')) {
            if (!this.isUserModeratorOrBroadcaster(userData)) {
                this.sendMessageToChat(`❌ ${user}, только модераторы и стример могут одобрять треки!`);
                return;
            }
            
            const parts = trimmed.split(/\s+/);
            const command = parts[0];
            const idStr = parts[1];
            
            const id = parseInt(idStr, 10);
            if (isNaN(id) || id < 0) {
                this.sendMessageToChat(`❌ Неверный формат: используйте "!yes {ID}" или "!no {ID}"`);
                return;
            }
            
            const request = this.globalState.moderationQueue.get(id);
            if (!request) {
                this.sendMessageToChat(`❌ Заявка #${id} не найдена. Проверьте !modqueue`);
                return;
            }
            
            if (command === '!yes') {
                await this.approveSongRequest(id, request, user);
            } else if (command === '!no') {
                await this.rejectSongRequest(id, request, user);
            }
            return;
        }
    }

    // Показать очередь модерации
    showModerationQueue() {
        if (this.globalState.moderationQueue.size === 0) {
            this.sendMessageToChat(`📭 Очередь модерации пуста`);
            return;
        }
        
        const requests = Array.from(this.globalState.moderationQueue.values());
        let message = `📋 Очередь модерации (${requests.length}):\n`;
        
        for (const request of requests.slice(0, 10)) {
            const age = Math.floor((Date.now() - request.timestamp) / 60000);
            message += `#${request.id}: ${request.user} (${request.platformName}) — "${request.song.title}" (${age} мин)\n`;
        }
        
        if (requests.length > 10) {
            message += `...и ещё ${requests.length - 10} заявок`;
        }
        
        this.sendMessageToChat(message);
    }

    // Одобрить трек - РЕДАКТИРУЕМ сообщение вместо создания новых
    async approveSongRequest(id, request, moderator) {
        try {
            const { user, userId, song, isVIP, platformName, messageId } = request;
            
            // Обновляем время последнего запроса пользователя
            const now = Date.now();
            this.globalState.songQueue.lastRequest.set(`${platformName}:${userId}`, now);
            
            // Добавляем трек в очередь
            this.globalState.songQueue.add(song, isVIP);
            
            // === РЕДАКТИРУЕМ сообщение с заявкой ===
            if (messageId) {
                this.eventEmitter.emit('chat.update', {
                    messageId: messageId,
                    platform: platformName,
                    text: `Одобрено ${moderator}: ${song.author} — ${song.title}`,
                    status: 'approved',
                    extraClass: 'moderation-approved'
                });
            }
            // =====================================
            
            // Удаляем из очереди модерации
            this.globalState.moderationQueue.delete(id);
            
            // Запускаем воспроизведение, если очередь пуста
            if (!this.globalState.songQueue.current && !this.globalState.songQueue.isEmpty()) {
                const nextSong = this.globalState.songQueue.next();
                if (nextSong) {
                    this.eventEmitter.emit('music.play', nextSong);
                }
            }
            
            this.eventEmitter.emit('queue.update');
            
        } catch (error) {
            console.error('Error approving song request:', error);
            this.sendMessageToChat(`❌ Ошибка при одобрении заявки #${id}`);
        }
    }

    // Отклонить трек - РЕДАКТИРУЕМ сообщение вместо создания новых
    async rejectSongRequest(id, request, moderator) {
        try {
            const { song, messageId, platformName } = request;
            
            // === РЕДАКТИРУЕМ сообщение с заявкой ===
            if (messageId) {
                this.eventEmitter.emit('chat.update', {
                    messageId: messageId,
                    platform: platformName,
                    text: `Отклонено ${moderator}: ${song.author} — ${song.title}`,
                    status: 'rejected',
                    extraClass: 'moderation-rejected'
                });
            }
            // =====================================
            
            // Удаляем из очереди модерации
            this.globalState.moderationQueue.delete(id);
            
        } catch (error) {
            console.error('Error rejecting song request:', error);
            this.sendMessageToChat(`❌ Ошибка при отклонении заявки #${id}`);
        }
    }

    // Модифицированный handleSongRequest с поддержкой премодерации
    async handleSongRequest({ user, userId, text, cooldownMs, isVIP = false, bypassModeration = false }) {
        const query = text.slice(6).trim();
        if (!query) {
            this.sendMessageToChat(`❌ ${user}, укажите название трека после !song`);
            return;
        }

        const last = this.globalState.songQueue.lastRequest.get(`${this.name}:${user}`) || 0;
        const now = Date.now();

        if (cooldownMs > 0 && now - last < cooldownMs) {
            const remainingMs = cooldownMs - (now - last);
            this.sendMessageToChat(`⏳ ${user}, сможешь заказать ещё через ⏱: ${Utils.formatCooldown(remainingMs)}`);
            return;
        }

        let foundVideo;
        const videoId = Utils.extractYouTubeID(query);

        try {
            if (videoId) {
                const r = await yts({ videoId });
                foundVideo = r.video || r;
            } else {
                const r = await yts({ query });
                foundVideo = r.videos?.[0];
            }
        } catch (error) {
            console.error('Error searching song:', error);
            this.sendMessageToChat(`❌ ${user}, ошибка при поиске трека`);
            return;
        }

        if (!foundVideo) {
            this.sendMessageToChat(`❌ ${user}, трек не найден`);
            return;
        }
        
        if (foundVideo.seconds > 10 * 60) {
            this.sendMessageToChat(`❌ ${user}, трек слишком длинный (максимум 10 минут)`);
            return;
        }

        // Создаём объект трека
        const song = {
            requesterId: `${this.name}:${userId}`,
            requester: user,
            title: foundVideo.title,
            videoId: foundVideo.videoId,
            author: foundVideo.author?.name || 'Unknown',
            duration: foundVideo.seconds || 0
        };

        // Если пользователь может пропустить премодерацию (стример/мод)
        if (bypassModeration) {
            this.globalState.songQueue.lastRequest.set(`${this.name}:${user}`, now);
            this.globalState.songQueue.add(song, isVIP);
            this.sendMessageToChat(`🎵 Добавлено: ${song.author} — ${song.title} (пропущена модерация)`);

            if (!this.globalState.songQueue.current && !this.globalState.songQueue.isEmpty()) {
                const nextSong = this.globalState.songQueue.next();
                if (nextSong) {
                    this.eventEmitter.emit('music.play', nextSong);
                }
            }

            this.eventEmitter.emit('queue.update');
            return;
        }

        // Добавляем в очередь премодерации
        const moderationId = this.globalState.moderationIdCounter++;
        const messageId = this.generateMessageId(); // Генерируем уникальный ID
        
        const request = {
            id: moderationId,
            user,
            userId,
            song,
            isVIP,
            timestamp: Date.now(),
            platformName: this.name,
            messageId: messageId // Сохраняем ID для последующего редактирования
        };
        
        this.globalState.moderationQueue.set(moderationId, request);
        
        // === Отправляем сообщение с уникальным ID и классом для модерации ===
        this.eventEmitter.emit('chat', {
            platform: this.name,
            userId: 'system',
            nickname: this.name,
            text: `📝 ${user}: "${song.title}" — ${song.author} отправлен на модерацию. [ID: #${moderationId}]`,
            messageId: messageId,
            extraClass: 'moderation-pending'
        });
        // =====================================================================
    }

    // Обновлённый sendMessageToChat с поддержкой messageId
    sendMessageToChat(message, extraClass = '') {
        const messageId = this.generateMessageId();
        this.eventEmitter.emit('chat', {
            platform: this.name,
            userId: 'system',
            nickname: this.name,
            text: message,
            messageId: messageId,
            extraClass: extraClass
        });
    }

    // Обновлённый emitChat с поддержкой messageId
    emitChat(userId, nickname, text, extraClass = '') {
        const messageId = this.generateMessageId();
        this.eventEmitter.emit('chat', {
            platform: this.name,
            userId,
            nickname,
            text,
            timestamp: Date.now(),
            time: Utils.formatTimeHHMMSS(),
            messageId: messageId,
            extraClass: extraClass
        });
    }

    // Abstract methods to be implemented by subclasses
    async _connect() {
        throw new Error('_connect must be implemented');
    }

    async _disconnect() {
        throw new Error('_disconnect must be implemented');
    }
}

/* =======================
TIKTOK SERVICE
======================= */

class TikTokService extends Platform {
    constructor(globalState, eventEmitter) {
        super('tiktok', globalState, eventEmitter);
        this.connection = null;
    }

    async _connect() {
        this.connection = new TikTokLiveConnection(TIKTOK_USERNAME, {
            enableExtendedGiftInfo: true
        });

        await this.connection.connect();
        this.setupEventListeners();
    }

    async _disconnect() {
        if (this.connection) {
            this.connection.disconnect();
            this.connection = null;
        }
    }

    setupEventListeners() {
        const tt = this.connection;

        tt.on(WebcastEvent.MEMBER, d => {
            if (!this.globalState.tiktokLikes.has(d.user.userId)) {
                this.globalState.tiktokLikes.set(d.user.userId, 0);
            }
            /*
            this.emitChat(
                d.user.userId,
                this.formatNickname(d.user.nickname, d.user.userId),
                'присоединился'
            );
            */
        });

        tt.on(WebcastEvent.CHAT, d => {
            const text = d.comment;
            const userId = d.user.userId;
            const user = d.user.nickname;

            const identity = d.userIdentity || {};
            const isAnchor = identity.isAnchor || false;
            const isMod = identity.isModeratorOfAnchor || isAnchor;
            const isSubscriber = identity.isSubscriberOfAnchor || false;
            const isFollower = Boolean(identity.isFollower);

            if (text.startsWith('!yes ') || text.startsWith('!no ') || text === '!modqueue') {
                if (!this.isUserModeratorOrBroadcaster(identity)) {
                    this.sendMessageToChat(`❌ ${user}, только модераторы и стример могут управлять модерацией!`);
                    return;
                }
        
                this.handleModerationCommand({
                    user,
                    userId,
                    text,
                    userData: identity
                });
                return;
            }

            // Commands
            if (text.startsWith('!song ')) {
                const cooldownMs = Utils.getTikTokCooldown(userId, {
                    isAnchor,
                    isMod,
                    isSubscriber,
                    isFollower
                });
                const bypassModeration = this.isUserModeratorOrBroadcaster(identity);
                this.handleSongRequest({
                    user,
                    userId,
                    text,
                    cooldownMs,
                    bypassModeration
                });
                return;
            }

            if (text === '!skip') {
                const allowed = Utils.canUserSkipCurrentSong(
                    this.globalState.songQueue,
                    'tiktok',
                    userId,
                    null,
                    isMod ? 'moderator' : isAnchor ? 'broadcaster' : 'user'
                );
                if (!allowed) {
                    this.sendMessageToChat(`❌ ${user}, ты можешь скипать только свой текущий трек`);
                    return;
                }

                this.globalState.songQueue.current = null;
                const next = this.globalState.songQueue.next();
                
                if (next) {
                    this.eventEmitter.emit('music.play', next);
                    this.sendMessageToChat(`⏭ Следующий трек: ${next.author} — ${next.title}`);
                } else {
                    this.eventEmitter.emit('music.stop');
                    this.sendMessageToChat(`⏹ Очередь пуста, воспроизведение остановлено`);
                }
                
                this.eventEmitter.emit('queue.update');
                return;
            }

            if (text === '!queue') {
                this.handleQueueCommand();
                return;
            }

            if (text === '!pause' || text === '!play' || text === '!stop') {
                if (!isMod && !isAnchor) {
                    this.sendMessageToChat(`❌ ${user}, команда доступна только модераторам и стримеру`);
                    return;
                }

                if (text === '!pause') {
                    this.eventEmitter.emit('music.pause');
                    this.sendMessageToChat(`⏸ Трек поставлен на паузу`);
                } else if (text === '!play') {
                    this.eventEmitter.emit('music.resume');
                    this.sendMessageToChat(`▶️ Продолжаем воспроизведение`);
                } else if (text === '!stop') {
                    this.globalState.songQueue.clearCurrent();
                    this.globalState.songQueue.queue = [];
                    this.globalState.songQueue.lastRequest.clear();
                    this.eventEmitter.emit('music.stop');
                    this.eventEmitter.emit('queue.update');
                    this.sendMessageToChat(`⏹ Воспроизведение остановлено, очередь очищена`);
                }
                return;
            }

            // Regular chat
            this.emitChat(
                userId,
                this.formatNickname(user, userId),
                text
            );
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

            const key = `${d.user.userId}_${giftName}`;

            const prev = this.globalState.tiktokGiftProgress.get(key) || 0;
            const current = d.repeatCount ?? 1;

            // 🔹 считаем реальное приращение
            const delta = current - prev;

            // 🔹 сохраняем текущее состояние
            this.globalState.tiktokGiftProgress.set(key, current);

            // ❗ если дельта <= 0 — ничего не делаем
            if (delta <= 0) return;

            // 🔥 ЭМИТИМ ВСЕГДА, а не только repeatEnd
            this.eventEmitter.emit('gift', {
                platform: 'tiktok',
                data: {
                    userId: d.user.userId,
                    nickname: d.user.nickname,
                    gift: {
                        name: giftName,
                        icon: giftIcon
                    },
                    amount: delta
                }
            });

            // 🧹 если комбо завершено — чистим кэш
            if (d.repeatEnd) {
                this.globalState.tiktokGiftProgress.delete(key);
            }
        });

        tt.on(WebcastEvent.LIKE, d => {
            const userId = d.user.userId;
            const prev = this.globalState.tiktokLikes.get(userId) || 0;
            const total = prev + d.likeCount;
            this.globalState.tiktokLikes.set(userId, total);
            this.eventEmitter.emit('like', {
                platform: 'tiktok',
                data: {
                    userId,
                    nickname: d.user.nickname,
                    amount: d.likeCount
                }
            });
        });

        tt.on(WebcastEvent.FOLLOW, d => {
            this.eventEmitter.emit('follow', {
                platform: 'tiktok',
                data: {
                    userId: d.user.userId,
                    nickname: this.formatNickname(d.user.nickname, d.user.userId)
                }
            });
        });

        tt.on(WebcastEvent.SUBSCRIBE, d => {
            this.eventEmitter.emit('subscribe', {
                platform: 'tiktok',
                data: {
                    userId: d.user.userId,
                    nickname: this.formatNickname(d.user.nickname, d.user.userId)
                }
            });
        });

        tt.on('disconnected', () => {
            console.log('⚠ TikTok disconnected');
            this.isConnected = false;
            this.globalState.setPlatformStatus('tiktok', false);
            this.scheduleReconnect();
        });
    }

    formatNickname(nickname, userId) {
        if (userId && this.globalState.tiktokLikes.has(userId)) {
            return `[TikTok] ${nickname} ❤️×${this.globalState.tiktokLikes.get(userId)}`;
        }
        return `[TikTok] ${nickname}`;
    }

    handleQueueCommand() {
        const queue = this.globalState.songQueue;
        if (queue.queue.length > 0) {
            const list = queue.list();
            const current = queue.current ? 
                `🎶 Сейчас: ${queue.current.author} — ${queue.current.title}\n📜 Очередь: ${list}` :
                `📜 Очередь: ${list}`;
            this.sendMessageToChat(current);
        } else {
            if (queue.current) {
                this.sendMessageToChat(`🎶 Сейчас: ${queue.current.author} — ${queue.current.title}\n📭 Очередь пуста`);
            } else {
                this.sendMessageToChat(`📭 Очередь пуста`);
            }
        }
    }

    isUserModeratorOrBroadcaster(identity) {
        const isAnchor = identity.isAnchor || false;
        const isMod = identity.isModeratorOfAnchor || isAnchor;
        return isMod || isAnchor;
    }
}

/* =======================
TWITCH SERVICE
======================= */

class TwitchService extends Platform {
    constructor(globalState, eventEmitter) {
        super('twitch', globalState, eventEmitter);
        this.client = null;
        this.announcer = null;
        this.twitchSeen = new Set();
    }

    isUserModeratorOrBroadcaster(tags) {
        return Utils.canSkipOrStop(tags, STREAMER);
    }

    async _connect() {
        this.client = new tmi.Client({
            identity: {
                username: STREAMER,
                password: process.env.TWITCH_TOKEN
            },
            channels: [STREAMER]
        });

        await this.client.connect();
        this.announcer = new TwitchAnnouncer(this.client, STREAMER);
        
        // Start announcer
        setInterval(() => {
            if (this.announcer) {
                this.announcer.sendRandom();
            }
        }, 10 * 60 * 1000);

        this.setupEventListeners();
    }

    async _disconnect() {
        if (this.client) {
            this.client.disconnect();
            this.client = null;
        }
    }

    setupEventListeners() {
        const client = this.client;

        client.on('message', async (_, tags, msg, self) => {
            if (self) return;

            const user = tags.username;
            const text = msg.trim();

            // Сначала обрабатываем команды премодерации
            if (text.startsWith('!yes ') || text.startsWith('!no ') || text === '!modqueue') {
                await this.handleModerationCommand({
                    user,
                    userId: tags['user-id'],
                    text,
                    userData: tags
                });
                return;
            }

            // Commands
            if (text.startsWith('!song ')) {
                const cooldownMs = Utils.getUnifiedCooldown({
                    isAnchor: Utils.isBroadcaster(tags, STREAMER),
                    isMod: Utils.isModerator(tags),
                    isSubscriber: Utils.isSubscriber(tags),
                    isFollower: false
                });
                const bypassModeration = this.isUserModeratorOrBroadcaster(tags);
                await this.handleSongRequest({
                    user,
                    userId: tags['user-id'],
                    text,
                    cooldownMs,
                    isVIP: Utils.isVIP(tags),
                    bypassModeration
                });
                return;
            }

            if (text === '!skip') {
                const allowed = Utils.canUserSkipCurrentSong(
                    this.globalState.songQueue,
                    'twitch',
                    tags['user-id'],
                    tags
                );
                if (!allowed) {
                    this.sendMessageToUser(user, `❌ ${user}, ты можешь скипать только свой текущий трек`);
                    return;
                }

                this.globalState.songQueue.current = null;
                const next = this.globalState.songQueue.next();
                
                if (next) {
                    this.eventEmitter.emit('music.play', next);
                    this.sendMessageToUser(user, `⏭ Следующий трек: ${next.author} — ${next.title}`);
                } else {
                    this.eventEmitter.emit('music.stop');
                    this.sendMessageToUser(user, `⏹ Очередь пуста, воспроизведение остановлено`);
                }
                
                this.eventEmitter.emit('queue.update');
                return;
            }

            if (text === '!queue') {
                this.handleQueueCommand(user);
                return;
            }

            if (text === '!pause' || text === '!play' || text === '!stop') {
                if (!Utils.canSkipOrStop(tags, STREAMER)) {
                    this.sendMessageToUser(user, `❌ ${user}, команда доступна только модераторам и стримеру!`);
                    return;
                }

                if (text === '!pause') {
                    this.eventEmitter.emit('music.pause');
                    this.sendMessageToUser(user, `⏸ Трек поставлен на паузу`);
                } else if (text === '!play') {
                    this.eventEmitter.emit('music.resume');
                    this.sendMessageToUser(user, `▶️ Продолжаем воспроизведение`);
                } else if (text === '!stop') {
                    this.globalState.songQueue.clearCurrent();
                    this.globalState.songQueue.queue = [];
                    this.globalState.songQueue.lastRequest.clear();
                    this.eventEmitter.emit('music.stop');
                    this.eventEmitter.emit('queue.update');
                    this.sendMessageToUser(user, `⏹ Воспроизведение остановлено, очередь очищена`);
                }
                return;
            }

            // Regular chat
            if (!this.twitchSeen.has(user)) {
                this.twitchSeen.add(user);
                if (this.twitchSeen.size > 1000) {
                    const first = this.twitchSeen.values().next().value;
                    this.twitchSeen.delete(first);
                }
                this.eventEmitter.emit('join', {
                    platform: 'twitch',
                    data: {
                        userId: tags['user-id'],
                        nickname: `[Twitch] ${user}`
                    }
                });
            }

            this.emitChat(
                tags['user-id'],
                `[Twitch] ${user}`,
                msg
            );
        });

        client.on('cheer', (_, u) => {
            this.eventEmitter.emit('gift', {
                platform: 'twitch',
                data: {
                    userId: u['user-id'],
                    nickname: `[Twitch] ${u.username}`,
                    amount: u.bits
                }
            });
        });

        client.on('raided', (_, raider) => {
            this.emitChat(
                raider.username,
                `[Twitch] ${raider.username}`,
                `[РЕЙД] ${raider.viewers} зрителей`
            );
        });

        client.on('disconnected', (reason) => {
            console.error('⚠ Twitch disconnected:', reason);
            this.isConnected = false;
            this.globalState.setPlatformStatus('twitch', false);
            this.scheduleReconnect();
        });
    }

    sendMessageToUser(user, message) {
        if (this.client && this.isConnected) {
            this.client.say(STREAMER, message);
        }
    }

    handleQueueCommand(user) {
        const queue = this.globalState.songQueue;
        if (queue.queue.length > 0) {
            const list = queue.list();
            const current = queue.current ? 
                `🎶 Сейчас: ${queue.current.author} — ${queue.current.title}\n📜 Очередь: ${list}` :
                `📜 Очередь: ${list}`;
            
            if (current.length > 400) {
                this.sendMessageToUser(user, current.substring(0, 400));
                setTimeout(() => {
                    this.sendMessageToUser(user, current.substring(400, 800));
                }, 500);
            } else {
                this.sendMessageToUser(user, current);
            }
        } else {
            if (queue.current) {
                this.sendMessageToUser(user, `🎶 Сейчас: ${queue.current.author} — ${queue.current.title}\n📭 Очередь пуста`);
            } else {
                this.sendMessageToUser(user, `📭 Очередь пуста`);
            }
        }
    }
}

/* =======================
YOUTUBE SERVICE
======================= */

class YouTubeService extends Platform {
    constructor(globalState, eventEmitter) {
        super('youtube', globalState, eventEmitter);
        this.client = null;
    }

    async _connect() {
        this.client = new LiveChat({
            channelId: YT_CHANNEL_ID
        });

        await this.client.start();
        this.setupEventListeners();
    }

    async _disconnect() {
        if (this.client) {
            this.client.stop();
            this.client = null;
        }
    }

    setupEventListeners() {
        const yt = this.client;

        yt.on('start', () => {
            console.log('✅ YouTube Live Chat started');
            this.isConnected = true;
            this.globalState.setPlatformStatus('youtube', true);
        });

        yt.on('end', () => {
            console.warn('⚠ YouTube Live Chat ended');
            this.isConnected = false;
            this.globalState.setPlatformStatus('youtube', false);
            this.scheduleReconnect();
        });

        yt.on('error', (err) => {
            console.error('⚠ YouTube error:', err.message);
            this.isConnected = false;
            this.globalState.setPlatformStatus('youtube', false);
            this.scheduleReconnect();
        });

        yt.on('chat', chatItem => {
            this.handleChatMessage(chatItem);
        });

        yt.on('superchat', scItem => {
            this.eventEmitter.emit('gift', {
                platform: 'youtube',
                data: {
                    userId: scItem.author.channelId,
                    nickname: `[YouTube] ${scItem.author.name}`,
                    amount: scItem.amount
                }
            });
        });

        yt.on('membership', m => {
            this.eventEmitter.emit('follow', {
                platform: 'youtube',
                data: {
                    userId: m.author.channelId,
                    nickname: `[YouTube] ${m.author.name}`
                }
            });
        });
    }

    handleChatMessage(chatItem) {
        const msgId = chatItem.id;
        
        // Cache message
        if (this.globalState.ytMessageCache.has(msgId)) return;
        this.globalState.ytMessageCache.add(msgId);
        if (this.globalState.ytMessageCache.size > 500) {
            const first = this.globalState.ytMessageCache.values().next().value;
            this.globalState.ytMessageCache.delete(first);
        }

        const userId = chatItem.author?.channelId;
        const username = chatItem.author?.name || 'YouTubeUser';
        const messageText = Utils.parseYouTubeMessageText(chatItem);
        if (!messageText) return;

        const { isAnchor, isMod, isSubscriber } = Utils.getYouTubeRoles(chatItem);

        // Команды премодерации
        if (messageText.startsWith('!yes ') || messageText.startsWith('!no ') || messageText === '!modqueue') {
            if (!this.isUserModeratorOrBroadcaster(chatItem)) {
                this.sendMessageToChat(`❌ ${username}, только модераторы и стример могут управлять модерацией!`);
                return;
            }
    
            this.handleModerationCommand({
                user: username,
                userId,
                text: messageText,
                userData: chatItem
            });
            return;
        }

        // Commands
        if (messageText.startsWith('!song ')) {
            const cooldownMs = Utils.getUnifiedCooldown({
                isAnchor,
                isMod,
                isSubscriber,
                isFollower: false
            });
            // Проверяем, может ли пользователь пропустить премодерацию
            const bypassModeration = this.isUserModeratorOrBroadcaster(chatItem);
            this.handleSongRequest({
                user: username,
                userId,
                text: messageText,
                cooldownMs,
                bypassModeration
            });
            return;
        }

        if (messageText === '!skip') {
            const allowed = Utils.canUserSkipCurrentSong(
                this.globalState.songQueue,
                'youtube',
                userId,
                null,
                isAnchor ? 'broadcaster' : isMod ? 'moderator' : 'user'
            );
            if (!allowed) {
                this.sendMessageToChat(`❌ ${username}, ты можешь скипать только свой текущий трек`);
                return;
            }

            this.globalState.songQueue.current = null;
            const next = this.globalState.songQueue.next();
            
            if (next) {
                this.eventEmitter.emit('music.play', next);
                this.sendMessageToChat(`⏭ Следующий трек: ${next.author} — ${next.title}`);
            } else {
                this.eventEmitter.emit('music.stop');
                this.sendMessageToChat(`⏹ Очередь пуста, воспроизведение остановлено`);
            }
            
            this.eventEmitter.emit('queue.update');
            return;
        }

        if (messageText === '!queue') {
            this.handleQueueCommand();
            return;
        }

        if (messageText === '!pause' || messageText === '!play' || messageText === '!stop') {
            if (!isAnchor && !isMod) {
                this.sendMessageToChat(`❌ ${username}, команда доступна только модераторам и стримеру`);
                return;
            }

            if (messageText === '!pause') {
                this.eventEmitter.emit('music.pause');
                this.sendMessageToChat(`⏸ Трек поставлен на паузу`);
            } else if (messageText === '!play') {
                this.eventEmitter.emit('music.resume');
                this.sendMessageToChat(`▶️ Продолжаем воспроизведение`);
            } else if (messageText === '!stop') {
                this.globalState.songQueue.clearCurrent();
                this.globalState.songQueue.queue = [];
                this.globalState.songQueue.lastRequest.clear();
                this.eventEmitter.emit('music.stop');
                this.eventEmitter.emit('queue.update');
                this.sendMessageToChat(`⏹ Воспроизведение остановлено, очередь очищена`);
            }
            return;
        }

        // Regular chat
        this.emitChat(
            userId,
            `[YouTube] ${username}`,
            messageText
        );
    }

    handleQueueCommand() {
        const queue = this.globalState.songQueue;
        if (queue.queue.length > 0) {
            const list = queue.list();
            const current = queue.current ? 
                `🎶 Сейчас: ${queue.current.author} — ${queue.current.title}\n📜 Очередь: ${list}` :
                `📜 Очередь: ${list}`;
            this.sendMessageToChat(current);
        } else {
            if (queue.current) {
                this.sendMessageToChat(`🎶 Сейчас: ${queue.current.author} — ${queue.current.title}\n📭 Очередь пуста`);
            } else {
                this.sendMessageToChat(`📭 Очередь пуста`);
            }
        }
    }

    // Проверка прав модератора/стримера для YouTube
    isUserModeratorOrBroadcaster(chatItem) {
        const { isAnchor, isMod } = Utils.getYouTubeRoles(chatItem);
        return isAnchor || isMod;
    }
}

/* =======================
TELEGRAM SERVICE
======================= */

class TelegramService extends Platform {
    constructor(globalState, eventEmitter) {
        super('telegram', globalState, eventEmitter);
        this.bot = null;
        this.TELEGRAM_COMMAND_MAP = {
            '/song': '!song',
            '/skip': '!skip',
            '/queue': '!queue',
            '/pause': '!pause',
            '/play': '!play',
            '/stop': '!stop'  // ← ДОБАВЛЕНО
        };
    }

    async _connect() {
        this.bot = new TelegramBot(process.env.TG_BOT_TOKEN, {
            polling: true
        });

        this.setupEventListeners();
    }

    async _disconnect() {
        if (this.bot) {
            this.bot.stopPolling();
            this.bot = null;
        }
    }

    setupEventListeners() {
        this.bot.on('message', async (msg) => {
            await this.handleMessage(msg);
        });

        this.bot.on('polling_error', (error) => {
            console.error('⚠ Telegram polling error:', error.message);
            this.isConnected = false;
            this.globalState.setPlatformStatus('telegram', false);
            this.scheduleReconnect();
        });
    }

    async handleMessage(msg) {
        if (!msg.text) return;
        const chatId = msg.chat.id;
        const fromId = msg.from.id;
        const userId = fromId;
        const firstName = msg.from.first_name || '';
        const lastName = msg.from.last_name || '';
        const username = msg.from.username ? `@${msg.from.username}` : '';
        const user = `${firstName}${lastName ? ' ' + lastName : ''}${username ? ' (' + username + ')' : ''}`;
        let originalText = msg.text.trim();

        // === ОТЛАДОЧНОЕ ЛОГИРОВАНИЕ ===
        console.log('📥 Telegram message debug:', {
            chatId: chatId,
            fromId: fromId,
            user: user,
            text: originalText,
            chatType: msg.chat.type,
            isPrivate: msg.chat.type === 'private',
            ownerId: OWNER_ID,
            isOwner: msg.chat.type === 'private' && fromId === OWNER_ID
        });
        // ============================
    
        // ОПРЕДЕЛЯЕМ РОЛЬ ДО ВСЕГО
        const role = await this.getTelegramRole(msg);
        const isModeratorOrBroadcaster = this.isUserModeratorOrBroadcaster(role);

        // Convert commands
        let text = originalText;
        for (const tgCmd in this.TELEGRAM_COMMAND_MAP) {
            if (text === tgCmd || text.startsWith(tgCmd + ' ')) {
                text = text.replace(tgCmd, this.TELEGRAM_COMMAND_MAP[tgCmd]);
                break;
            }
        }

        // Команды премодерации - доступны только модераторам/стримеру
        if (text.startsWith('!yes ') || text.startsWith('!no ') || text === '!modqueue') {
            if (!isModeratorOrBroadcaster) {
                await this.bot.sendMessage(chatId, `❌ Только модераторы и стример могут управлять модерацией!`);
                return;
            }
        
            await this.handleModerationCommand({
                user: msg.from.username || msg.from.first_name,
                userId: fromId,
                text: text,
                userData: role
            });
            return;
        }

        // Commands
        if (text.startsWith('!song ')) {
            const cooldownMs = this.globalState.isVIPTelegram(fromId) ? 0 : Utils.getUnifiedCooldown({
                isAnchor: role === 'broadcaster',
                isMod: role === 'moderator',
                isSubscriber: false,
                isFollower: false
            });
        
            // ИСПОЛЬЗУЕМ СОХРАНЁННУЮ РОЛЬ
            const bypassModeration = isModeratorOrBroadcaster;
        
            await this.handleSongRequest({
                user: msg.from.username || msg.from.first_name,
                userId: fromId,
                text: text,
                cooldownMs: cooldownMs,
                isVIP: this.globalState.isVIPTelegram(fromId),
                bypassModeration: bypassModeration
            });
            return;
        }

        // ... остальные команды остаются без изменений ...
        if (text === '!skip') {
            const allowed = Utils.canUserSkipCurrentSong(
                this.globalState.songQueue,
                'telegram',
                userId,
                null,
                role
            );
            if (!allowed) {
                await this.bot.sendMessage(
                    chatId,
                    `❌ ${user}, ты можешь скипать только свой текущий трек`
                );
                return;
            }

            this.globalState.songQueue.current = null;
            const next = this.globalState.songQueue.next();
        
            if (next) {
                this.eventEmitter.emit('music.play', next);
            } else {
                this.eventEmitter.emit('music.stop');
            }
        
            this.eventEmitter.emit('queue.update');
            return;
        }

        if (text === '!stop') {
            if (role === 'user') {
                await this.bot.sendMessage(chatId, '⛔ Недостаточно прав');
                return;
            }

            this.globalState.songQueue.clearCurrent();
            this.globalState.songQueue.queue = [];
            this.globalState.songQueue.lastRequest.clear();
            this.eventEmitter.emit('music.stop');
            this.eventEmitter.emit('queue.update');
            await this.bot.sendMessage(chatId, `⏹ Воспроизведение остановлено, очередь очищена`);
            return;
        }

        if (text === '!queue') {
            this.handleQueueCommand(chatId);
            return;
        }

        if (text === '!pause' || text === '!play') {
            if (role === 'user') {
                await this.bot.sendMessage(chatId, '⛔ Недостаточно прав');
                return;
            }

            if (text === '!pause') {
                this.eventEmitter.emit('music.pause');
            } else {
                this.eventEmitter.emit('music.resume');
            }
            return;
        }

        // Regular chat
        this.emitChat(
            userId,
            `[TG] ${user}`,
            text
        );
    }

    // Переопределяем sendMessageToChat для Telegram
    sendMessageToChat(message) {
        // Для Telegram мы не можем отправить сообщение без chatId
        // Поэтому эмитим событие в мультичат, но не отправляем в Telegram
        this.eventEmitter.emit('chat', {
            platform: this.name,
            userId: 'system',
            nickname: this.name,
            text: message,
            messageId: this.generateMessageId()
        });
    }

    async getTelegramRole(msg) {
        if (msg.chat.type === 'private' && msg.from.id === OWNER_ID) {
            return 'broadcaster';
        }

        if ((msg.chat.type === 'supergroup' || msg.chat.type === 'group') && 
            Math.abs(OWNER_ID) === Math.abs(msg.chat.id)) {
            return 'broadcaster';
        }

        try {
            const member = await this.bot.getChatMember(msg.chat.id, msg.from.id);
            if (member.status === 'creator') return 'broadcaster';
            if (member.status === 'administrator') return 'moderator';
        } catch (e) {
            console.error('TG role check error:', e.message);
        }

        return 'user';
    }

    async handleVIPCommands(text, chatId, fromId) {
        if (text.startsWith('/vip ')) {
            const targetId = parseInt(text.split(' ')[1]);
            if (!isNaN(targetId)) {
                this.globalState.telegramVIPs.add(targetId);
                this.globalState.saveVIPs();
                await this.bot.sendMessage(chatId, `✅ Пользователь ${targetId} теперь VIP!`);
                // Также отправляем в мультичат
                this.sendMessageToChat(`✅ TG VIP добавлен: ${targetId}`);
            } else {
                await this.bot.sendMessage(chatId, `❌ Неверный ID`);
            }
            return;
        }

        if (text.startsWith('/unvip ')) {
            const targetId = parseInt(text.split(' ')[1]);
            if (!isNaN(targetId) && this.globalState.telegramVIPs.has(targetId)) {
                this.globalState.telegramVIPs.delete(targetId);
                this.globalState.saveVIPs();
                await this.bot.sendMessage(chatId, `❌ Пользователь ${targetId} больше не VIP`);
                this.sendMessageToChat(`❌ TG VIP удалён: ${targetId}`);
            } else {
                await this.bot.sendMessage(chatId, `❌ Пользователь не найден в VIP`);
            }
            return;
        }

        if (text === '/viplist') {
            if (this.globalState.telegramVIPs.size === 0) {
                await this.bot.sendMessage(chatId, `VIP-пользователей нет`);
            } else {
                await this.bot.sendMessage(chatId, `🌟 VIP:\n${[...this.globalState.telegramVIPs].join('\n')}`);
            }
            return;
        }
    }

    async handleQueueCommand(chatId) {
        const queue = this.globalState.songQueue;
        if (queue.queue.length > 0) {
            const list = queue.list();
            const current = queue.current ? 
                `🎶 Сейчас: ${queue.current.author} — ${queue.current.title}\n📜 Очередь: ${list}` :
                `📜 Очередь: ${list}`;
            await this.bot.sendMessage(chatId, current);
        } else {
            if (queue.current) {
                await this.bot.sendMessage(chatId, `🎶 Сейчас: ${queue.current.author} — ${queue.current.title}\n📭 Очередь пуста`);
            } else {
                await this.bot.sendMessage(chatId, `📭 Очередь пуста`);
            }
        }
    }

    // Проверка прав модератора/стримера для Telegram
    isUserModeratorOrBroadcaster(role) {
        return role === 'broadcaster' || role === 'moderator';
    }
}

/* =======================
DISCORD SERVICE
======================= */

class DiscordService extends Platform {
    constructor(globalState, eventEmitter) {
        super('discord', globalState, eventEmitter);
        this.client = null;
        this.chatChannel = null;
        this.statusChannel = null;
    }

    async _connect() {
        this.client = new Client({
            intents: [
                GatewayIntentBits.Guilds,
                GatewayIntentBits.GuildMessages,
                GatewayIntentBits.MessageContent
            ]
        });

        await this.client.login(process.env.DISCORD_BOT_TOKEN);

        return new Promise((resolve, reject) => {
            this.client.once('ready', async () => {
                console.log(`✅ Discord bot logged in as ${this.client.user.tag}`);
                try {
                    this.statusChannel = await this.client.channels.fetch(
                        process.env.DISCORD_CHANNEL_ID
                    );
                    this.chatChannel = await this.client.channels.fetch(
                        process.env.DISCORD_LIVE_CHAT
                    );
                    this.setupEventListeners();
                    this.isConnected = true;
                    this.globalState.setPlatformStatus('discord', true);
                    resolve();
                } catch (error) {
                    reject(error);
                }
            });

            this.client.on('error', (error) => {
                console.error('Discord client error:', error);
                reject(error);
            });
        });
    }

    async _disconnect() {
        if (this.client) {
            this.client.destroy();
            this.client = null;
        }
    }

    setupEventListeners() {
        this.client.on('messageCreate', async (msg) => {
            if (msg.author.bot) return;
            if (!this.chatChannel || msg.channel.id !== this.chatChannel.id) return;
        
            const text = msg.content?.trim();
            if (!text) return;
        
            const userId = msg.author.id;
            const username = msg.author.username;
        
            // Получаем участника сервера для проверки прав
            const member = await msg.guild.members.fetch(userId).catch(() => null);
        
            // Команды премодерации
            if (text.startsWith('!yes ') || text.startsWith('!no ') || text === '!modqueue') {
                if (!this.isUserModeratorOrBroadcaster(member)) {
                    await msg.reply(`❌ Только модераторы и владелец сервера могут управлять модерацией!`);
                    return;
                }
            
                await this.handleModerationCommand({
                    user: username,
                    userId: userId,
                    text: text,
                    userData: member
                });
                return;
            }

            // Commands
            if (text.startsWith('!song ')) {
                const cooldownMs = Utils.getUnifiedCooldown({
                    isAnchor: member?.id === msg.guild.ownerId,
                    isMod: this.isUserModeratorOrBroadcaster(member),
                    isSubscriber: false,
                    isFollower: false
                });
            
                const bypassModeration = this.isUserModeratorOrBroadcaster(member);
            
                await this.handleSongRequest({
                    user: username,
                    userId: userId,
                    text: text,
                    cooldownMs: cooldownMs,
                    bypassModeration: bypassModeration
                });
                return;
            }

            if (text === '!skip') {
                const allowed = Utils.canUserSkipCurrentSong(
                    this.globalState.songQueue,
                    'discord',
                    userId,
                    null,
                    member?.id === msg.guild.ownerId ? 'broadcaster' : 
                    this.isUserModeratorOrBroadcaster(member) ? 'moderator' : 'user'
                );
                if (!allowed) {
                    const errorMsg = `❌ ${username}, ты можешь скипать только свой текущий трек`;
                    await msg.reply(errorMsg);
                    this.sendMessageToChat(errorMsg);
                    return;
                }

                this.globalState.songQueue.current = null;
                const next = this.globalState.songQueue.next();
    
                if (next) {
                    this.eventEmitter.emit('music.play', next);
                    const response = `⏭ Следующий трек: ${next.author} — ${next.title}`;
                    await msg.reply(response);
                    this.sendMessageToChat(response);
                } else {
                    this.eventEmitter.emit('music.stop');
                    const response = `⏹ Очередь пуста, воспроизведение остановлено`;
                    await msg.reply(response);
                    this.sendMessageToChat(response);
                }
    
                this.eventEmitter.emit('queue.update');
                return;
            }

            if (text === '!queue') {
                const queue = this.globalState.songQueue;
                let response;
                if (queue.queue.length > 0) {
                    const list = queue.list();
                    const current = queue.current ? 
                        `🎶 Сейчас: ${queue.current.author} — ${queue.current.title}\n📜 Очередь: ${list}` :
                        `📜 Очередь: ${list}`;
                    response = current;
                } else {
                    if (queue.current) {
                        response = `🎶 Сейчас: ${queue.current.author} — ${queue.current.title}\n📭 Очередь пуста`;
                    } else {
                        response = `📭 Очередь пуста`;
                    }
                }
                await msg.reply(response);
                this.sendMessageToChat(response);
                return;
            }

            if (text === '!pause' || text === '!play' || text === '!stop') {
                if (!this.isUserModeratorOrBroadcaster(member)) {
                    const errorMsg = `❌ ${username}, команда доступна только модераторам и владельцу сервера!`;
                    await msg.reply(errorMsg);
                    this.sendMessageToChat(errorMsg);
                    return;
                }

                if (text === '!pause') {
                    this.eventEmitter.emit('music.pause');
                    const response = `⏸ Трек поставлен на паузу`;
                    await msg.reply(response);
                    this.sendMessageToChat(response);
                } else if (text === '!play') {
                    this.eventEmitter.emit('music.resume');
                    const response = `▶️ Продолжаем воспроизведение`;
                    await msg.reply(response);
                    this.sendMessageToChat(response);
                } else if (text === '!stop') {
                    this.globalState.songQueue.clearCurrent();
                    this.globalState.songQueue.queue = [];
                    this.globalState.songQueue.lastRequest.clear();
                    this.eventEmitter.emit('music.stop');
                    this.eventEmitter.emit('queue.update');
                    const response = `⏹ Воспроизведение остановлено, очередь очищена`;
                    await msg.reply(response);
                    this.sendMessageToChat(response);
                }
                return;
            }

            // Regular chat
            this.emitChat(
                userId,
                `[DC] ${username}`,
                text
            );
        });

        this.client.on('disconnect', () => {
            console.log('⚠ Discord disconnected');
            this.isConnected = false;
            this.globalState.setPlatformStatus('discord', false);
            this.scheduleReconnect();
        });
    }

    async handleQueueCommand(msg) {
        const queue = this.globalState.songQueue;
        if (queue.queue.length > 0) {
            const list = queue.list();
            const current = queue.current ? 
                `🎶 Сейчас: ${queue.current.author} — ${queue.current.title}\n📜 Очередь: ${list}` :
                `📜 Очередь: ${list}`;
            await msg.reply(current);
        } else {
            if (queue.current) {
                await msg.reply(`🎶 Сейчас: ${queue.current.author} — ${queue.current.title}\n📭 Очередь пуста`);
            } else {
                await msg.reply(`📭 Очередь пуста`);
            }
        }
    }

    // Проверка прав модератора/владельца сервера для Discord
    isUserModeratorOrBroadcaster(member) {
        if (!member) return false;
    
        // Владелец сервера
        if (member.id === member.guild.ownerId) return true;
    
        // Проверяем права администратора или модератора
        return member.permissions.has(PermissionsBitField.Flags.Administrator) ||
                member.permissions.has(PermissionsBitField.Flags.ManageMessages) ||
                member.permissions.has(PermissionsBitField.Flags.KickMembers);
    }
}

/* =======================
STATUS UPDATER
======================= */

class StatusUpdater {
    constructor(globalState, eventEmitter) {
        this.globalState = globalState;
        this.eventEmitter = eventEmitter;
        this.discordClient = null;
        this.discordStatusChannel = null;
        this.discordMessageId = null;
        this.discordUpdateLock = false;
        this.tgBot = null;
        this.announceMessageId = null;
        this.updateInterval = null;
        this.init();
    }

    async init() {
        await this.setupDiscord();
        this.setupTelegram();
        this.startUpdateInterval();
        this.setupEventListeners();
    }

    async setupDiscord() {
        try {
            this.discordClient = new Client({
                intents: [GatewayIntentBits.Guilds]
            });
            
            await this.discordClient.login(process.env.DISCORD_BOT_TOKEN);
            
            this.discordClient.once('ready', async () => {
                this.discordStatusChannel = await this.discordClient.channels.fetch(
                    process.env.DISCORD_CHANNEL_ID
                );
                console.log('✅ Discord status channel ready');
            });
        } catch (err) {
            console.error('⚠ Discord status setup failed:', err.message);
        }
    }

    setupTelegram() {
        try {
            this.tgBot = new TelegramBot(process.env.TG_BOT_TOKEN, { polling: false });
        } catch (err) {
            console.error('⚠ Telegram status setup failed:', err.message);
        }
    }

    setupEventListeners() {
        this.eventEmitter.on('platform.status', () => {
            //this.updateAllStatuses();
        });
    }

    startUpdateInterval() {
        // ⚡ ИЗМЕНИТЕ ЗДЕСЬ: текущее значение 120 секунд (120_000)
        this.updateInterval = setInterval(() => {
            //this.updateAllStatuses();
        }, 120_000); // ← Это значение в миллисекундах
    }

    async updateAllStatuses() {
        try {
            const twitchLive = await this.isTwitchLiveCached();
            const anyLive = twitchLive || 
                this.globalState.getPlatformStatus('youtube') || 
                this.globalState.getPlatformStatus('tiktok');
            
            this.updateStreamStart();
            
            const rawSpeedMBps = await this.getCachedUploadSpeed();
            const uploadMbps = rawSpeedMBps ? +(rawSpeedMBps * 8).toFixed(1) : null;
            
            const text = this.buildStreamStatusText({
                twitchLive,
                ytLive: this.globalState.getPlatformStatus('youtube'),
                tiktokLive: this.globalState.getPlatformStatus('tiktok'),
                uploadMbps
            });
            
            await this.updateDiscordStatus(text);
            await this.updateTelegramStatus(text);
            
        } catch (e) {
            console.error('Stream status update error:', e.message);
        }
    }

    async isTwitchLiveCached() {
        if (Date.now() - this.globalState.twitchLiveCache.ts < 30_000) {
            return this.globalState.twitchLiveCache.value;
        }
        const v = await this.isTwitchLive();
        this.globalState.twitchLiveCache = { value: v, ts: Date.now() };
        return v;
    }

    async isTwitchLive() {
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
        return Array.isArray(json.data) && json.data.length > 0;
    }

    async getUploadSpeedMbps() {
        try {
            const sizeBytes = 512 * 1024;
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

    async getCachedUploadSpeed() {
        if (Date.now() - this.globalState.cachedUpload.ts < 60_000) {
            return this.globalState.cachedUpload.value;
        }

        const v = await this.getUploadSpeedMbps();
        this.globalState.cachedUpload = { value: v, ts: Date.now() };
        return v;
    }

    updateStreamStart() {
        if (!this.globalState.streamStartTs) {
            this.globalState.streamStartTs = Date.now();
        }
    }

    buildStreamStatusText({ twitchLive, ytLive, tiktokLive, uploadMbps }) {
        const platformLine = [
            `Twitch ${twitchLive ? '🟢' : '🔴'}`,
            `YouTube ${ytLive ? '🟢' : '🔴'}`,
            `TikTok ${tiktokLive ? '🟢' : '🔴'}`
        ].join(' | ');

        const speedLine = uploadMbps
            ? `${Utils.uploadIndicator(uploadMbps)} ${uploadMbps} Mbps`
            : `⚪ n/a`;

        const uptime = Utils.formatUptime(this.globalState.streamStartTs);

        return (
            `Стрим идёт на:\n` +
            `${platformLine} | ${speedLine}\n` +
            `⏱ Аптайм: ${uptime}\n\n` +
            `Чаты:\n` +
            `💭 TG: https://t.me/+q9BrXnjmFCFmMmQy\n` +
            `💭 DISCORD: https://discord.com/channels/735134140697018419/1464255245009031279`
        );
    }

    async updateDiscordStatus(text) {
        if (!this.discordStatusChannel) return;
        if (this.discordUpdateLock) return;

        this.discordUpdateLock = true;

        try {
            if (this.discordMessageId) {
                const msg = await this.discordStatusChannel.messages.fetch(this.discordMessageId);
                await msg.edit(text);
            } else {
                const msg = await this.discordStatusChannel.send(text);
                this.discordMessageId = msg.id;
            }
        } catch (e) {
            console.error('Discord update error:', e.message);
            this.discordMessageId = null;
        } finally {
            setTimeout(() => { this.discordUpdateLock = false; }, 500);
        }
    }

    async updateTelegramStatus(text) {
        if (!this.tgBot) return;

        try {
            if (this.announceMessageId) {
                await this.tgBot.editMessageText(text, {
                    chat_id: TELEGRAM_CHANNEL_ID,
                    message_id: this.announceMessageId,
                    disable_web_page_preview: true
                });
            } else {
                const msg = await this.tgBot.sendMessage(TELEGRAM_CHANNEL_ID, text, {
                    disable_web_page_preview: true
                });
                this.announceMessageId = msg.message_id;
            }
        } catch (e) {
            console.error('Telegram status update error:', e.message);
            this.announceMessageId = null;
        }
    }
}

/* =======================
TERRARIA WEBSOCKET SERVER
======================= */

class TerrariaWebSocketServer {
    constructor(globalState, eventEmitter) {
        this.globalState = globalState;
        this.eventEmitter = eventEmitter;
        this.wss = null;
        this.discordChatSender = new DiscordChatSender();
        this.initializeDiscordChat();
    }
    
    async initializeDiscordChat() {
        await this.discordChatSender.initialize();
    }

    start(port = 21214) {
        this.wss = new WebSocket.Server({ port });
        console.log(`✅ Terraria WS → ws://localhost:${port}`);
        
        this.wss.on('connection', (ws) => {
            ws.send(JSON.stringify({ 
                event: 'chatHistory', 
                data: this.globalState.chatHistory 
            }));
            
            ws.on('message', (message) => {
                try {
                    const d = JSON.parse(message);
                    if (d.event === 'trackEnded') {
                        const next = this.globalState.songQueue.next();
                        if (next) {
                            this.eventEmitter.emit('music.play', next);
                        } else {
                            this.eventEmitter.emit('music.stop');
                        }
                        this.eventEmitter.emit('queue.update');
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

        this.setupEventListeners();
    }

    setupEventListeners() {
        this.eventEmitter.on('chat', (data) => {
            this.globalState.addToChatHistory(data.platform, {
                userId: data.userId,
                nickname: data.nickname,
                text: data.text,
                timestamp: data.timestamp,
                time: data.time,
                messageId: data.messageId,      // ← ДОБАВЛЕНО
                extraClass: data.extraClass

            });

            this.broadcast({
                event: 'chat',
                platform: data.platform,
                data: {
                    userId: data.userId,
                    nickname: data.nickname,
                    text: data.text,
                    timestamp: data.timestamp,
                    time: data.time,
                    messageId: data.messageId,   // ← ДОБАВЛЕНО
                    extraClass: data.extraClass  // ← ДОБАВЛЕНО
                }
            });

            // Отправляем в Discord (кроме системных сообщений и самого Discord)
            //if (data.userId !== 'system' && data.platform !== 'discord') {
            if (data.userId !== 'system') {
                this.discordChatSender.sendMessage({
                    platform: data.platform,
                    username: data.nickname,
                    text: data.text
                });
            }
        });

        // ДОБАВЬ ЭТО В setupEventListeners() ВМЕСТЕ С ОСТАЛЬНЫМИ this.eventEmitter.on(...)
        this.eventEmitter.on('chat.update', (data) => {
            this.broadcast({
                event: 'chat.update',
                messageId: data.messageId,
                platform: data.platform,
                text: data.text,
                status: data.status,
                extraClass: data.extraClass
            });
        });

        this.eventEmitter.on('music.play', (song) => {
            this.globalState.songQueue.current = song;
            this.broadcast({
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
        });

        this.eventEmitter.on('music.stop', () => {
            this.broadcast({
                event: 'music_stop'
            });
        });

        this.eventEmitter.on('music.pause', () => {
            this.broadcast({
                event: 'music_pause'
            });
        });

        this.eventEmitter.on('music.resume', () => {
            this.broadcast({
                event: 'music_play'
            });
        });

        this.eventEmitter.on('queue.update', () => {
            this.broadcast({
                event: 'queue',
                data: {
                    list: this.globalState.songQueue.queue,
                    current: this.globalState.songQueue.current
                }
            });
        });

        this.eventEmitter.on('follow', (data) => {
            this.broadcast({
                event: 'follow',
                platform: data.platform,
                data: data.data
            });
        });

        this.eventEmitter.on('subscribe', (data) => {
            this.broadcast({
                event: 'subscribe',
                platform: data.platform,
                data: data.data
            });
        });

        this.eventEmitter.on('gift', (data) => {
            this.broadcast({
                event: 'gift',
                platform: data.platform,
                data: data.data
            });
        });

        this.eventEmitter.on('like', (data) => {
            this.broadcast({
                event: 'like',
                platform: data.platform,
                data: data.data
            });
        });

        this.eventEmitter.on('join', (data) => {
            this.broadcast({
                event: 'join',
                platform: data.platform,
                data: data.data
            });
        });
    }

    broadcast(event) {
        if (!this.wss) return;
        const msg = JSON.stringify(event);
        this.wss.clients.forEach(client => {
            if (client.readyState === WebSocket.OPEN) {
                client.send(msg);
            }
        });
    }
}

/* =======================
HTTP SERVER
======================= */

class HttpServer {
    constructor(eventEmitter) {
        this.app = express();
        this.eventEmitter = eventEmitter;
        this.setupMiddleware();
        this.setupRoutes();
    }

    setupMiddleware() {
        this.app.use(express.static(path.join(__dirname, 'public')));
        this.app.use(express.json());
    }

    setupRoutes() {
        this.app.post('/twitch/eventsub', (req, res) => {
            const type = req.get('Twitch-Eventsub-Message-Type');

            if (type === 'webhook_callback_verification')
                return res.send(req.body.challenge);

            if (!this.verifyTwitchSignature(req))
                return res.status(403).end();

            if (type === 'notification') {
                const { subscription, event } = req.body;

                switch (subscription.type) {
                    case 'channel.follow':
                        this.eventEmitter.emit('follow', {
                            platform: 'twitch',
                            data: {
                                userId: event.user_id,
                                nickname: `[Twitch] ${event.user_name}`
                            }
                        });
                        break;

                    case 'channel.subscribe':
                        this.eventEmitter.emit('subscribe', {
                            platform: 'twitch',
                            data: {
                                userId: event.user_id,
                                nickname: `[Twitch] ${event.user_name}`
                            }
                        });
                        break;

                    case 'channel.subscription.gift':
                        this.eventEmitter.emit('gift', {
                            platform: 'twitch',
                            data: {
                                userId: event.user_id,
                                nickname: `[Twitch] ${event.user_name}`,
                                amount: event.total
                            }
                        });
                        break;
                }
            }

            res.status(200).end();
        });
    }

    verifyTwitchSignature(req) {
        const message =
            req.get('Twitch-Eventsub-Message-Id') +
            req.get('Twitch-Eventsub-Message-Timestamp') +
            JSON.stringify(req.body);
        const expected =
            'sha256=' +
            crypto.createHmac('sha256', EVENTSUB_SECRET).update(message).digest('hex');
        return expected === req.get('Twitch-Eventsub-Message-Signature');
    }

    start(port = 3000) {
        this.app.listen(port, () => {
            console.log(`🌐 HTTP → :${port}`);
        });
    }
}

/* =======================
MAIN APPLICATION
======================= */

class Application {
    constructor() {
        this.globalState = new GlobalState();
        this.eventEmitter = new EventEmitter(); // ⚡ ИСПРАВЛЕНО: используем импортированный EventEmitter
        this.services = new Map();
        this.terrariaServer = null;
        this.httpServer = null;
        this.statusUpdater = null;
    }

    async initialize() {
        console.log('🚀 Starting application...');
        
        // Initialize servers
        this.terrariaServer = new TerrariaWebSocketServer(this.globalState, this.eventEmitter);
        this.httpServer = new HttpServer(this.eventEmitter);
        
        // Start servers
        this.terrariaServer.start();
        this.httpServer.start();
        
        // Initialize status updater
        this.statusUpdater = new StatusUpdater(this.globalState, this.eventEmitter);
        
        // Initialize all services
        await this.initializeServices();
        
        console.log('✅ Application initialized');
    }

    async initializeServices() {
        const serviceConfigs = [
            { name: 'tiktok', class: TikTokService },
            { name: 'twitch', class: TwitchService },
            { name: 'youtube', class: YouTubeService },
            { name: 'telegram', class: TelegramService },
            { name: 'discord', class: DiscordService }
        ];

        for (const config of serviceConfigs) {
            try {
                console.log(`🔄 Initializing ${config.name}...`);
                const service = new config.class(this.globalState, this.eventEmitter);
                this.services.set(config.name, service);
                
                // Start connection asynchronously
                service.connect().catch(err => {
                    console.error(`❌ Failed to initialize ${config.name}:`, err.message);
                });
                
                // Small delay between initializations
                await new Promise(resolve => setTimeout(resolve, 1000));
                
            } catch (error) {
                console.error(`❌ Error initializing ${config.name}:`, error.message);
            }
        }
    }

    async shutdown() {
        console.log('🛑 Shutting down application...');
        
        // Disconnect all services
        for (const [name, service] of this.services) {
            try {
                console.log(`🛑 Disconnecting ${name}...`);
                await service.disconnect();
            } catch (error) {
                console.error(`❌ Error disconnecting ${name}:`, error.message);
            }
        }
        
        console.log('✅ Application shutdown complete');
    }
}

/* =======================
DISCORD CHAT SENDER
======================= */

class DiscordChatSender {
    constructor() {
        this.client = null;
        this.chatChannel = null;
        this.icons = {
            twitch: '🟣',
            youtube: '🔴',
            tiktok: '⚫',
            telegram: '🔵',
            discord: '🔘'
        };
    }

    async initialize() {
        try {
            this.client = new Client({
                intents: [
                    GatewayIntentBits.Guilds,
                    GatewayIntentBits.GuildMessages
                ]
            });

            await this.client.login(process.env.DISCORD_BOT_TOKEN);

            return new Promise((resolve) => {
                this.client.once('ready', async () => {
                    console.log(`✅ Discord chat sender logged in as ${this.client.user.tag}`);
                    try {
                        this.chatChannel = await this.client.channels.fetch(
                            process.env.DISCORD_LOG_CHANNEL_ID
                        );
                        resolve();
                    } catch (error) {
                        console.error('❌ Discord chat channel not found:', error.message);
                        resolve();
                    }
                });
            });
        } catch (error) {
            console.error('❌ Discord chat sender failed:', error.message);
        }
    }

    async sendMessage({ platform, username, text }) {
        if (!this.chatChannel || !this.client) {
            console.log('Discord chat channel not available');
            return;
        }

        const icon = this.icons[platform] ?? '💬';
        const message = `${icon} **${username}** :\n${text}`;

        try {
            await this.chatChannel.send({
                content: message.slice(0, 1900)
            });
        } catch (error) {
            console.error('Ошибка отправки сообщения в Discord:', error);
        }
    }
}

/* =======================
MAIN EXECUTION
======================= */

async function main() {
    const app = new Application();
    
    // Handle application shutdown
    process.on('SIGINT', async () => {
        await app.shutdown();
        process.exit(0);
    });
    
    process.on('SIGTERM', async () => {
        await app.shutdown();
        process.exit(0);
    });
    
    try {
        await app.initialize();
    } catch (error) {
        console.error('❌ Application startup failed:', error);
        process.exit(1);
    }
}

// Start the application
main().catch(console.error);