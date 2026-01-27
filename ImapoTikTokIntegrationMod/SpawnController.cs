// SpawnController.cs
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace ImapoTikTokIntegrationMod
{
    public static class SpawnController
    {
        // === КОНСТАНТЫ ===
        private const int MAX_BUTTERFLIES = 20;
        private const int MAX_FIREFLIES = 15;
        private const int MAX_SLIMES = 10;
        private const int MAX_DRAGONFLIES = 10;
        private const int MIN_SPAWN_DELAY_MS = 5000; // 5 сек между спавнами от одного зрителя

        // === ЗАЩИТА ОТ ГОНКИ УСЛОВИЙ ===
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, int> _likeComboCounter = new();
        private static readonly Dictionary<string, DateTime> _lastSpawnTime = new(); // защита от спама

        // === ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ===
        private static bool CanSpawn()
        {
            // Базовые проверки
            if (!TikFinityClient.worldLoaded || Main.gameMenu)
                return false;

            // Мультиплеер: только сервер спавнит
            if (Main.netMode == NetmodeID.MultiplayerClient)
                return false;

            return true;
        }

        private static Player GetTargetPlayer()
        {
            if (Main.netMode == NetmodeID.SinglePlayer)
                return Main.LocalPlayer?.active == true ? Main.LocalPlayer : null;

            return Main.player.FirstOrDefault(p => p.active && !p.dead);
        }

        // === ЗАЩИТА ОТ СПАМА: проверка задержки между спавнами ===
        private static bool IsSpam(string viewerKey)
        {
            lock (_lock)
            {
                if (_lastSpawnTime.TryGetValue(viewerKey, out var lastTime))
                {
                    if ((DateTime.Now - lastTime).TotalMilliseconds < MIN_SPAWN_DELAY_MS)
                        return true;
                }

                _lastSpawnTime[viewerKey] = DateTime.Now;
                return false;
            }
        }

        // === СЧЁТЧИКИ С АТОМАРНОЙ ЗАЩИТОЙ ===
        public static int CountActiveButterflies()
        {
            lock (_lock)
            {
                return Main.npc.Count(n =>
                    n.active &&
                    n.type == NPCID.Butterfly &&
                    n.TryGetGlobalNPC(out ViewerButterflyGlobal g) &&
                    g.isViewerButterfly
                );
            }
        }

        public static int CountActiveFireflies()
        {
            lock (_lock)
            {
                return Main.npc.Count(n =>
                    n.active &&
                    n.type == NPCID.Firefly &&
                    n.TryGetGlobalNPC(out ViewerFireflyGlobal g) &&
                    g.isViewer
                );
            }
        }

        public static int CountActiveSlimes()
        {
            lock (_lock)
            {
                return Main.npc.Count(n =>
                    n.active &&
                    n.TryGetGlobalNPC(out ViewerSlimesGlobal g) &&
                    g.isViewer
                );
            }
        }

        public static int CountActiveDragonflies()
        {
            lock (_lock)
            {
                return Main.npc.Count(n =>
                    n.active &&
                    n.type == NPCID.GreenDragonfly &&
                    n.TryGetGlobalNPC(out LikeFloatingTextGlobal g) &&
                    g != null
                );
            }
        }

        // === ОСНОВНОЙ СПАВН С ЗАЩИТОЙ ===
        private static bool TrySpawn(Action spawnAction, int currentCount, int maxCount, string entityType, string viewerKey = null)
        {
            lock (_lock)
            {
                // 1. Проверка лимита
                if (currentCount >= maxCount)
                {
                    ModContent.GetInstance<ImapoTikTokIntegrationMod>()
                        .Logger.Debug($"[SpawnController] Отмена {entityType}: достигнут лимит ({currentCount}/{maxCount})");
                    return false;
                }

                // 2. Проверка спама (только для зрителей)
                if (!string.IsNullOrEmpty(viewerKey) && IsSpam(viewerKey))
                {
                    ModContent.GetInstance<ImapoTikTokIntegrationMod>()
                        .Logger.Debug($"[SpawnController] Отмена {entityType}: спам от {viewerKey}");
                    return false;
                }

                // 3. Выполнение спавна
                spawnAction();
                return true;
            }
        }

        // =====================================================
        // 🦋 БАБОЧКА ЗРИТЕЛЯ (с защитой от спама)
        // =====================================================
        public static void SpawnViewerButterfly(string nickname, string viewerId)
        {
            if (!CanSpawn() || string.IsNullOrEmpty(viewerId))
                return;

            // Анти-дубликат (без лока — быстрая проверка)
            if (Main.npc.Any(n =>
                n.active &&
                n.type == NPCID.Butterfly &&
                n.TryGetGlobalNPC(out ViewerButterflyGlobal g) &&
                g.isViewerButterfly &&
                g.rawId == viewerId))
                return;

            TrySpawn(() =>
            {
                Main.QueueMainThreadAction(() =>
                {
                    if (!CanSpawn()) return;

                    var player = GetTargetPlayer();
                    if (player == null) return;

                    int id = NPC.NewNPC(
                        player.GetSource_FromThis(),
                        (int)player.Center.X + Main.rand.Next(-200, 200),
                        (int)player.Center.Y - 100,
                        NPCID.Butterfly
                    );

                    if (id < 0 || id >= Main.maxNPCs) return;

                    var npc = Main.npc[id];
                    if (!npc?.active == true) return;

                    if (npc.TryGetGlobalNPC(out ViewerButterflyGlobal g))
                    {
                        g.isViewerButterfly = true;
                        g.viewerName = NickSanitizer.Sanitize(nickname);
                        g.rawId = viewerId;
                        g.lifetime = 0;
                    }

                    npc.netUpdate = true;
                });
            },
            CountActiveButterflies(),
            MAX_BUTTERFLIES,
            "бабочка",
            viewerId
            );
        }

        // =====================================================
        // ✨ СВЕТЛЯЧОК КОММЕНТАРИЯ
        // =====================================================
        public static void SpawnCommentFirefly(string nickname, string comment)
        {
            if (!CanSpawn() || string.IsNullOrEmpty(comment))
                return;

            comment = ReplaceEmojis(comment);

            TrySpawn(() =>
            {
                Main.QueueMainThreadAction(() =>
                {
                    if (!CanSpawn()) return;

                    var player = GetTargetPlayer();
                    if (player == null) return;

                    int id = NPC.NewNPC(
                        player.GetSource_FromThis(),
                        (int)player.Center.X + Main.rand.Next(-300, 300),
                        (int)player.Center.Y - 100,
                        NPCID.Firefly
                    );

                    if (id < 0 || id >= Main.maxNPCs) return;

                    var npc = Main.npc[id];
                    if (!npc?.active == true) return;

                    if (npc.TryGetGlobalNPC(out ViewerFireflyGlobal g))
                    {
                        g.viewerName = NickSanitizer.Sanitize(nickname);
                        g.commentText = comment;
                        g.isViewer = true;
                        g.isComment = true;
                    }

                    npc.timeLeft = 300;
                    npc.netUpdate = true;

                    // Чат-сообщение
                    var msg = $"[Чат] {nickname}: {comment}";
                    if (Main.netMode == NetmodeID.SinglePlayer)
                        Main.NewText(msg, Color.White);
                    else
                        Terraria.Chat.ChatHelper.BroadcastChatMessage(
                            Terraria.Localization.NetworkText.FromLiteral(msg),
                            new Color(180, 255, 180)
                        );
                });
            },
            CountActiveFireflies(),
            MAX_FIREFLIES,
            "светлячок"
            );
        }

        // =====================================================
        // 🟢 СТРЕКОЗА ЛАЙКОВ (с комбо-счётчиком)
        // =====================================================
        public static void SpawnLikeDragonfly(string viewerKey, string nickname, int likeIncrement)
        {
            if (!CanSpawn() || string.IsNullOrEmpty(viewerKey))
                return;

            lock (_lock)
            {
                // Обновление комбо
                if (_likeComboCounter.ContainsKey(viewerKey))
                    _likeComboCounter[viewerKey] += likeIncrement;
                else
                    _likeComboCounter[viewerKey] = likeIncrement;

                int totalLikes = _likeComboCounter[viewerKey];

                // Поиск существующей стрекозы
                var existing = Main.npc.FirstOrDefault(n =>
                    n.active &&
                    n.type == NPCID.GreenDragonfly &&
                    n.TryGetGlobalNPC(out LikeFloatingTextGlobal g) &&
                    g?.viewerKey == viewerKey
                );

                if (existing != null && existing.TryGetGlobalNPC(out LikeFloatingTextGlobal g))
                {
                    g.likeCount = totalLikes;
                    g.TriggerCombo(existing.Center + new Vector2(0, -50));
                    existing.netUpdate = true;
                    return;
                }

                // Спавн новой стрекозы
                TrySpawn(() =>
                {
                    Main.QueueMainThreadAction(() =>
                    {
                        if (!CanSpawn()) return;

                        var player = GetTargetPlayer();
                        if (player == null) return;

                        int id = NPC.NewNPC(
                            player.GetSource_FromThis(),
                            (int)player.Center.X + Main.rand.Next(-30, 30),
                            (int)player.Center.Y - 50,
                            NPCID.GreenDragonfly
                        );

                        if (id < 0 || id >= Main.maxNPCs) return;

                        var npc = Main.npc[id];
                        if (!npc?.active == true) return;

                        npc.friendly = true;
                        npc.dontTakeDamage = true;
                        npc.noGravity = true;
                        npc.noTileCollide = true;
                        npc.life = npc.lifeMax = 1;
                        npc.timeLeft = LikeFloatingTextGlobal.MaxLife;

                        if (npc.TryGetGlobalNPC(out LikeFloatingTextGlobal newG))
                        {
                            newG.viewerKey = viewerKey;
                            newG.viewerName = NickSanitizer.Sanitize(nickname);
                            newG.likeCount = totalLikes;
                            newG.life = 0;
                            newG.TriggerCombo(npc.Center + new Vector2(0, -50));
                        }

                        npc.netUpdate = true;
                    });
                },
                CountActiveDragonflies(),
                MAX_DRAGONFLIES,
                "стрекоза"
                );
            }
        }

        // =====================================================
        // 🌈 РАДУЖНЫЙ СЛИЗЕНЬ (шеринг)
        // =====================================================
        public static void SpawnShareSlime(string nickname)
        {
            if (!CanSpawn())
                return;

            TrySpawn(() =>
            {
                Main.QueueMainThreadAction(() =>
                {
                    if (!CanSpawn()) return;

                    var player = GetTargetPlayer();
                    if (player == null) return;

                    int id = NPC.NewNPC(
                        player.GetSource_FromThis(),
                        (int)player.Center.X + Main.rand.Next(-200, 200),
                        (int)player.Center.Y - 100,
                        NPCID.RainbowSlime
                    );

                    if (id < 0 || id >= Main.maxNPCs) return;

                    var npc = Main.npc[id];
                    if (!npc?.active == true) return;

                    npc.friendly = true;
                    npc.damage = 10;
                    npc.lifeMax = npc.life = 150;
                    npc.defense = 20;
                    npc.knockBackResist = 0.5f;
                    npc.timeLeft = 60 * 60 * 3;

                    if (npc.TryGetGlobalNPC(out ViewerSlimesGlobal g))
                    {
                        g.isViewer = true;
                        g.isRainbow = true;
                        g.viewerName = NickSanitizer.Sanitize(nickname);
                    }

                    if (npc.TryGetGlobalNPC(out VisualLifetimeGlobalNPC v))
                        v.SetLifetime(15);

                    npc.netUpdate = true;

                    Main.NewText(
                        $"[Share] {nickname} поделился стримом!",
                        new Color(255, 182, 193)
                    );
                });
            },
            CountActiveSlimes(),
            MAX_SLIMES,
            "радужный слизень"
            );
        }

        // =====================================================
        // 🟢 СЛИЗНИ (подписчики, модераторы, дарители)
        // =====================================================
        private static void SpawnSlime(
            int npcType,
            string nickname,
            Action<ViewerSlimesGlobal> setup,
            int life,
            int defense,
            float kbResist,
            int visualLifetime,
            string entityType)
        {
            if (!CanSpawn())
                return;

            TrySpawn(() =>
            {
                Main.QueueMainThreadAction(() =>
                {
                    if (!CanSpawn()) return;

                    var player = GetTargetPlayer();
                    if (player == null) return;

                    int id = NPC.NewNPC(
                        player.GetSource_FromThis(),
                        (int)player.Center.X + Main.rand.Next(-200, 200),
                        (int)player.Center.Y,
                        npcType
                    );

                    if (id < 0 || id >= Main.maxNPCs) return;

                    var npc = Main.npc[id];
                    if (!npc?.active == true) return;

                    npc.friendly = true;
                    npc.damage = 20;
                    npc.lifeMax = npc.life = life;
                    npc.defense = defense;
                    npc.knockBackResist = kbResist;
                    npc.timeLeft = 60 * 60 * 5;

                    if (npc.TryGetGlobalNPC(out ViewerSlimesGlobal g))
                    {
                        g.isViewer = true;
                        g.viewerName = NickSanitizer.Sanitize(nickname);
                        setup?.Invoke(g);
                    }

                    if (npc.TryGetGlobalNPC(out VisualLifetimeGlobalNPC v))
                        v.SetLifetime(visualLifetime);

                    npc.netUpdate = true;
                });
            },
            CountActiveSlimes(),
            MAX_SLIMES,
            entityType
            );
        }

        // === КОНКРЕТНЫЕ ТИПЫ СЛИЗНЕЙ ===
        public static void SpawnSubscriberSlime(string nickname)
        {
            SpawnSlime(NPCID.BlueSlime, nickname, null, 250, 15, 0.5f, 60, "подписчик");
            Main.NewText($"[Новый подписчик] {nickname}!", new Color(255, 10, 100));
        }

        public static void SpawnVeteranSlime(string nickname)
        {
            SpawnSlime(NPCID.RedSlime, nickname, g => g.isVeteran = true, 500, 40, 0.3f, 150, "ветеран");
            Main.NewText($"[Подписчик] {nickname} прибыл!", new Color(255, 215, 0));
        }

        public static void SpawnModeratorSlime(string nickname)
        {
            SpawnSlime(NPCID.LavaSlime, nickname, g => g.isModerator = true, 400, 40, 0.5f, 300, "модератор");
            Main.NewText($"[Модератор] {nickname} прибыл!", new Color(255, 80, 20));
        }

        public static void SpawnGifterSlime(string nickname)
        {
            SpawnSlime(NPCID.GoldenSlime, nickname, g => g.isGifter = true, 500, 40, 0.3f, 300, "даритель");
            Main.NewText($"[Даритель] {nickname} прибыл!", new Color(255, 215, 0));
        }

        // === ВСПОМОГАТЕЛЬНОЕ ===
        private static string ReplaceEmojis(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            return text
                .Replace("😀", ":)")
                .Replace("😂", "xD")
                .Replace("❤️", "<3")
                .Replace("🔥", "FIRE")
                .Replace("👍", "+");
        }

        // === СБРОС СТАТИСТИКИ ПРИ ВЫГРУЗКЕ МИРА ===
        public static void OnWorldUnload()
        {
            lock (_lock)
            {
                _likeComboCounter.Clear();
                _lastSpawnTime.Clear();
            }
        }
    }
}