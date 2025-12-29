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
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

public class TikFinityClient : ModSystem
{
    private static List<SubscriberDatabaseEntry> subscriberDatabase = new List<SubscriberDatabaseEntry>();
    private static ClientWebSocket socket;
    private static CancellationTokenSource cancelToken;
    private static HashSet<string> veteranSpawnedThisSession = new HashSet<string>();
    public static HashSet<string> SubscriberIds = new HashSet<string>();
    private const int MAX_VIEWERS = 500;
    private static List<GiftDatabaseEntry> giftDatabase = new();
    public static HashSet<string> GiftGiverIds = new();
    private static bool _hasLoggedFirstMessage = false;
    private static SafeWebSocketClient safeSocket;
    private static bool _hasShownStreamerName = false;
    private static Dictionary<string, int> likeComboCounter = new Dictionary<string, int>();
    private const string ModDataFolderName = "ImapoTikTokIntegrationModBD";
    private static string GetModDataRoot()
    {
        return Path.Combine(Main.SavePath, ModDataFolderName);
    }
    private static string CurrentStreamerKey = "default";
    private static string GetStreamerPath()
    {
        string safeName = MakeSafeFolderName(CurrentStreamerKey);
        return Path.Combine(GetModDataRoot(), safeName);
    }
    private static string MakeSafeFolderName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return name.Trim();
    }
    private static string ViewerDatabaseFilePath =>
    Path.Combine(GetStreamerPath(), "ViewerDatabase.json");
    private static string SubscriberDatabaseFilePath =>
        Path.Combine(GetStreamerPath(), "SubscriberDatabase.json");
    private static string GiftDatabaseFilePath =>
        Path.Combine(GetStreamerPath(), "GiftDatabase.json");
    private static string ModeratorDatabaseFilePath =>
        Path.Combine(GetStreamerPath(), "ModeratorDatabase.json");
    // === ОГРАНИЧЕНИЯ НА СПАВН ===
    private static int CountActiveButterflies()
    {
        int count = 0;
        foreach (var npc in Main.npc)
        {
            if (npc.active && npc.type == NPCID.Butterfly &&
                npc.TryGetGlobalNPC(out ViewerButterflyGlobal g) &&
                g.isViewerButterfly)
                count++;
        }
        return count;
    }

    private static int CountActiveDragonflies()
    {
        int count = 0;
        foreach (var npc in Main.npc)
        {
            if (npc.active && npc.type == NPCID.GreenDragonfly &&
                npc.TryGetGlobalNPC(out LikeFloatingTextGlobal g) &&
                !string.IsNullOrEmpty(g.viewerKey))
                count++;
        }
        return count;
    }

    private static int CountActiveFireflies()
    {
        int count = 0;
        foreach (var npc in Main.npc)
        {
            if (npc.active && npc.type == NPCID.Firefly &&
                npc.TryGetGlobalNPC(out ViewerFireflyGlobal g) &&
                g.isViewer)
                count++;
        }
        return count;
    }

    private static int CountActiveSlimes()
    {
        int count = 0;
        foreach (var npc in Main.npc)
        {
            if (npc.active && (
                npc.type == NPCID.BlueSlime ||
                npc.type == NPCID.RedSlime ||
                npc.type == NPCID.LavaSlime ||
                npc.type == NPCID.GoldenSlime
            ) && npc.TryGetGlobalNPC(out ViewerSlimesGlobal g) && g.isViewer)
                count++;
        }
        return count;
    }
    private static void EnsureStreamerDirectory()
    {
        Directory.CreateDirectory(GetStreamerPath());
    }

    // -------------------------
    // Жизненный цикл ModSystem
    // -------------------------
    public override void OnWorldLoad()
    {
        ImportGiftDatabase();
        ImportViewerDatabase();
        ImportSubscriberDatabase();
        ImportModeratorDatabase();
        StartClient();
    }

    public override void OnWorldUnload()
    {
        StopClientAsync().ConfigureAwait(false);
        UpdateViewerDatabaseJson();
        veteranSpawnedThisSession.Clear();
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
        try
        {
            HandleMessage(json);
        }
        catch (Exception ex)
        {
            Main.NewText($"[TikFinity ERROR] Failed to handle message: {ex}", 255, 0, 0);
        }
    }

    private async Task ListenLoop()
    {
        var buffer = new byte[4096];
        var messageBuilder = new StringBuilder();
        while (socket != null && socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                do
                {
                    result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        cancelToken.Token
                    );
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closed",
                            CancellationToken.None
                        );
                        return;
                    }
                    messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);
            }
            catch
            {
                return;
            }

            string fullMessage = messageBuilder.ToString();
            messageBuilder.Clear();

            // 🔍 ЛОГИРУЕМ ПЕРВОЕ СООБЩЕНИЕ
            if (!_hasLoggedFirstMessage)
            {
                _hasLoggedFirstMessage = true;
                try
                {
                    var mod = ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>();
                    mod.Logger.Info($"[TikFinity] First message from server: {fullMessage}");
                }
                catch (Exception ex)
                {
                    // На всякий случай
                    Console.WriteLine($"[TikFinity] Failed to log first message: {ex}");
                }
            }

            HandleMessage(fullMessage);
        }
    }

    // -------------------------
    // Основной обработчик сообщений
    // -------------------------
    private static void SetStreamer(string newStreamer)
    {
        newStreamer = MakeSafeFolderName(newStreamer);
        if (newStreamer == CurrentStreamerKey)
            return;
        CurrentStreamerKey = newStreamer;
        Directory.CreateDirectory(GetStreamerPath());
        Main.NewText($"[TikFinity] Connected to stream: {newStreamer}", Color.Cyan);
        _hasShownStreamerName = true;

        // Очистка кэшей текущей сессии
        ModDataStorage.ViewerDatabase.Clear();
        ModDataStorage.ModeratorDatabase.Clear();
        ModDataStorage.SubscriberDatabase.Clear();
        ModDataStorage.giftDatabase.Clear();
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string platform = root.GetProperty("platform").GetString();
            string eventType = root.TryGetProperty("event", out var ev) ? ev.GetString() : "";
            JsonElement data = root.TryGetProperty("data", out var d) ? d : root;

            if (eventType == "roomUser" && !_hasShownStreamerName)
            {
                // Попробуем извлечь tikfinityUsername из корня сообщения
                if (root.TryGetProperty("tikfinityUsername", out var streamerNameElem) &&
                    !string.IsNullOrEmpty(streamerNameElem.GetString()))
                {
                    string streamerName = streamerNameElem.GetString().Trim();
                    SetStreamer(streamerName);
                }
                else if (root.TryGetProperty("data", out var dataNested) && data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("tikfinityUsername", out var nameFromData) &&
                        !string.IsNullOrEmpty(nameFromData.GetString()))
                    {
                        string streamerName = nameFromData.GetString().Trim();
                        SetStreamer(streamerName);
                    }
                }
            }

            string key = ExtractViewerKey(data);
            string nickname = ExtractNickname(data);

            ExtractUserFlags(root, out bool isSubscriber, out bool isModerator, out bool isFollowing);
            ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
            .Logger.Info($"[Tikfinity RAW] {json}");

            if (string.IsNullOrWhiteSpace(key))
                return;

            switch (eventType)
            {
                case "join":
                case "roomUser":
                case "member":
                case "":
                    HandleJoinEvent(key, nickname);
                    AddOrUpdateViewer(key, nickname, isSubscriber, isModerator, isFollowing, eventType ?? "Unknown");
                    /*
                        if (platform == "youtube")
                        {
                            Main.QueueMainThreadAction(() =>
                            {
                                string messageText = ExtractCommentText(data);
                                if (!string.IsNullOrWhiteSpace(messageText))
                                {
                                    // Вывод в чат
                                    string chatMessage = $"[YouTube] {nickname}: {messageText}";
                                    if (Main.netMode == NetmodeID.SinglePlayer)
                                        Main.NewText(chatMessage, 255, 255, 0);
                                    else if (Main.netMode == NetmodeID.Server)
                                        Terraria.Chat.ChatHelper.BroadcastChatMessage(
                                            Terraria.Localization.NetworkText.FromLiteral(chatMessage),
                                            new Color(255, 255, 0)
                                        );

                                    // Спавн Firefly (аналог TikTok)
                                    SpawnCommentFirefly(nickname, messageText);
                                }
                            });
                        }

                    */
                    break;
                case "chat":
                case "connect":
                HandleChatEvent(key, nickname, isSubscriber, isModerator, isFollowing, data);
                    break;

                case "like":
                    ProcessLikeEvent(data, nickname);
                    break;

                case "gift":
                    int amount = data.TryGetProperty("coins", out var c) ? c.GetInt32() : 1;
                    AddGiftDatabase(key, nickname, amount);
                    GiftEnemySpawner.SpawnGiftEnemy(nickname, amount);
                    break;

                case "follow":
                    HandleSubscribeEvent(key, nickname, isModerator, isFollowing);
                    break;

                case "share":
                case "subscribe":
                    HandleShareEvent(key, nickname, isModerator, isFollowing);
                    break;

                default:
                    // Если неизвестное событие, всё равно обновляем базу, но SourceEvent = eventType или "Unknown"
                    HandleJoinEvent(key, nickname);
                    AddOrUpdateViewer(key, nickname, isSubscriber, isModerator, isFollowing, eventType ?? "Unknown");
                    break;
            }
        }
        catch (JsonException)
        {
            // Тихий fail
        }
        catch (Exception)
        {
            // Тихий fail
        }
    }

    private static void HandleShareEvent(string key, string nickname, bool isModerator, bool isFollowing)
    {
        // Логируем (опционально)
        AddOrUpdateViewer(key, nickname, isSubscriber: false, isModerator, isFollowing, "Share");

        if (Main.netMode == NetmodeID.MultiplayerClient) return;

        // Применяем случайный бафф
        ApplyRandomBuffFromShare();

        SpawnShareSlime(nickname);
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

        UpdateModeratorDatabaseJson();
    }

    public static void ImportModeratorDatabase()
    {
        try
        {
            if (!File.Exists(ModeratorDatabaseFilePath))
                return;

            string json = File.ReadAllText(ModeratorDatabaseFilePath);
            var list = JsonSerializer.Deserialize<List<ModeratorInfo>>(json);

            if (list != null)
            {
                ModDataStorage.ModeratorDatabase.Clear();
                foreach (var m in list)
                {
                    if (!string.IsNullOrEmpty(m.Key))
                        ModDataStorage.ModeratorDatabase[m.Key] = m;
                }

                ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                    .Logger.Info($"[Tikfinity] Moderator database imported from {ModeratorDatabaseFilePath}");
            }
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                .Logger.Info($"[Tikfinity ERROR] Failed to import moderator database: {ex}");
        }
    }


    public static void UpdateModeratorDatabaseJson()
    {
        try
        {
            EnsureStreamerDirectory();
            var list = ModDataStorage.ModeratorDatabase.Values.ToList();
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(
                ModeratorDatabaseFilePath,
                JsonSerializer.Serialize(list, options)
            );
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                .Logger.Info($"[Tikfinity ERROR] Failed to update moderator JSON: {ex}");
        }
    }

    public static void ImportGiftDatabase()
    {
        try
        {
            if (!File.Exists(GiftDatabaseFilePath))
                return;

            string json = File.ReadAllText(GiftDatabaseFilePath);
            var list = JsonSerializer.Deserialize<List<GiftDatabaseEntry>>(json);

            if (list != null)
            {
                ModDataStorage.giftDatabase.Clear();
                foreach (var g in list)
                {
                    if (!string.IsNullOrEmpty(g.Key))
                        ModDataStorage.giftDatabase[g.Key] = g;
                }
                RebuildGiftGiverCache();

                ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                    .Logger.Info($"[Tikfinity] Gift database imported from {GiftDatabaseFilePath}");
            }
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                .Logger.Info($"[Tikfinity ERROR] Failed to import gift database: {ex}");
        }
    }

    public static void UpdateGiftDatabaseJson()
    {
        try
        {
            EnsureStreamerDirectory();
            var list = ModDataStorage.giftDatabase.Values.ToList();
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(
                GiftDatabaseFilePath,
                JsonSerializer.Serialize(list, options)
            );
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                .Logger.Info($"[Tikfinity ERROR] Failed to update gift JSON: {ex}");
        }
    }

    private static void AddOrUpdateGift(string key, string nickname, int coins)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        string now = DateTime.Now.ToString("dd.MM.yy HH:mm:ss");

        if (ModDataStorage.giftDatabase.TryGetValue(key, out var existing))
        {
            existing.Nickname = nickname;
            existing.Coins += coins; // аккумулируем монеты
            existing.Time = now;
        }
        else
        {
            ModDataStorage.giftDatabase[key] = new GiftDatabaseEntry
            {
                Key = key,
                Nickname = nickname,
                Coins = coins,
                Time = now
            };
        }

        UpdateGiftDatabaseJson();
        RebuildGiftGiverCache();
    }

    private static void AddGiftDatabase(string key, string nickname, int coins)
    {
        giftDatabase.Add(new GiftDatabaseEntry
        {
            Key = key,
            Nickname = nickname,
            Coins = coins,
            Time = DateTime.Now.ToString("dd.MM.yy HH:mm:ss")
        });

        File.WriteAllText(
            GiftDatabaseFilePath,
            JsonSerializer.Serialize(giftDatabase, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            })
        );

        RebuildGiftGiverCache();
    }

    private static void RebuildGiftGiverCache()
    {
        GiftGiverIds.Clear();
        foreach (var g in giftDatabase)
        {
            if (!string.IsNullOrEmpty(g.Key))
                GiftGiverIds.Add(g.Key);
        }
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

    private static void RebuildSubscriberCache()
    {
        SubscriberIds.Clear();
        foreach (var s in subscriberDatabase)
        {
            if (!string.IsNullOrEmpty(s.Key))
                SubscriberIds.Add(s.Key);
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

    public static void UpdateSubscriberDatabaseJson(SubscriberDatabaseEntry entry)
    {
        try
        {
            EnsureStreamerDirectory();
            subscriberDatabase.Add(entry);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(
                SubscriberDatabaseFilePath,
                JsonSerializer.Serialize(subscriberDatabase, options)
            );
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                .Logger.Info($"[Tikfinity ERROR] Failed to update subscriber Database JSON: {ex}");
        }
    }

    public static void ImportSubscriberDatabase()
    {
        try
        {
            if (!File.Exists(SubscriberDatabaseFilePath)) return;
            string json = File.ReadAllText(SubscriberDatabaseFilePath);
            var list = JsonSerializer.Deserialize<List<SubscriberDatabaseEntry>>(json);
            if (list != null)
            {
                subscriberDatabase = list;
                RebuildSubscriberCache();
            }
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                .Logger.Info($"[Tikfinity ERROR] Failed to import subscriber Database: {ex}");
        }
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
    // Сохранение / загрузка базы
    // -------------------------

    public static void ExportViewerDatabase()
    {
        try
        {
            var list = ModDataStorage.ViewerDatabase.Values.ToList();

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            string json = JsonSerializer.Serialize(list, options);

            File.WriteAllText(ViewerDatabaseFilePath, json);
            ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>().Logger.Info($"[Tikfinity] Viewer database exported to {ViewerDatabaseFilePath}");
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>().Logger.Info($"[Tikfinity ERROR] Failed to export viewer database: {ex}");
        }
    }

    // -------------------------
    // Импорт viewerDatabase из JSON
    // -------------------------
    public static void ImportViewerDatabase()
    {
        try
        {
            if (!File.Exists(ViewerDatabaseFilePath))
                return;

            string json = File.ReadAllText(ViewerDatabaseFilePath);
            var list = JsonSerializer.Deserialize<List<ViewerInfo>>(json);

            if (list != null)
            {
                ModDataStorage.ViewerDatabase.Clear();
                foreach (var v in list)
                {
                    if (!string.IsNullOrEmpty(v.Key))
                        ModDataStorage.ViewerDatabase[v.Key] = v;
                }

                ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>().Logger.Info($"[Tikfinity] Viewer database imported from {ViewerDatabaseFilePath}");
            }
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>().Logger.Info($"[Tikfinity ERROR] Failed to import viewer database: {ex}");
        }
    }

    public static void UpdateViewerDatabaseJson()
    {
        try
        {
            EnsureStreamerDirectory();
            var list = ModDataStorage.ViewerDatabase.Values.ToList();
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(
                ViewerDatabaseFilePath,
                JsonSerializer.Serialize(list, options)
            );
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                .Logger.Info($"[Tikfinity ERROR] Failed to update viewer JSON: {ex}");
        }
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

        if (!string.IsNullOrEmpty(nickname) && nickname.Length > 20)
            nickname = nickname.Substring(0, 27) + "...";

        return nickname;
    }

    // Возвращает стабильный ключ: сначала uniqueId (если есть), иначе nickname
    private string ExtractViewerKey(JsonElement root)
    {
        // Ищем uniqueId в корне, в data, в user
        if (root.TryGetProperty("uniqueId", out var idProp) && !string.IsNullOrWhiteSpace(idProp.GetString()))
            return idProp.GetString().Trim();

        if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object)
        {
            if (dataProp.TryGetProperty("uniqueId", out var dataIdProp) && !string.IsNullOrWhiteSpace(dataIdProp.GetString()))
                return dataIdProp.GetString().Trim();

            if (dataProp.TryGetProperty("user", out var userProp) && userProp.ValueKind == JsonValueKind.Object)
            {
                if (userProp.TryGetProperty("uniqueId", out var userIdProp) && !string.IsNullOrWhiteSpace(userIdProp.GetString()))
                    return userIdProp.GetString().Trim();
            }
        }

        if (root.TryGetProperty("user", out var userElement) && userElement.ValueKind == JsonValueKind.Object)
        {
            if (userElement.TryGetProperty("uniqueId", out var userIdProp) && !string.IsNullOrWhiteSpace(userIdProp.GetString()))
                return userIdProp.GetString().Trim();
        }

        // Фолбэк — используем nickname (не идеально, но лучше чем ничего)
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
        UpdateViewerDatabaseJson();
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

            UpdateSubscriberDatabaseJson(entry);
            RebuildSubscriberCache();
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
        SpawnSubscriberSlime(nickname);
        // --- записываем в историю ---
        var entry = new SubscriberDatabaseEntry
        {
            Key = key,
            Nickname = nickname,
            Timestamp = DateTime.UtcNow,
            EventType = "subscribe",
            Time = DateTime.Now.ToString("dd.MM.yy HH:mm:ss")
        };
        UpdateSubscriberDatabaseJson(entry);
        RebuildSubscriberCache();
    }

    private static void HandleJoinEvent(string key, string nickname)
    {
        if (string.IsNullOrEmpty(nickname))
            return;

        // 🦋 бабочка всегда
        SpawnViewerButterfly(nickname, key);

        // 🟡 если это подписчик — ветеран
        if (SubscriberIds.Contains(key) && !veteranSpawnedThisSession.Contains(key))
        {
            SpawnVeteranSlime(nickname);
            veteranSpawnedThisSession.Add(key);
        }

        // 🔥 если пользователь — модератор — спавним огненного слизня
        if (ModDataStorage.ModeratorDatabase.ContainsKey(key))
        {
            SpawnModeratorSlime(nickname);
        }

        // 🔥 если пользователь есть в базе дарителей — спавним золотого слизня
        if (GiftGiverIds.Contains(key))
        {
            // SpawnGifterDragonfly(nickname, key);
            SpawnGifterSlime(nickname);
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
        SpawnCommentFirefly(nickname, commentText);
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

    // -------------------------
    // Остальные вспомогательные методы / спавн (твои существующие)
    // -------------------------
    private static string ReplaceEmojis(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        return input
            .Replace("😀", ":)")
            .Replace("😂", "xD")
            .Replace("❤️", "<3")
            .Replace("🔥", "FIRE")
            .Replace("👍", "+");
    }

    private void ProcessLikeEvent(JsonElement root, string nickname)
    {
        int likeIncrement = 1;
        if (root.TryGetProperty("count", out var countProp) && countProp.ValueKind == JsonValueKind.Number)
            likeIncrement = countProp.GetInt32();

        string viewerKey = ExtractViewerKey(root);
        if (string.IsNullOrEmpty(viewerKey))
            return;

        string cleanName = NickSanitizer.Sanitize(nickname);

        // 🔥 Обновляем глобальный счётчик комбо за сессию
        if (likeComboCounter.ContainsKey(viewerKey))
            likeComboCounter[viewerKey] += likeIncrement;
        else
            likeComboCounter[viewerKey] = likeIncrement;

        int totalLikes = likeComboCounter[viewerKey];

        Main.QueueMainThreadAction(() =>
        {
            var player = Main.LocalPlayer;

            // Ищем существующую стрекозу этого зрителя
            NPC existing = Main.npc.FirstOrDefault(n =>
                n.active &&
                n.type == NPCID.GreenDragonfly &&
                n.TryGetGlobalNPC(out LikeFloatingTextGlobal g) &&
                g.viewerKey == viewerKey
            );

            if (existing != null)
            {
                var g = existing.GetGlobalNPC<LikeFloatingTextGlobal>();
                g.likeCount = totalLikes; // обновляем до накопленного значения
                g.TriggerCombo(player.Center + new Vector2(0, -50));
                existing.netUpdate = true;
                return;
            }

            if (CountActiveDragonflies() >= 10) return;

            // Создаём новую стрекозу
            int npcID = NPC.NewNPC(
                player.GetSource_FromThis(),
                (int)player.Center.X + Main.rand.Next(-30, 30),
                (int)player.Center.Y - 50,
                NPCID.GreenDragonfly
            );

            if (npcID >= 0)
            {
                NPC npc = Main.npc[npcID];
                npc.friendly = true;
                npc.dontTakeDamage = true;
                npc.noGravity = true;
                npc.noTileCollide = true;
                npc.life = 1;
                npc.lifeMax = 1;
                npc.timeLeft = LikeFloatingTextGlobal.MaxLife; // ← важно!

                var g = npc.GetGlobalNPC<LikeFloatingTextGlobal>();
                g.viewerKey = viewerKey;
                g.viewerName = cleanName;
                g.likeCount = totalLikes; // ← используем накопленное значение
                g.life = 0;
                g.TriggerCombo(player.Center + new Vector2(0, -50));
                npc.netUpdate = true;
            }
        });
    }

    // --- SpawnViewerButterfly / SpawnCommentFirefly / SpawnSubscriberSlime / SpawnVeteranSlime ---
    // Использую твою существующую реализацию (обёрнутые вызовы) — просто вызываю их как есть.

    private static void SpawnViewerButterfly(string nickname, string viewerId)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) return;
        if (CountActiveButterflies() >= 20) return; // ⚠️ лимит

        string cleanName = NickSanitizer.Sanitize(nickname).Trim();
        if (string.IsNullOrWhiteSpace(cleanName)) cleanName = viewerId;

        // Не дублируем
        if (Main.npc.Any(n =>
            n.active && n.type == NPCID.Butterfly &&
            n.TryGetGlobalNPC(out ViewerButterflyGlobal g) &&
            g.isViewerButterfly && g.rawId == viewerId))
            return;

        Main.QueueMainThreadAction(() =>
        {
            var player = Main.LocalPlayer;
            int npcID = NPC.NewNPC(
                player.GetSource_FromThis(),
                (int)player.position.X + Main.rand.Next(-200, 200),
                (int)player.position.Y - 100,
                NPCID.Butterfly
            );
            if (npcID >= 0)
            {
                NPC npc = Main.npc[npcID];
                var g = npc.GetGlobalNPC<ViewerButterflyGlobal>();
                g.isViewerButterfly = true;
                g.viewerName = cleanName;
                g.rawId = viewerId;
                g.lifetime = 0;
            }
        });
    }

    private static void SpawnCommentFirefly(string nickname, string comment)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) return;
        if (CountActiveFireflies() >= 15) return; // ⚠️ лимит
                                                    // Не спавним, если уже есть активная Firefly от этого зрителя
        if (Main.npc.Any(n => n.active && n.type == NPCID.Firefly &&
            n.TryGetGlobalNPC(out ViewerFireflyGlobal g) && g.viewerName == NickSanitizer.Sanitize(nickname)))
            return;

        Main.QueueMainThreadAction(() =>
        {
            var player = Main.LocalPlayer;

            int npcID = NPC.NewNPC(
                player.GetSource_FromThis(),
                (int)player.position.X + Main.rand.Next(-300, 300),
                (int)player.position.Y - 100,
                NPCID.Firefly
            );

            if (npcID >= 0)
            {
                NPC npc = Main.npc[npcID];
                var global = npc.GetGlobalNPC<ViewerFireflyGlobal>();
                comment = ReplaceEmojis(comment);

                global.viewerName = NickSanitizer.Sanitize(nickname);
                global.commentText = comment;
                global.isComment = true;
                global.isViewer = true;

                npc.timeLeft = 300;
            }

            string chatMessage = $"[Чат] {nickname}: {comment}";
            if (Main.netMode == NetmodeID.SinglePlayer)
                Main.NewText(chatMessage, 255, 255, 255);
            else if (Main.netMode == NetmodeID.Server)
                Terraria.Chat.ChatHelper.BroadcastChatMessage(
                    Terraria.Localization.NetworkText.FromLiteral(chatMessage),
                    new Color(180, 255, 180)
                );
        });
    }

    private static void SpawnSubscriberSlime(string nickname)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) return;
        if (CountActiveSlimes() >= 10) return; // ⚠️ общий лимит на всех слизней

        Main.QueueMainThreadAction(() =>
        {
            var player = Main.LocalPlayer;

            int npcID = NPC.NewNPC(
                player.GetSource_FromThis(),
                (int)player.position.X + Main.rand.Next(-200, 200),
                (int)player.position.Y,
                NPCID.BlueSlime
            );

            if (npcID >= 0)
            {
                NPC npc = Main.npc[npcID];

                npc.friendly = true;
                npc.damage = 20;
                npc.lifeMax = 250;
                npc.life = 250;
                npc.defense = 15;
                npc.knockBackResist = 0.5f;
                npc.chaseable = true;
                npc.target = player.whoAmI;
                npc.timeLeft = int.MaxValue; // чтобы Terraria не убила NPC раньше
                var global = npc.GetGlobalNPC<ViewerSlimesGlobal>();
                global.viewerName = NickSanitizer.Sanitize(nickname);
                global.isViewer = true;
                var lifetime = npc.GetGlobalNPC<VisualLifetimeGlobalNPC>();
                lifetime.SetLifetime(60); // 60 сек
                npc.netUpdate = true;

            }

            Main.NewText($"[Новый подписчик] {nickname}!", 255, 10, 100);
        });
    }

    private static void SpawnVeteranSlime(string nickname)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) return;
        if (CountActiveSlimes() >= 10) return; // ⚠️ общий лимит на всех слизней

        Main.QueueMainThreadAction(() =>
        {
            var player = Main.LocalPlayer;

            int npcID = NPC.NewNPC(
                player.GetSource_FromThis(),
                (int)player.position.X + Main.rand.Next(-200, 200),
                (int)player.position.Y,
                NPCID.RedSlime
            );

            if (npcID >= 0)
            {
                NPC npc = Main.npc[npcID];
                npc.friendly = true;
                npc.damage = 20;
                npc.lifeMax = 500;
                npc.life = 500;
                npc.defense = 40;
                npc.knockBackResist = 0.3f;
                //npc.chaseable = true;
                //npc.target = player.whoAmI;
                npc.timeLeft = int.MaxValue; // чтобы Terraria не убила NPC раньше
                var global = npc.GetGlobalNPC<ViewerSlimesGlobal>();
                global.viewerName = NickSanitizer.Sanitize(nickname);
                global.isViewer = true;
                global.isVeteran = true;
                var lifetime = npc.GetGlobalNPC<VisualLifetimeGlobalNPC>();
                lifetime.SetLifetime(150);
                npc.netUpdate = true;
            }

            Main.NewText($"[Подписчик] {nickname} прибыл!", 255, 215, 0);
        });
    }

    private static void SpawnModeratorSlime(string nickname)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) return;
        Main.QueueMainThreadAction(() =>
        {
            var player = Main.LocalPlayer;
            int npcID = NPC.NewNPC(
                player.GetSource_FromThis(),
                (int)player.position.X + Main.rand.Next(-200, 200),
                (int)player.position.Y,
                NPCID.LavaSlime
            );
            if (npcID >= 0)
            {
                NPC npc = Main.npc[npcID];
                npc.friendly = true;
                npc.damage = 25;
                npc.lifeMax = 400;
                npc.life = 400;
                npc.defense = 40;
                npc.knockBackResist = 0.5f;
                //npc.chaseable = true;
                //npc.target = player.whoAmI;
                npc.timeLeft = int.MaxValue; // ← важно: отключаем стандартный таймер Terraria
                var global = npc.GetGlobalNPC<ViewerSlimesGlobal>();
                global.viewerName = NickSanitizer.Sanitize(nickname);
                global.isViewer = true;
                global.isModerator = true;
                var lifetime = npc.GetGlobalNPC<VisualLifetimeGlobalNPC>();
                lifetime.SetLifetime(300);
                npc.netUpdate = true;
            }
            Main.NewText($"[Модератор] {nickname} прибыл!", 255, 80, 20);
        });
    }

    private static void SpawnGifterSlime(string nickname)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) return;
        if (CountActiveSlimes() >= 10) return; // ⚠️ общий лимит на всех слизней

        Main.QueueMainThreadAction(() =>
        {
            var player = Main.LocalPlayer;

            int npcID = NPC.NewNPC(
                player.GetSource_FromThis(),
                (int)player.position.X + Main.rand.Next(-200, 200),
                (int)player.position.Y,
                NPCID.GoldenSlime
            );

            if (npcID >= 0)
            {
                NPC npc = Main.npc[npcID];
                npc.friendly = true;
                npc.damage = 20;       // можно чуть больше или меньше по желанию
                npc.lifeMax = 500;
                npc.life = 500;
                npc.defense = 40;
                npc.knockBackResist = 0.3f;
                //npc.chaseable = true;
                //npc.target = player.whoAmI;
                npc.timeLeft = int.MaxValue; // чтобы Terraria не убила NPC раньше
                var global = npc.GetGlobalNPC<ViewerSlimesGlobal>();
                global.viewerName = NickSanitizer.Sanitize(nickname);
                global.isViewer = true;
                global.isGifter = true;
                var lifetime = npc.GetGlobalNPC<VisualLifetimeGlobalNPC>();
                lifetime.SetLifetime(300);
                npc.netUpdate = true;
            }

            Main.NewText($"[Даритель] {nickname} прибыл!", 255, 215, 0);
        });
    }

    private static void SpawnShareSlime(string nickname)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) return;
        Main.QueueMainThreadAction(() =>
        {
            var player = Main.LocalPlayer;
            int npcID = NPC.NewNPC(
                player.GetSource_FromThis(),
                (int)player.Center.X + Main.rand.Next(-200, 200),
                (int)player.Center.Y - 100,
                NPCID.RainbowSlime
            );
            if (npcID >= 0)
            {
                NPC npc = Main.npc[npcID];
                npc.friendly = true;
                npc.damage = 10;
                npc.lifeMax = 150;
                npc.life = 150;
                npc.defense = 20;
                npc.knockBackResist = 0.5f;
                //npc.chaseable = true;
                //npc.target = player.whoAmI;
                npc.timeLeft = int.MaxValue; // важно!
                var global = npc.GetGlobalNPC<ViewerSlimesGlobal>();
                global.isViewer = true;
                global.isRainbow = true;
                global.viewerName = NickSanitizer.Sanitize(nickname);
                var lifetime = npc.GetGlobalNPC<VisualLifetimeGlobalNPC>();
                lifetime.SetLifetime(15);
                npc.netUpdate = true;
            }
            Main.NewText($"[Share] {nickname} поделился стримом!", new Color(255, 182, 193));
        });
    }

    #region TEST METHODS

    public static void TestGift(int giftId = 5655, int count = 1)
    {
        var fakeGift = new TikGiftEvent
        {
            UserName = TikTestFactory.RandomName(),
            RepeatCount = count
        };
        GiftEnemySpawner.SpawnGiftEnemy("TEST_USER", 5);
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

        SpawnVeteranSlime(fakeJoinSubscriber.UserName);
        Main.NewText($"[TEST] Subscriber joined: {fakeJoinSubscriber.UserName}", Color.Gold);
    }

    public static void TestModeratorJoin()
    {
        var fakeJoinModerator = new TikJoinEvent
        {
            UserId = TikTestFactory.RandomId(),
            UserName = "MOD_" + TikTestFactory.RandomName()
        };

        SpawnModeratorSlime(fakeJoinModerator.UserName);
        Main.NewText($"[TEST] Moderator joined: {fakeJoinModerator.UserName}", Color.Red);
    }

    public static void TestGifterJoin()
    {
        var fakeJoinGifter = new TikJoinEvent
        {
            UserId = TikTestFactory.RandomId(),
            UserName = "Gift_" + TikTestFactory.RandomName()
        };

        SpawnGifterSlime(fakeJoinGifter.UserName);
        Main.NewText($"[TEST] Gifter joined: {fakeJoinGifter.UserName}", Color.Red);
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