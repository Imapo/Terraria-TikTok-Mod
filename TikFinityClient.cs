// TikFinityClient.cs
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using System.IO;
using System.Text.Encodings.Web;

public class TikFinityClient : ModSystem
{
    private static readonly string ViewerDatabaseFilePath = Path.Combine(Main.SavePath, "TikFinity_ViewerDatabase.json");
    private static readonly string SubscriberHistoryFilePath = Path.Combine(Main.SavePath, "TikFinity_SubscriberHistory.json");
    private static List<SubscriberHistoryEntry> subscriberHistory = new List<SubscriberHistoryEntry>();
    private static ClientWebSocket socket;
    private static CancellationTokenSource cancelToken;
    private static Dictionary<string, ViewerInfo> viewerDatabase = new Dictionary<string, ViewerInfo>();
    private static HashSet<string> veteranSpawnedThisSession = new HashSet<string>();
    public static HashSet<string> SubscriberIds = new HashSet<string>();
    private const int MAX_VIEWERS = 500;
    private static readonly string GiftHistoryFilePath =
    Path.Combine(Main.SavePath, "TikFinity_GiftHistory.json");
    private static List<GiftHistoryEntry> giftHistory = new();
    public static HashSet<string> GiftGiverIds = new();
    private static readonly string ModeratorDatabaseFilePath = Path.Combine(Main.SavePath, "TikFinity_ModeratorDatabase.json");
    private static Dictionary<string, ModeratorInfo> moderatorDatabase = new Dictionary<string, ModeratorInfo>();
    private static bool _hasLoggedFirstMessage = false;
    private static bool _hasShownStreamerName = false;
    private static Dictionary<string, int> likeComboCounter = new Dictionary<string, int>();
    // Проверка по истории дарителей
    private bool IsFollower(JsonElement root)
    {
        if (root.TryGetProperty("isSubscribed", out var sub1) && sub1.ValueKind == JsonValueKind.True)
            return true;

        if (root.TryGetProperty("isFollower", out var sub2) && sub2.ValueKind == JsonValueKind.True)
            return true;

        if (root.TryGetProperty("follow", out var sub3) && sub3.ValueKind == JsonValueKind.True)
            return true;

        if (root.TryGetProperty("event", out var ev) && ev.GetString() == "follow")
            return true;

        return false;
    }


    // -------------------------
    // Основной обработчик сообщений
    // -------------------------
    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string eventType = root.TryGetProperty("event", out var ev) ? ev.GetString() : "";
            JsonElement data = root.TryGetProperty("data", out var d) ? d : root;

            if (eventType == "roomUser" && !_hasShownStreamerName)
            {
                // Попробуем извлечь tikfinityUsername из корня сообщения
                if (root.TryGetProperty("tikfinityUsername", out var streamerNameElem) &&
                    !string.IsNullOrEmpty(streamerNameElem.GetString()))
                {
                    string streamerName = streamerNameElem.GetString().Trim();
                    Main.NewText($"[TikFinity] Connected to stream: {streamerName}", Color.Cyan);
                    _hasShownStreamerName = true;
                }
                else if (root.TryGetProperty("data", out var dataNested) && data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("tikfinityUsername", out var nameFromData) &&
                        !string.IsNullOrEmpty(nameFromData.GetString()))
                    {
                        string streamerName = nameFromData.GetString().Trim();
                        Main.NewText($"[TikFinity] Connected to stream: {streamerName}", Color.Cyan);
                        _hasShownStreamerName = true;
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
                    break;

                case "chat":
                    HandleChatEvent(key, nickname, isSubscriber, isModerator, isFollowing, data);
                    break;

                case "like":
                    ProcessLikeEvent(data, nickname);
                    break;

                case "gift":
                    int amount = data.TryGetProperty("coins", out var c) ? c.GetInt32() : 1;
                    AddGiftHistory(key, nickname, amount);
                    SpawnGiftFlyingFish(nickname, amount);
                    break;

                case "follow":
                    HandleSubscribeEvent(key, nickname, isModerator, isFollowing);
                    break;

                case "share":
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

    private void HandleShareEvent(string key, string nickname, bool isModerator, bool isFollowing)
    {
        // Логируем (опционально)
        AddOrUpdateViewer(key, nickname, isSubscriber: false, isModerator, isFollowing, "Share");

        if (Main.netMode == NetmodeID.MultiplayerClient) return;

        // Применяем случайный бафф
        ApplyRandomBuffFromShare();

        Main.QueueMainThreadAction(() =>
        {
            Player player = Main.LocalPlayer;
            if (Main.netMode == NetmodeID.Server)
            {
                // На сервере — выбираем первого активного игрока
                foreach (var plr in Main.player)
                {
                    if (plr.active && !plr.dead)
                    {
                        player = plr;
                        break;
                    }
                }
            }

            if (!player.active) return;

            // 🌈 Спавним радужного слизня с ником
            int npcID = NPC.NewNPC(
                player.GetSource_FromThis(),
                (int)player.Center.X + Main.rand.Next(-100, 100),
                (int)player.Center.Y - 100,
                NPCID.RainbowSlime
            );
            if (npcID >= 0)
            {
                NPC npc = Main.npc[npcID];
                npc.friendly = true;
                npc.timeLeft = 600; // 10 секунд

                // 🔥 Настраиваем GlobalNPC для отрисовки ника
                var g = npc.GetGlobalNPC<ImapoTikTokIntegrationModGlobal>();
                g.isViewer = true;
                g.viewerName = NickSanitizer.Sanitize(nickname);
                g.isRainbow = true; // ← новая строка
                g.isSeagull = false; // не морская чайка

                npc.netUpdate = true;
            }

            // 🎉 Сообщение в чат
            Main.NewText($"[Share] {nickname} поделился стримом!", new Color(255, 182, 193));
        });
    }

    private void ApplyRandomBuffFromShare()
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

        if (moderatorDatabase.TryGetValue(key, out var existing))
        {
            existing.Nickname = nickname;
            existing.SourceEvent = sourceEvent;
            existing.TextMessage = textMessage;
            existing.Time = now;
        }
        else
        {
            moderatorDatabase[key] = new ModeratorInfo
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
                moderatorDatabase.Clear();
                foreach (var m in list)
                {
                    if (!string.IsNullOrEmpty(m.Key))
                        moderatorDatabase[m.Key] = m;
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
            var list = moderatorDatabase.Values.ToList();
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            string json = JsonSerializer.Serialize(list, options);
            File.WriteAllText(ModeratorDatabaseFilePath, json);
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                .Logger.Info($"[Tikfinity ERROR] Failed to update moderator JSON: {ex}");
        }
    }

    private static void ImportGiftHistory()
    {
        if (!File.Exists(GiftHistoryFilePath))
            return;

        var json = File.ReadAllText(GiftHistoryFilePath);
        var list = JsonSerializer.Deserialize<List<GiftHistoryEntry>>(json);

        if (list != null)
        {
            giftHistory = list;
            RebuildGiftGiverCache();
        }
    }


    private static void AddGiftHistory(string key, string nickname, int coins)
    {
        giftHistory.Add(new GiftHistoryEntry
        {
            Key = key,
            Nickname = nickname,
            Coins = coins,
            Time = DateTime.Now.ToString("dd.MM.yy HH:mm:ss")
        });

        // ограничим, например, до 300 подарков
        if (giftHistory.Count > 300)
            giftHistory.RemoveAt(0);

        File.WriteAllText(
            GiftHistoryFilePath,
            JsonSerializer.Serialize(giftHistory, new JsonSerializerOptions
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
        foreach (var g in giftHistory)
        {
            if (!string.IsNullOrEmpty(g.Key))
                GiftGiverIds.Add(g.Key);
        }
    }

    public class GiftHistoryEntry
    {
        public string Key { get; set; }
        public string Nickname { get; set; }
        public int Coins { get; set; }
        public string Time { get; set; }
    }

    private static void TrimViewerDatabase()
    {
        if (viewerDatabase.Count <= MAX_VIEWERS)
            return;

        var ordered = viewerDatabase
            .OrderBy(v =>
            {
                if (DateTime.TryParse(v.Value.Time, out var t))
                    return t;
                return DateTime.MinValue;
            })
            .ToList();

        int removeCount = viewerDatabase.Count - MAX_VIEWERS;

        for (int i = 0; i < removeCount; i++)
        {
            viewerDatabase.Remove(ordered[i].Key);
        }
    }

    private static void RebuildSubscriberCache()
    {
        SubscriberIds.Clear();
        foreach (var s in subscriberHistory)
        {
            if (!string.IsNullOrEmpty(s.Key))
                SubscriberIds.Add(s.Key);
        }
    }

    public class SubscriberHistoryEntry
    {
        public string Key { get; set; }
        public string Nickname { get; set; }
        public DateTime Timestamp { get; set; }
        public string EventType { get; set; } // subscribe, member, etc.
        // Человекочитаемая дата
        public string Time { get; set; }
    }

    public static void UpdateSubscriberHistoryJson(SubscriberHistoryEntry entry)
    {
        try
        {
            subscriberHistory.Add(entry);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            string json = JsonSerializer.Serialize(subscriberHistory, options);
            File.WriteAllText(SubscriberHistoryFilePath, json);
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                .Logger.Info($"[Tikfinity ERROR] Failed to update subscriber history JSON: {ex}");
        }
    }

    public static void ImportSubscriberHistory()
    {
        try
        {
            if (!File.Exists(SubscriberHistoryFilePath)) return;
            string json = File.ReadAllText(SubscriberHistoryFilePath);
            var list = JsonSerializer.Deserialize<List<SubscriberHistoryEntry>>(json);
            if (list != null)
            {
                subscriberHistory = list;
                RebuildSubscriberCache();
            }
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                .Logger.Info($"[Tikfinity ERROR] Failed to import subscriber history: {ex}");
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
            var list = viewerDatabase.Values.ToList();

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
                viewerDatabase.Clear();
                foreach (var v in list)
                {
                    if (!string.IsNullOrEmpty(v.Key))
                        viewerDatabase[v.Key] = v;
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
            var list = viewerDatabase.Values.ToList();
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            string json = JsonSerializer.Serialize(list, options);
            File.WriteAllText(ViewerDatabaseFilePath, json);
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<global::ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                .Logger.Info($"[Tikfinity ERROR] Failed to update viewer JSON: {ex}");
        }
    }

    // -------------------------
    // Жизненный цикл ModSystem
    // -------------------------
    public override void OnWorldLoad()
    {
        ImportGiftHistory();
        ImportViewerDatabase();
        ImportSubscriberHistory();
        StartClient();
    }

    public override void OnWorldUnload()
    {
        StopClient();
        UpdateViewerDatabaseJson();
        veteranSpawnedThisSession.Clear();
    }

    private async void StartClient()
    {
        _hasShownStreamerName = false;
        likeComboCounter.Clear();
        try
        {
            socket = new ClientWebSocket();
            cancelToken = new CancellationTokenSource();

            var uri = new Uri("ws://localhost:21213/");
            await socket.ConnectAsync(uri, cancelToken.Token);
            Main.NewText("[TikFinity SUCCESS] Initialized!", 50, 255, 50);
            _ = ListenLoop();
        }
        catch (Exception ex)
        {
            Main.NewText("[TikFinity ERROR] " + ex.Message, 255, 0, 0);
        }
    }

    private async void StopClient()
    {
        try
        {
            cancelToken?.Cancel();
            socket?.Dispose();
        }
        catch { }
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
            nickname = nickname.Substring(0, 17) + "...";

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

    private void AddOrUpdateViewer(
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

        if (sourceEvent == "config" || sourceEvent == "Unknown")
            return;

        // ❌ ChatMessage без текста — не пишем
        if (sourceEvent == "ChatMessage" && string.IsNullOrWhiteSpace(textMessage))
            return;

        string now = DateTime.Now.ToString("dd.MM.yy HH:mm:ss");

        if (viewerDatabase.TryGetValue(key, out var existing))
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
            viewerDatabase[key] = new ViewerInfo
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
            var entry = new SubscriberHistoryEntry
            {
                Key = key,
                Nickname = nickname,
                Timestamp = DateTime.UtcNow,
                EventType = "follow",
                Time = DateTime.Now.ToString("dd.MM.yy HH:mm:ss")
            };

            UpdateSubscriberHistoryJson(entry);
            RebuildSubscriberCache();
        }

        if (isModerator)
        {
            AddOrUpdateModerator(key, nickname, "ChatMessage", ExtractCommentText(data));
        }

        ProcessChatMessage(data, nickname);
    }

    private void HandleSubscribeEvent(string key, string nickname, bool isModerator, bool isFollowing)
    {
        AddOrUpdateViewer(key, nickname, true, isModerator, isFollowing, "Subscribe");

        SpawnSubscriberSlime(nickname);

        // --- записываем в историю ---
        var entry = new SubscriberHistoryEntry
        {
            Key = key,
            Nickname = nickname,
            Timestamp = DateTime.UtcNow,
            EventType = "subscribe",
            Time = DateTime.Now.ToString("dd.MM.yy HH:mm:ss")
        };
        UpdateSubscriberHistoryJson(entry);
        RebuildSubscriberCache();
    }

    private void HandleJoinEvent(string key, string nickname)
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
        if (moderatorDatabase.ContainsKey(key))
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

        if (text.Length > 50)
            text = text.Substring(0, 47) + "...";

        return text;
    }

    // -------------------------
    // Остальные вспомогательные методы / спавн (твои существующие)
    // -------------------------
    private string ReplaceEmojis(string input)
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

    // --- SpawnViewerButterfly / SpawnCommentFirefly / SpawnGiftFlyingFish / SpawnSubscriberSlime / SpawnVeteranSlime ---
    // Использую твою существующую реализацию (обёрнутые вызовы) — просто вызываю их как есть.

    private void SpawnViewerButterfly(string nickname, string viewerId)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
            return;

        // 1. Санитизация ника
        string cleanName = NickSanitizer.Sanitize(nickname).Trim();

        // Если ник пустой — используем ID
        if (string.IsNullOrWhiteSpace(cleanName))
            cleanName = viewerId;

        // 2. Проверяем уникальность бабочки ПО rawId
        if (Main.npc.Any(n =>
            n.active &&
            n.type == NPCID.Butterfly &&
            n.TryGetGlobalNPC(out ViewerButterflyGlobal g) &&
            g.isViewerButterfly &&
            g.rawId == viewerId))
            return;

        // 3. Спавним бабочку
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

    private void SpawnCommentFirefly(string nickname, string comment)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) return;

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

                npc.timeLeft = 600;
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

    private void SpawnGiftFlyingFish(string nickname, int goldCoins)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) return;

        Main.QueueMainThreadAction(() =>
        {
            var player = Main.LocalPlayer;

            int npcType = NPCID.FlyingFish;
            if (goldCoins >= 10)
                npcType = NPCID.Mimic;

            int npcID = NPC.NewNPC(
                player.GetSource_FromThis(),
                (int)player.position.X + Main.rand.Next(-300, 300),
                (int)player.position.Y,
                npcType
            );

            if (npcID >= 0)
            {
                NPC npc = Main.npc[npcID];

                if (npcType == NPCID.FlyingFish || npcType == NPCID.Mimic)
                {
                    var global = npc.GetGlobalNPC<GiftFlyingFishGlobal>();
                    global.giverName = nickname;
                    global.goldInside = goldCoins;
                }

                npc.netUpdate = true;
            }
        });
    }

    private void SpawnSubscriberSlime(string nickname)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) return;

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
                npc.lifeMax = 350;
                npc.life = 250;
                npc.defense = 30;
                npc.knockBackResist = 0.5f;
                npc.chaseable = true;

                var global = npc.GetGlobalNPC<ImapoTikTokIntegrationModGlobal>();
                global.viewerName = NickSanitizer.Sanitize(nickname);
                global.isSeagull = false;
                global.isViewer = true;

                npc.netUpdate = true;
            }

            Main.NewText($"[Новый подписчик] {nickname}!", 255, 10, 100);
        });
    }

    private void SpawnVeteranSlime(string nickname)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) return;

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

                var global = npc.GetGlobalNPC<ImapoTikTokIntegrationModGlobal>();
                global.viewerName = NickSanitizer.Sanitize(nickname);
                global.isViewer = true;
                global.isVeteran = true;

                npc.netUpdate = true;
            }

            Main.NewText($"[Подписчик] {nickname} прибыл!", 255, 215, 0);
        });
    }

    private void SpawnModeratorSlime(string nickname)
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
                npc.damage = 25;       // чуть сильнее обычного слизня
                npc.lifeMax = 400;
                npc.life = 400;
                npc.defense = 35;
                npc.knockBackResist = 0.5f;
                npc.chaseable = true;

                var global = npc.GetGlobalNPC<ImapoTikTokIntegrationModGlobal>();
                global.viewerName = NickSanitizer.Sanitize(nickname);
                global.isSeagull = false;
                global.isViewer = true;
                global.isVeteran = true; // ⚡ модератор

                npc.netUpdate = true;
            }

            Main.NewText($"[Модератор] {nickname} прибыл!", 255, 80, 20);
        });
    }

    private void SpawnGifterSlime(string nickname)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) return;

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
                npc.chaseable = true;

                var global = npc.GetGlobalNPC<ImapoTikTokIntegrationModGlobal>();
                global.viewerName = NickSanitizer.Sanitize(nickname);
                global.isSeagull = false;
                global.isViewer = true;
                // ⚡ можем добавить отдельный флаг, если нужно различать в PostDraw
                // например, global.isGifter = true;

                npc.netUpdate = true;
            }

            Main.NewText($"[Даритель] {nickname} прибыл!", 255, 215, 0);
        });
    }

}
