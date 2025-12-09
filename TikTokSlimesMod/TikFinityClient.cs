using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

public class TikFinityClient : ModSystem
{
    private static ClientWebSocket socket;
    private static CancellationTokenSource cancelToken;
    private static Dictionary<string, int> viewerLikes = new Dictionary<string, int>();

    public override void OnWorldLoad()
    {
        StartClient();
    }

    public override void OnWorldUnload()
    {
        StopClient();
    }

    private async void StartClient()
    {
        try
        {
            socket = new ClientWebSocket();
            cancelToken = new CancellationTokenSource();

            var uri = new Uri("ws://localhost:21213/");
            await socket.ConnectAsync(uri, cancelToken.Token);

            Main.NewText("[TikFinity] Connected!", 0, 255, 0);

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

        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;

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

            string fullMessage = messageBuilder.ToString();
            messageBuilder.Clear();

            HandleMessage(fullMessage);
        }
    }


    private void HandleMessage(string json)
    {
        try
        {
            json = json.Trim();
            if (!json.StartsWith("{") || !json.EndsWith("}"))
                return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 1. Сначала извлекаем никнейм ЕДИНЫМ методом
            string nickname = ExtractNickname(root);

            // Если не удалось извлечь ник - выходим
            if (string.IsNullOrEmpty(nickname))
                return;

            // 2. Определяем тип события
            string eventType = "";
            if (root.TryGetProperty("event", out var eventProp))
            {
                eventType = eventProp.GetString();
            }

            // 3. Обработка по типу события
            switch (eventType)
            {
                case "member":
                case "roomUser":
                case "join":
                case "": // Если нет поля event, считаем входом
                    SpawnViewerButterfly(nickname);
                    break;

                case "like":
                    ProcessLikeEvent(root, nickname);
                    break;

                case "chat": // Или "comment", "message" - проверьте какое событие приходит
                    ProcessChatMessage(root, nickname);
                    break;
                // Можно добавить другие события
                default:
                    // Для неизвестных событий тоже спавним обычного слизня
                    SpawnViewerButterfly(nickname);
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

    // 📝 МЕТОД ОБРАБОТКИ КОММЕНТАРИЯ
    private void ProcessChatMessage(JsonElement root, string nickname)
    {
        // 1. Извлекаем текст комментария
        string commentText = ExtractCommentText(root);

        if (string.IsNullOrEmpty(commentText))
            return;

        // 2. Спавним чайку с комментарием
        SpawnSeagullWithComment(nickname, commentText);
    }

    // 📝 ИЗВЛЕЧЕНИЕ ТЕКСТА КОММЕНТАРИЯ
    private string ExtractCommentText(JsonElement root)
    {
        string text = "";

        // 1. Прямое поле в корне
        if (root.TryGetProperty("text", out var textProp) && !string.IsNullOrWhiteSpace(textProp.GetString()))
        {
            text = textProp.GetString().Trim();
        }
        else if (root.TryGetProperty("comment", out var commentProp) && !string.IsNullOrWhiteSpace(commentProp.GetString()))
        {
            text = commentProp.GetString().Trim();
        }
        // 2. В data
        else if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object)
        {
            if (dataProp.TryGetProperty("text", out var dataTextProp) && !string.IsNullOrWhiteSpace(dataTextProp.GetString()))
            {
                text = dataTextProp.GetString().Trim();
            }
            else if (dataProp.TryGetProperty("comment", out var dataCommentProp) && !string.IsNullOrWhiteSpace(dataCommentProp.GetString()))
            {
                text = dataCommentProp.GetString().Trim();
            }
            else if (dataProp.TryGetProperty("content", out var contentProp) && !string.IsNullOrWhiteSpace(contentProp.GetString()))
            {
                text = contentProp.GetString().Trim();
            }
        }

        // Ограничиваем длину (чайке много не унести)
        if (text.Length > 50)
            text = text.Substring(0, 47) + "...";

        return text;
    }

    // 📝 МЕТОД ИЗВЛЕЧЕНИЯ НИКНЕЙМА (универсальный)
    private string ExtractNickname(JsonElement root)
    {
        string nickname = "";

        // 1. Прямые поля в корне
        if (root.TryGetProperty("nickname", out var nickProp) && !string.IsNullOrWhiteSpace(nickProp.GetString()))
        {
            nickname = nickProp.GetString().Trim();
        }
        else if (root.TryGetProperty("uniqueId", out var idProp) && !string.IsNullOrWhiteSpace(idProp.GetString()))
        {
            nickname = idProp.GetString().Trim();
            if (nickname.StartsWith("@")) nickname = nickname.Substring(1);
        }

        // 2. Вложенный объект data
        if (string.IsNullOrEmpty(nickname) &&
            root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object)
        {
            // Прямо в data
            if (dataElement.TryGetProperty("nickname", out var dataNickProp) && !string.IsNullOrWhiteSpace(dataNickProp.GetString()))
            {
                nickname = dataNickProp.GetString().Trim();
            }
            else if (dataElement.TryGetProperty("uniqueId", out var dataIdProp) && !string.IsNullOrWhiteSpace(dataIdProp.GetString()))
            {
                nickname = dataIdProp.GetString().Trim();
                if (nickname.StartsWith("@")) nickname = nickname.Substring(1);
            }
            // data.user
            else if (dataElement.TryGetProperty("user", out var dataUserProp) && dataUserProp.ValueKind == JsonValueKind.Object)
            {
                if (dataUserProp.TryGetProperty("nickname", out var userNickProp) && !string.IsNullOrWhiteSpace(userNickProp.GetString()))
                {
                    nickname = userNickProp.GetString().Trim();
                }
                else if (dataUserProp.TryGetProperty("uniqueId", out var userIdProp) && !string.IsNullOrWhiteSpace(userIdProp.GetString()))
                {
                    nickname = userIdProp.GetString().Trim();
                    if (nickname.StartsWith("@")) nickname = nickname.Substring(1);
                }
            }
        }

        // 3. Вложенный объект user (в корне)
        if (string.IsNullOrEmpty(nickname) &&
            root.TryGetProperty("user", out var userElement) && userElement.ValueKind == JsonValueKind.Object)
        {
            if (userElement.TryGetProperty("nickname", out var userNickProp) && !string.IsNullOrWhiteSpace(userNickProp.GetString()))
            {
                nickname = userNickProp.GetString().Trim();
            }
            else if (userElement.TryGetProperty("uniqueId", out var userIdProp) && !string.IsNullOrWhiteSpace(userIdProp.GetString()))
            {
                nickname = userIdProp.GetString().Trim();
                if (nickname.StartsWith("@")) nickname = nickname.Substring(1);
            }
        }

        // 4. Ограничиваем длину
        if (!string.IsNullOrEmpty(nickname) && nickname.Length > 20)
            nickname = nickname.Substring(0, 17) + "...";

        return nickname;
    }

    // 📝 МЕТОД ОБРАБОТКИ ЛАЙКОВ
    // Внутри вашего TikFinityClient
    private void ProcessLikeEvent(JsonElement root, string nickname)
    {
        int likeCount = 1;

        if (root.TryGetProperty("count", out var countProp) && countProp.ValueKind == JsonValueKind.Number)
        {
            likeCount = countProp.GetInt32();
        }
        else if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object)
        {
            if (dataElement.TryGetProperty("count", out var dataCountProp) && dataCountProp.ValueKind == JsonValueKind.Number)
                likeCount = dataCountProp.GetInt32();
            else if (dataElement.TryGetProperty("likeCount", out var likeCountProp) && likeCountProp.ValueKind == JsonValueKind.Number)
                likeCount = likeCountProp.GetInt32();
        }

        // Обновляем счетчик лайков
        if (!viewerLikes.ContainsKey(nickname))
            viewerLikes[nickname] = 0;

        viewerLikes[nickname] += likeCount;

        // Лечим игрока и отображаем ник
        Main.QueueMainThreadAction(() =>
        {
            var player = Main.LocalPlayer;

            // Лечение на 1 за каждый лайк
            player.statLife += likeCount;
            if (player.statLife > player.statLifeMax2)
                player.statLife = player.statLifeMax2;

            // Отображаем ник лайкера через CombatText
            CombatText.NewText(
                player.getRect(),
                Microsoft.Xna.Framework.Color.LimeGreen,
                nickname
            );
        });
    }


    private void SpawnRedSlime(string nickname)
    {
        if (Main.netMode == 1) return;

        Main.QueueMainThreadAction(() =>
        {
            var player = Main.LocalPlayer;

            int npcID = NPC.NewNPC(
                player.GetSource_FromThis(),
                (int)player.position.X + Main.rand.Next(-200, 200),
                (int)player.position.Y - 200,
                NPCID.RedSlime
            );

            if (npcID >= 0)
            {
                NPC npc = Main.npc[npcID];
                npc.GetGlobalNPC<ViewerSlimeGlobal>().viewerName = nickname;
            }
        });
    }

    private void SpawnViewerButterfly(string name)
    {
        if (Main.netMode == 1) return;

        Main.QueueMainThreadAction(() =>
        {
            var player = Main.LocalPlayer;

            int npcID = NPC.NewNPC(
                player.GetSource_FromThis(),
                (int)player.position.X + Main.rand.Next(-200, 200),
                (int)player.position.Y - 100, // чуть выше игрока
                NPCID.Butterfly   // ✅ бабочка вместо синего слизня
            );

            if (npcID >= 0)
            {
                NPC npc = Main.npc[npcID];

                var global = npc.GetGlobalNPC<ViewerSlimeGlobal>();
                global.viewerName = name;
                global.isSeagull = false; // это не комментарий, а новый зритель
            }
        });
    }

    private void SpawnSeagullWithComment(string nickname, string comment)
    {
        if (Main.netMode == 1) return;

        Main.QueueMainThreadAction(() =>
        {
            var player = Main.LocalPlayer;

            int npcID = NPC.NewNPC(
                player.GetSource_FromThis(),
                (int)player.position.X + Main.rand.Next(-300, 300),
                (int)player.position.Y - 50,
                NPCID.Bunny   // ✅ ЗАЯЦ ВМЕСТО ЧАЙКИ
            );

            // ✅ Вывод в чат
            string chatMessage = $"[TikTok] {nickname}: {comment}";
            Main.NewText(chatMessage, 180, 255, 180);

            // ✅ Заполнение GlobalNPC — ВАЖНО
            if (npcID >= 0)
            {
                NPC npc = Main.npc[npcID];

                var global = npc.GetGlobalNPC<ViewerSlimeGlobal>();
                global.viewerName = nickname;
                global.commentText = comment;
                global.isSeagull = true; // можно потом переименовать в isCommentNPC
            }
        });
    }


}