// TikFinityClient.cs
using ImapoTikTokIntegrationMod;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

public class TikFinityClient : ModSystem
{
    private static List<SubscriberDatabaseEntry> subscriberDatabase = new List<SubscriberDatabaseEntry>();
    private static HashSet<string> veteranSpawnedThisSession = new HashSet<string>();
    public static HashSet<string> SubscriberIds = new HashSet<string>();
    private const int MAX_VIEWERS = 500;
    private static List<GiftDatabaseEntry> giftDatabase = new();
    public static HashSet<string> GiftGiverIds = new();
    private static bool _hasLoggedFirstMessage = false;
    private static SafeWebSocketClient safeSocket;
    private static bool _hasShownStreamerName = false;
    private static Dictionary<string, int> likeComboCounter = new Dictionary<string, int>();
    internal static bool worldLoaded = false;
    private static readonly HashSet<string> sharedViewers = new();

    // -------------------------
    // Жизненный цикл ModSystem
    // -------------------------
    public override void OnWorldLoad()
    {
        TikFinityDatabase.ImportGiftDatabase();
        TikFinityDatabase.ImportViewerDatabase();
        TikFinityDatabase.ImportSubscriberDatabase();
        TikFinityDatabase.ImportModeratorDatabase();
        StartClient();
        worldLoaded = true;
        sharedViewers.Clear();
        // ОЧИСТКА СПАВН-КОНТРОЛЛЕРА
        SpawnController.OnWorldUnload(); // ← НОВАЯ СТРОКА
    }

    public override void OnWorldUnload()
    {
        worldLoaded = false;
        StopClientAsync().ConfigureAwait(false);
        TikFinityDatabase.UpdateViewerDatabaseJson();
        veteranSpawnedThisSession.Clear();
        sharedViewers.Clear();
        // ОЧИСТКА СПАВН-КОНТРОЛЛЕРА
        SpawnController.OnWorldUnload(); // ← НОВАЯ СТРОКА
    }

    private void StartClient()
    {
        _hasShownStreamerName = false;
        likeComboCounter.Clear();

        safeSocket = new SafeWebSocketClient("ws://localhost:21214/", 5000);
        safeSocket.OnMessageReceived = HandleMessageWrapper;
        safeSocket.Start();

        Main.NewText("[TikFinity] SafeWebSocketClient запущен", 50, 255, 50);
    }

    private async Task StopClientAsync()
    {
        if (safeSocket != null)
        {
            await safeSocket.StopAsync();
            safeSocket = null;
        }
    }

    private void HandleMessageWrapper(string json)
    {
        if (!worldLoaded)
        {
            // Игнорируем все сообщения TikTok, пока мир не загружен
            return;
        }
        try
        {
            HandleMessage(json);
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                .Logger.Warn($"[Parse Error] Wrapper error: {ex.Message}");
        }
    }

    // -------------------------
    // Основной обработчик сообщений
    // -------------------------
    private static void SetStreamer(string newStreamer)
    {
        newStreamer = TikFinityDatabase.MakeSafeFolderName(newStreamer);
        if (newStreamer == TikFinityDatabase.CurrentStreamerKey)
            return;

        TikFinityDatabase.CurrentStreamerKey = newStreamer;
        Directory.CreateDirectory(TikFinityDatabase.GetStreamerPath());

        Main.NewText($"[TikFinity] Connected to stream: {newStreamer}", Color.Cyan);
        _hasShownStreamerName = true;

        // Очистка кэшей текущей сессии через TikFinityDatabase
        TikFinityDatabase.ClearCaches();
    }


    private void HandleMessage(string json)
    {
        // ===== PRE-FILTER (самое важное) =====
        if (string.IsNullOrWhiteSpace(json))
            return;

        json = json.Trim();

        if (json.Length < 2)
            return;

        // Tikfinity иногда шлёт мусор: "15k", "ok", "ping" и т.п.
        if (json[0] != '{' && json[0] != '[')
        {
            ModContent.GetInstance<ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                .Logger.Debug($"[Tikfinity] Skipped non-JSON: {json}");
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // Битый JSON — просто игнорируем
            return;
        }
        catch
        {
            return;
        }

        using (doc)
        {
            try
            {
                var root = doc.RootElement;

                // ----- event / data -----
                string eventType =
                    root.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.String
                        ? ev.GetString() ?? ""
                        : "";

                JsonElement data =
                    root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                        ? d
                        : root;

                // ----- streamer name (один раз) -----
                if (eventType == "roomUser" && !_hasShownStreamerName)
                {
                    if (root.TryGetProperty("tikfinityUsername", out var sn) &&
                        sn.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(sn.GetString()))
                    {
                        SetStreamer(sn.GetString().Trim());
                    }
                    else if (data.ValueKind == JsonValueKind.Object &&
                             data.TryGetProperty("tikfinityUsername", out var sn2) &&
                             sn2.ValueKind == JsonValueKind.String &&
                             !string.IsNullOrWhiteSpace(sn2.GetString()))
                    {
                        SetStreamer(sn2.GetString().Trim());
                    }
                }

                // ----- viewer info -----
                string key = ExtractViewerKey(data);
                if (string.IsNullOrWhiteSpace(key))
                    return;

                string nickname = ExtractNickname(data);

                ExtractUserFlags(
                    root,
                    out bool isSubscriber,
                    out bool isModerator,
                    out bool isFollowing
                );
                /*
                ModContent.GetInstance<ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                    .Logger.Debug($"[Tikfinity RAW] {json}");
                */
                // ===== EVENT HANDLING =====
                switch (eventType)
                {
                    case "":
                    case "join":
                    case "roomUser":
                    case "member":
                        HandleJoinEvent(key, nickname);
                        AddOrUpdateViewer(
                            key,
                            nickname,
                            isSubscriber,
                            isModerator,
                            isFollowing,
                            string.IsNullOrEmpty(eventType) ? "Unknown" : eventType
                        );
                        break;

                    case "chat":
                    case "connect":
                        HandleChatEvent(
                            key,
                            nickname,
                            isSubscriber,
                            isModerator,
                            isFollowing,
                            data
                        );
                        break;

                    case "like":
                        ProcessLikeEvent(data, nickname);
                        break;

                    case "gift":
                        {
                            int giftCount = 1;
                            int giftPrice = 1;

                            if (data.TryGetProperty("repeatCount", out var rc) &&
                                rc.ValueKind == JsonValueKind.Number &&
                                rc.TryGetInt32(out int rcInt))
                            {
                                giftCount = Math.Max(1, rcInt);
                            }

                            if (data.TryGetProperty("gift", out var giftElem) &&
                                giftElem.ValueKind == JsonValueKind.Object &&
                                giftElem.TryGetProperty("diamondCount", out var dc) &&
                                dc.ValueKind == JsonValueKind.Number &&
                                dc.TryGetInt32(out int dcInt))
                            {
                                giftPrice = Math.Max(1, dcInt);
                            }

                            TikFinityDatabase.AddGiftDatabase(key, nickname, giftCount);
                            GiftEnemySpawner.SpawnGiftEnemy(nickname, giftCount, giftPrice);
                            break;
                        }

                    case "follow":
                        HandleSubscribeEvent(key, nickname, isModerator, isFollowing);
                        break;

                    case "share":
                    case "subscribe":
                        HandleShareEvent(key, nickname, isModerator, isFollowing);
                        break;

                    default:
                        HandleJoinEvent(key, nickname);
                        AddOrUpdateViewer(
                            key,
                            nickname,
                            isSubscriber,
                            isModerator,
                            isFollowing,
                            eventType
                        );
                        break;
                }
            }
            catch
            {
                // Любая логическая ошибка внутри — молча игнорируем
                // WS-поток НИКОГДА не должен падать
            }
        }
    }

    private static void HandleShareEvent(string key, string nickname, bool isModerator, bool isFollowing)
    {
        if (string.IsNullOrEmpty(key))
            return;

        // ❌ уже делился — игнор
        if (sharedViewers.Contains(key))
            return;

        sharedViewers.Add(key);

        AddOrUpdateViewer(key, nickname, isSubscriber: false, isModerator, isFollowing, "Share");

        if (Main.netMode == NetmodeID.MultiplayerClient)
            return;

        ApplyRandomBuffFromShare();
        SpawnController.SpawnShareSlime(nickname);
    }

    private static void ApplyRandomBuffFromShare()
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) return;

        Main.QueueMainThreadAction(() =>
        {
            // Находим активного игрока (в одиночке — Main.LocalPlayer, на сервере — первого активного)
            Player targetPlayer = null;
            if (Main.netMode == NetmodeID.SinglePlayer)
            {
                targetPlayer = Main.LocalPlayer;
            }
            else if (Main.netMode == NetmodeID.Server)
            {
                foreach (var plr in Main.player)
                {
                    if (plr.active && !plr.dead)
                    {
                        targetPlayer = plr;
                        break;
                    }
                }
            }

            if (targetPlayer == null || !targetPlayer.active) return;

            // Список безопасных и весёлых баффов (все из Terraria по умолчанию)
            int[] shareBuffs = new int[]
            {
                BuffID.Crate,           // Crate — +10 к защите на 10 сек
                BuffID.Lucky,           // Lucky — шанс найти больше лута
                BuffID.Sunflower,       // Sunflower — уменьшает spawn delay
                BuffID.WaterCandle,     // Water Candle — ускоряет spawn мобов (весело!)
                BuffID.Campfire,        // Campfire — +5 к регену HP
                BuffID.SugarRush,       // Sugar Rush — +10% к скорости
                BuffID.Gills,           // Gills — дышать под водой
                BuffID.Shine,           // Shine — светится
                BuffID.Mining,          // Mining — быстрее копаешь
                BuffID.Builder,         // Builder — быстрее строишь
                BuffID.WellFed,         // Well Fed — +2 к защите, +5% HP
                BuffID.Lifeforce,       // Lifeforce — +20% к макс. HP
                BuffID.Honey,           // Honey — быстрый реген HP
            };

            // Выбираем случайный бафф
            int randomBuff = shareBuffs[Main.rand.Next(shareBuffs.Length)];
            int duration = 600; // 10 секунд (600 тиков)

            // Применяем бафф
            targetPlayer.AddBuff(randomBuff, duration);

            // Опционально: логируем для отладки
            // var buffName = Lang.GetBuffName(randomBuff);
            // Main.NewText($"Applied buff: {buffName}", Color.Gold);
        });
    }

    public class ModeratorInfo
    {
        public string Key { get; set; }               // uniqueId или ник
        public string Nickname { get; set; }          // ник модератора
        public bool IsModerator { get; set; } = true; // всегда true
        public string SourceEvent { get; set; }       // join / chat / gift / etc.
        public string TextMessage { get; set; }       // для chat-событий
        public string Time { get; set; }              // время последнего события
    }

    private void AddOrUpdateModerator(string key, string nickname, string sourceEvent, string textMessage = null)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(nickname))
            return;

        string now = DateTime.Now.ToString("dd.MM.yy HH:mm:ss");

        if (ModDataStorage.ModeratorDatabase.TryGetValue(key, out var existing))
        {
            existing.Nickname = nickname;
            existing.SourceEvent = sourceEvent;
            existing.TextMessage = textMessage;
            existing.Time = now;
        }
        else
        {
            ModDataStorage.ModeratorDatabase[key] = new ModeratorInfo
            {
                Key = key,
                Nickname = nickname,
                IsModerator = true,
                SourceEvent = sourceEvent,
                TextMessage = textMessage,
                Time = now
            };
        }

        TikFinityDatabase.UpdateModeratorDatabaseJson();
    }

    public class GiftDatabaseEntry
    {
        public string Key { get; set; }
        public string Nickname { get; set; }
        public int Coins { get; set; }
        public string SourceEvent { get; set; } = "gift";
        public string Time { get; set; }
    }


    private static void TrimViewerDatabase()
    {
        if (ModDataStorage.ViewerDatabase.Count <= MAX_VIEWERS)
            return;

        var ordered = ModDataStorage.ViewerDatabase
            .OrderBy(v =>
                DateTime.TryParse(v.Value.Time, out var t)
                    ? t
                    : DateTime.MinValue
            )
            .ToList();

        int removeCount = ModDataStorage.ViewerDatabase.Count - MAX_VIEWERS;

        for (int i = 0; i < removeCount; i++)
        {
            ModDataStorage.ViewerDatabase.Remove(ordered[i].Key);
        }
    }

    public class SubscriberDatabaseEntry
    {
        public string Key { get; set; }
        public string Nickname { get; set; }
        public DateTime Timestamp { get; set; }
        public string EventType { get; set; } // subscribe, member, etc.
                                                // Человекочитаемая дата
        public string Time { get; set; }
    }

    

    public class ViewerInfo
    {
        public string Key { get; set; }
        public string Nickname { get; set; }
        public string TextMessage { get; set; }
        public bool IsSubscriber { get; set; }
        public bool IsModerator { get; set; }
        public bool IsFollowing { get; set; }
        // Новое поле для логирования источника события
        public string SourceEvent { get; set; }
        // Человекочитаемое время последнего события
        public string Time { get; set; }
    }

    // -------------------------
    // Вспомогательные экстракторы
    // -------------------------
    private string ExtractNickname(JsonElement root)
    {
        string nickname = "";

        if (root.TryGetProperty("nickname", out var nickProp) && !string.IsNullOrWhiteSpace(nickProp.GetString()))
        {
            nickname = nickProp.GetString().Trim();
        }
        else if (root.TryGetProperty("uniqueId", out var idProp) && !string.IsNullOrWhiteSpace(idProp.GetString()))
        {
            nickname = idProp.GetString().Trim();
            if (nickname.StartsWith("@")) nickname = nickname.Substring(1);
        }
        else if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object)
        {
            if (dataProp.TryGetProperty("nickname", out var dataNickProp) && !string.IsNullOrWhiteSpace(dataNickProp.GetString()))
                nickname = dataNickProp.GetString().Trim();
            else if (dataProp.TryGetProperty("uniqueId", out var dataIdProp) && !string.IsNullOrWhiteSpace(dataIdProp.GetString()))
            {
                nickname = dataIdProp.GetString().Trim();
                if (nickname.StartsWith("@")) nickname = nickname.Substring(1);
            }
            else if (dataProp.TryGetProperty("user", out var userProp) && userProp.ValueKind == JsonValueKind.Object)
            {
                if (userProp.TryGetProperty("nickname", out var userNickProp) && !string.IsNullOrWhiteSpace(userNickProp.GetString()))
                    nickname = userNickProp.GetString().Trim();
                else if (userProp.TryGetProperty("uniqueId", out var userIdProp) && !string.IsNullOrWhiteSpace(userIdProp.GetString()))
                {
                    nickname = userIdProp.GetString().Trim();
                    if (nickname.StartsWith("@")) nickname = nickname.Substring(1);
                }
            }
        }
        else if (root.TryGetProperty("user", out var userElement) && userElement.ValueKind == JsonValueKind.Object)
        {
            if (userElement.TryGetProperty("nickname", out var userNickProp) && !string.IsNullOrWhiteSpace(userNickProp.GetString()))
                nickname = userNickProp.GetString().Trim();
            else if (userElement.TryGetProperty("uniqueId", out var userIdProp) && !string.IsNullOrWhiteSpace(userIdProp.GetString()))
            {
                nickname = userIdProp.GetString().Trim();
                if (nickname.StartsWith("@")) nickname = nickname.Substring(1);
            }
        }

        // ====== БЕЗОПАСНОЕ ОБРЕЗАНИЕ С ЭМОДЗИ ======
        if (!string.IsNullOrEmpty(nickname))
        {
            int maxLength = 27;

            // Используем StringInfo для правильного подсчёта графем (учитывает эмодзи)
            var stringInfo = new StringInfo(nickname);
            if (stringInfo.LengthInTextElements > maxLength)
            {
                nickname = stringInfo.SubstringByTextElements(0, maxLength) + "...";
            }
        }

        return nickname;
    }

    // Возвращает стабильный ключ: сначала uniqueId (если есть), иначе nickname
    private string ExtractViewerKey(JsonElement root)
    {
        // Ищем uniqueId в корне
        if (root.TryGetProperty("uniqueId", out var idProp))
        {
            string id = idProp.GetString();
            if (!string.IsNullOrWhiteSpace(id))
                return id.Trim();
        }

        // Ищем uniqueId в data
        if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object)
        {
            if (dataProp.TryGetProperty("uniqueId", out var dataIdProp))
            {
                string id = dataIdProp.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                    return id.Trim();
            }

            // Проверяем data.user.uniqueId
            if (dataProp.TryGetProperty("user", out var userProp) && userProp.ValueKind == JsonValueKind.Object)
            {
                if (userProp.TryGetProperty("uniqueId", out var userIdProp))
                {
                    string id = userIdProp.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                        return id.Trim();
                }
            }
        }

        // Ищем root.user.uniqueId
        if (root.TryGetProperty("user", out var userElement) && userElement.ValueKind == JsonValueKind.Object)
        {
            if (userElement.TryGetProperty("uniqueId", out var userIdProp))
            {
                string id = userIdProp.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                    return id.Trim();
            }
        }

        // Фолбэк — используем безопасно ExtractNickname
        string nick = ExtractNickname(root);
        return string.IsNullOrEmpty(nick) ? null : nick;
    }

    // Извлекает флаги из структуры message (учитывает разные форматы)
    private void ExtractUserFlags(JsonElement root, out bool isSubscriber, out bool isModerator, out bool isFollower)
    {
        isSubscriber = false;
        isModerator = false;
        isFollower = false;

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return;

        // подписчик
        if (data.TryGetProperty("isSubscriber", out var sub) && sub.ValueKind == JsonValueKind.True)
            isSubscriber = true;

        // модератор
        if (data.TryGetProperty("isModerator", out var mod) && mod.ValueKind == JsonValueKind.True)
            isModerator = true;

        // follower
        if (data.TryGetProperty("userIdentity", out var ui) && ui.ValueKind == JsonValueKind.Object)
        {
            if (ui.TryGetProperty("isFollowerOfAnchor", out var follower) && follower.ValueKind == JsonValueKind.True)
                isFollower = true;
        }
    }

    private static void AddOrUpdateViewer(
    string key,
    string nickname,
    bool isSubscriber,
    bool isModerator,
    bool isFollowing,
    string sourceEvent,
    string textMessage = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (string.IsNullOrWhiteSpace(sourceEvent))
            return;

        // ❌ ChatMessage без текста — не пишем
        if (sourceEvent == "ChatMessage" && string.IsNullOrWhiteSpace(textMessage))
            return;

        string now = DateTime.Now.ToString("dd.MM.yy HH:mm:ss");

        if (ModDataStorage.ViewerDatabase.TryGetValue(key, out var existing))
        {
            existing.Nickname = nickname;
            existing.IsSubscriber = isSubscriber;
            existing.IsModerator = isModerator;
            existing.IsFollowing = isFollowing;
            existing.SourceEvent = sourceEvent;
            existing.Time = now;
            // 🔥 пишем текст ТОЛЬКО если он есть
            if (!string.IsNullOrWhiteSpace(textMessage))
                existing.TextMessage = textMessage;
        }
        else
        {
            ModDataStorage.ViewerDatabase[key] = new ViewerInfo
            {
                Key = key,
                Nickname = nickname,
                TextMessage = textMessage,
                IsSubscriber = isSubscriber,
                IsModerator = isModerator,
                IsFollowing = isFollowing,
                SourceEvent = sourceEvent,
                Time = now
            };
        }
        TrimViewerDatabase();
        TikFinityDatabase.UpdateViewerDatabaseJson();
    }

    private void HandleChatEvent(
    string key,
    string nickname,
    bool isSubscriber,
    bool isModerator,
    bool isFollowing,
    JsonElement data)
    {
        // 1️⃣ извлекаем текст
        string messageText = ExtractCommentText(data);

        // 2️⃣ ЛОГИРУЕМ ТОЛЬКО если текст не пустой
        if (!string.IsNullOrWhiteSpace(messageText))
        {
            AddOrUpdateViewer(
                key,
                nickname,
                isSubscriber,
                isModerator,
                isFollowing,
                "ChatMessage",
                messageText
            );
        }

        // 3️⃣ остальная логика не меняется
        if (isFollowing && !SubscriberIds.Contains(key))
        {
            var entry = new SubscriberDatabaseEntry
            {
                Key = key,
                Nickname = nickname,
                Timestamp = DateTime.UtcNow,
                EventType = "follow",
                Time = DateTime.Now.ToString("dd.MM.yy HH:mm:ss")
            };

            TikFinityDatabase.UpdateSubscriberDatabaseJson(entry);
            TikFinityDatabase.RebuildSubscriberCache();
        }
        if (isModerator)
        {
            AddOrUpdateModerator(key, nickname, "ChatMessage", ExtractCommentText(data));
        }

        ProcessChatMessage(data, nickname);
    }

    private static void HandleSubscribeEvent(string key, string nickname, bool isModerator, bool isFollowing)
    {
        AddOrUpdateViewer(key, nickname, true, isModerator, isFollowing, "Subscribe");
        SpawnController.SpawnSubscriberSlime(nickname);
        // --- записываем в историю ---
        var entry = new SubscriberDatabaseEntry
        {
            Key = key,
            Nickname = nickname,
            Timestamp = DateTime.UtcNow,
            EventType = "subscribe",
            Time = DateTime.Now.ToString("dd.MM.yy HH:mm:ss")
        };
        TikFinityDatabase.UpdateSubscriberDatabaseJson(entry);
        TikFinityDatabase.RebuildSubscriberCache();
    }

    private static void HandleJoinEvent(string key, string nickname)
    {
        if (string.IsNullOrEmpty(nickname))
            return;
        // 🦋 бабочка всегда
        SpawnController.SpawnViewerButterfly(nickname, key);

        // 🟡 если это подписчик — ветеран
        if (SubscriberIds.Contains(key) && !veteranSpawnedThisSession.Contains(key))
        {
            SpawnController.SpawnVeteranSlime(nickname);
            veteranSpawnedThisSession.Add(key);
        }
        // 🔥 если пользователь — модератор — спавним огненного слизня
        if (ModDataStorage.ModeratorDatabase.ContainsKey(key))
        {
            SpawnController.SpawnModeratorSlime(nickname);
        }
        // 🔥 если пользователь есть в базе дарителей — спавним золотого слизня
        if (GiftGiverIds.Contains(key))
        {
            // SpawnGifterDragonfly(nickname, key);
            SpawnController.SpawnGifterSlime(nickname);
        }
    }

    // -------------------------
    // Обработка чата (оставляем как было, можно расширить)
    // -------------------------
    private void ProcessChatMessage(JsonElement root, string nickname)
    {
        // 1. Извлекаем текст комментария
        string commentText = ExtractCommentText(root);

        if (string.IsNullOrEmpty(commentText))
            return;

        // 2. Спавним бабочку с комментарием
        SpawnController.SpawnCommentFirefly(nickname, commentText);
    }

    private string ExtractCommentText(JsonElement root)
    {
        string text = "";

        if (root.TryGetProperty("text", out var textProp) && !string.IsNullOrWhiteSpace(textProp.GetString()))
        {
            text = textProp.GetString().Trim();
        }
        else if (root.TryGetProperty("comment", out var commentProp) && !string.IsNullOrWhiteSpace(commentProp.GetString()))
        {
            text = commentProp.GetString().Trim();
        }
        else if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object)
        {
            if (dataProp.TryGetProperty("text", out var dataTextProp) && !string.IsNullOrWhiteSpace(dataTextProp.GetString()))
                text = dataTextProp.GetString().Trim();
            else if (dataProp.TryGetProperty("comment", out var dataCommentProp) && !string.IsNullOrWhiteSpace(dataCommentProp.GetString()))
                text = dataCommentProp.GetString().Trim();
            else if (dataProp.TryGetProperty("content", out var contentProp) && !string.IsNullOrWhiteSpace(contentProp.GetString()))
                text = contentProp.GetString().Trim();
        }
        //if (text.Length > 50)
        //text = text.Substring(0, 47) + "...";
        return text;
    }

    private void ProcessLikeEvent(JsonElement root, string nickname)
    {
        int likeIncrement = 1;
        if (root.TryGetProperty("count", out var countProp) &&
            countProp.ValueKind == JsonValueKind.Number)
        {
            likeIncrement = countProp.GetInt32();
        }
        string viewerKey = ExtractViewerKey(root);
        SpawnController.SpawnLikeDragonfly(viewerKey, nickname, likeIncrement);
    }

    #region TEST METHODS

    public static void TestGift(int count = 1, int diamonds = 1)
    {
        var fakeGift = new TikGiftEvent
        {
            UserName = TikTestFactory.RandomName(),
            RepeatCount = count,
            DiamondCount = diamonds
        };
        for (int i = 0; i < count; i++)
        {
            GiftEnemySpawner.SpawnGiftEnemy(fakeGift.UserName, fakeGift.RepeatCount, fakeGift.DiamondCount);
        }
        Main.NewText($"[TEST] Gift x{count} от {fakeGift.UserName}", Color.Gold);
    }

    public static void TestShare()
    {
        var fakeShare = new TikShareEvent
        {
            UserId = TikTestFactory.RandomId(),
            UserName = TikTestFactory.RandomName(),
            isModerator = false,
            isFollowing = false
        };
        HandleShareEvent(fakeShare.UserId, fakeShare.UserName, fakeShare.isModerator, fakeShare.isFollowing);
        Main.NewText($"[TEST] Share от {fakeShare.UserName}", Color.LightBlue);
    }

    public static void TestSubscribe()
    {
        var fakeSubscribe = new TikSubscribeEvent
        {
            UserId = TikTestFactory.RandomId(),
            UserName = TikTestFactory.RandomName(),
            isModerator = false,
            isFollowing = false
        };
        HandleSubscribeEvent(fakeSubscribe.UserId, fakeSubscribe.UserName, fakeSubscribe.isModerator, fakeSubscribe.isFollowing);
        Main.NewText($"[TEST] Subscribe: {fakeSubscribe.UserName}", Color.Gold);
    }

    

    public static void TestSubscriberJoin()
    {
        var fakeJoinSubscriber = new TikJoinEvent
        {
            UserId = TikTestFactory.RandomId(),
            UserName = "SUB_" + TikTestFactory.RandomName()
        };

        SpawnController.SpawnVeteranSlime(fakeJoinSubscriber.UserName);
        Main.NewText($"[TEST] Subscriber joined: {fakeJoinSubscriber.UserName}", Color.Gold);
    }

    public static void TestModeratorJoin()
    {
        var fakeJoinModerator = new TikJoinEvent
        {
            UserId = TikTestFactory.RandomId(),
            UserName = "MOD_" + TikTestFactory.RandomName()
        };

        SpawnController.SpawnModeratorSlime(fakeJoinModerator.UserName);
        Main.NewText($"[TEST] Moderator joined: {fakeJoinModerator.UserName}", Color.Red);
    }

    public static void TestGifterJoin()
    {
        var fakeJoinGifter = new TikJoinEvent
        {
            UserId = TikTestFactory.RandomId(),
            UserName = "Gift_" + TikTestFactory.RandomName()
        };

        SpawnController.SpawnGifterSlime(fakeJoinGifter.UserName);
        Main.NewText($"[TEST] Gifter joined: {fakeJoinGifter.UserName}", Color.Red);
    }

    public static void TestLike(int count = 10)
    {
        string viewerKey = "TEST_VIEWER";
        string name = "TEST_USER";

        SpawnController.SpawnLikeDragonfly(viewerKey, name, count);

        Main.NewText($"[TEST] Likes x{count} от {name}", Color.HotPink);
    }

    #endregion

}


public class SafeWebSocketClient
{
    private ClientWebSocket _ws;
    private readonly Uri _uri;
    private readonly int _reconnectDelay; // задержка между попытками переподключения в мс
    private bool _isRunning;

    public Action<string> OnMessageReceived;

    public SafeWebSocketClient(string url, int reconnectDelay = 5000)
    {
        _uri = new Uri(url);
        _reconnectDelay = reconnectDelay;
        _ws = new ClientWebSocket();
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        while (_isRunning)
        {
            try
            {
                if (_ws.State != WebSocketState.Open)
                {
                    _ws.Dispose();
                    _ws = new ClientWebSocket();
                    Main.NewText("[WebSocket] Попытка подключения...", 200, 200, 255);
                    await _ws.ConnectAsync(_uri, CancellationToken.None);
                    Main.NewText("[WebSocket] Успешно подключено!", 0, 255, 0);
                    _ = ListenLoopAsync(); // запускаем прослушивание в фоне
                }
            }
            catch (WebSocketException wsEx)
            {
                Main.NewText($"[WebSocket WARNING] Не удалось подключиться: {wsEx.Message}", 255, 200, 0);
            }
            catch (Exception ex)
            {
                Main.NewText($"[WebSocket ERROR] {ex.Message}", 255, 0, 0);
            }

            await Task.Delay(_reconnectDelay);
        }
    }

    private async Task ListenLoopAsync()
    {
        var buffer = new byte[1024];
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    Main.NewText("[WebSocket] Сервер закрыл соединение", 255, 200, 0);
                    break;
                }

                string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                // Main.NewText($"[WebSocket] Получено: {message}", 200, 255, 200);

                OnMessageReceived?.Invoke(message);
            }
        }
        catch (WebSocketException wsEx)
        {
            Main.NewText($"[WebSocket WARNING] Ошибка при прослушивании: {wsEx.Message}", 255, 200, 0);
        }
        catch (Exception ex)
        {
            Main.NewText($"[WebSocket ERROR] {ex.Message}", 255, 0, 0);
        }
    }

    public async Task StopAsync()
    {
        _isRunning = false;
        if (_ws != null && (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.Connecting))
        {
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopped", CancellationToken.None);
        }
    }

}