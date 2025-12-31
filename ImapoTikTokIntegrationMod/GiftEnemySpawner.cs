using ImapoTikTokIntegrationMod;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.Graphics.Effects;
using Terraria.Graphics.Shaders;
using Terraria.ID;
using Terraria.ModLoader;

namespace ImapoTikTokIntegrationMod
{
    // =========================================================
    // Boss definition
    // =========================================================

    public class BossDefinition
    {
        public int NpcType;
        public string DisplayName;

        public bool RequiresHardmode;
        public bool RequiresNight;
        public bool RequiresDay;
        public bool RequiresBloodMoon;

        // Доп условие (Crimson/Corruption, BloodMoon active, etc.)
        public Func<bool> ExtraCondition;

        // Подготовка мира (телепорт/ночь/ивент)
        public Action<Player> PrepareWorld;

        // 10 для Tier5, 30 для Tier6
        public int CountdownSeconds;
    }

    // =========================================================
    // Boss countdown HUD
    // =========================================================

    public class GiftNpcCleanupSystem : ModSystem
    {
        public override void PostUpdateWorld()
        {
            int before = GiftNpcTracker.ActiveCount;

            GiftNpcTracker.CleanupMissing();

            int after = GiftNpcTracker.ActiveCount;

            if (after < before)
            {
                // освобождаем очередь мобов
                GiftEnemySpawner.NotifyNpcSlotFreed(before - after);
            }
        }
    }
    public class BossCountdownSystem : ModSystem
    {
        public static int Timer;
        public static string BossName;
        public static bool Active;

        public override void PostDrawInterface(SpriteBatch spriteBatch)
        {
            if (!Active || Timer <= 0 || string.IsNullOrEmpty(BossName))
                return;

            string text = $"{BossName} [{Timer}]";
            var font = FontAssets.DeathText.Value;
            Vector2 size = font.MeasureString(text);

            spriteBatch.DrawString(
                font,
                text,
                new Vector2(Main.screenWidth / 2f - size.X / 2f, 70f),
                Color.OrangeRed
            );
        }
    }

    // =========================================================
    // Player hooks: on respawn -> re-prepare boss encounter
    // =========================================================
    public class BossRespawnPlayer : ModPlayer
    {
        public override void PreUpdate()
        {
            if (!Player.active || Player.dead)
            {
                BossQueueManager.PauseCountdown(Player);
            }
        }

        public override void OnRespawn()
        {
            BossQueueManager.ResumeCountdown(Player);
        }
    }

    // =========================================================
    // Boss queue manager: Tier5+Tier6 share one queue, 1 boss at a time
    // =========================================================
    internal static class GiftNpcTracker
    {
        public static void ResetAll()
        {
            ActiveGiftNpcs.Clear();
        }
        private static readonly HashSet<int> ActiveGiftNpcs = new();
        public static void Register(int whoAmI)
        {
            if (whoAmI >= 0 && whoAmI < Main.maxNPCs)
                ActiveGiftNpcs.Add(whoAmI);
        }

        public static void Unregister(int whoAmI)
        {
            ActiveGiftNpcs.Remove(whoAmI);
        }

        public static int ActiveCount => ActiveGiftNpcs.Count;

        public static void CleanupMissing()
        {
            ActiveGiftNpcs.RemoveWhere(i =>
                i < 0 ||
                i >= Main.maxNPCs ||
                !Main.npc[i].active
            );
        }
    }

    internal static class BossQueueManager
    {
        public static void ResetAll()
        {
            _queue.Clear();

            _state = BossState.Idle;
            _currentBoss = null;
            _currentGiver = null;
            _currentCoins = 0;
            _currentBossNpcWhoAmI = -1;

            _countdownPaused = false;
            _pausedPlayer = null;

            _preparationInProgress = false;
            _pendingWorldPrep = false;

            BossCountdownSystem.Active = false;
            BossCountdownSystem.Timer = 0;
            BossCountdownSystem.BossName = null;

            DisableMoonLordDistortion();
        }
        private static bool _countdownPaused = false;
        private static Player _pausedPlayer = null;
        public static bool IsCountdownActive => BossCountdownSystem.Active && !_countdownPaused;

        public static void PauseCountdown(Player player)
        {
            if (_state == BossState.Countdown || _state == BossState.Recovering)
            {
                _countdownPaused = true;
                _pausedPlayer = player;
                // HUD остаётся активным, но таймер не уменьшается
                _state = BossState.Recovering;
            }
        }

        public static void ResumeCountdown(Player player)
        {
            if (!_countdownPaused || player != _pausedPlayer)
                return;

            _countdownPaused = false;
            _pausedPlayer = null;

            _state = BossState.Countdown;

            // Если подготовка мира была отложена — делаем её теперь
            if (_pendingWorldPrep && _currentBoss != null)
            {
                PrepareWorldAndStartTimer(player, isRespawnRecovery: true);
            }
        }

        private enum BossState { Idle, Countdown, ActiveBoss, Recovering }

        private static readonly Queue<(BossDefinition boss, string giverName, int coins)> _queue = new();
        private static BossState _state = BossState.Idle;

        private static BossDefinition _currentBoss;
        private static string _currentGiver;
        private static int _currentCoins;

        private static int _currentBossNpcWhoAmI = -1;

        public static bool IsBusy => _state != BossState.Idle;

        public static void EnqueueBoss(BossDefinition boss, string giverName, int coins)
        {
            _queue.Enqueue((boss, giverName, coins));
            TryStartNext();
        }

        public static void TryStartNext()
        {
            if (Main.netMode == NetmodeID.MultiplayerClient)
                return;

            if (_state != BossState.Idle)
                return;

            if (_queue.Count == 0)
                return;

            var item = _queue.Dequeue();
            _currentBoss = item.boss;
            _currentGiver = item.giverName;
            _currentCoins = item.coins;
            _currentBossNpcWhoAmI = -1;

            StartPreparation(isRespawnRecovery: false);
        }

        public static void OnPlayerRespawned(Player player)
        {
            if (Main.netMode == NetmodeID.MultiplayerClient)
                return;

            if (player == null || !player.active)
                return;

            // Если игрок мёртв — ничего не делаем, подготовка отложится до появления
            if (player.dead)
                return;

            Main.QueueMainThreadAction(async () =>
            {
                // Ждём 1 секунду, чтобы игрок полностью загрузился
                await Task.Delay(1000);

                if (IsCurrentBossAlive())
                {
                    StartPreparation(isRespawnRecovery: true);
                    return;
                }

                ResetToIdle();
                TryStartNext();
            });
        }

        private static bool IsCurrentBossAlive()
        {
            if (_currentBoss == null) return false;

            // Если мы запомнили whoAmI — проверим его
            if (_currentBossNpcWhoAmI >= 0 &&
                _currentBossNpcWhoAmI < Main.maxNPCs &&
                Main.npc[_currentBossNpcWhoAmI].active &&
                Main.npc[_currentBossNpcWhoAmI].type == _currentBoss.NpcType)
                return true;

            // Иначе проверим по типу (надёжнее при релоаде/сетях)
            return NPC.AnyNPCs(_currentBoss.NpcType);
        }

        private static void EnableMoonLordDistortion()
        {
            if (Main.dedServ)
                return;

            if (!Filters.Scene["MoonLordShake"].IsActive())
            {
                Filters.Scene.Activate("MoonLordShake", Main.LocalPlayer.Center);
                Filters.Scene["MoonLordShake"].Opacity = 1f; // сила эффекта
            }
        }

        private static void DisableMoonLordDistortion()
        {
            if (Main.dedServ)
                return;

            if (Filters.Scene["MoonLordShake"].IsActive())
                Filters.Scene.Deactivate("MoonLordShake");
        }

        private static bool _preparationInProgress = false;
        private static bool _pendingWorldPrep = false;

        private static void StartPreparation(bool isRespawnRecovery)
        {
            if (_currentBoss == null || _preparationInProgress)
            {
                ResetToIdle();
                return;
            }

            _preparationInProgress = true;
            _state = isRespawnRecovery ? BossState.Recovering : BossState.Countdown;

            Player player = Main.LocalPlayer;
            if (player == null || !player.active || player.dead)
            {
                // Игрок мёртв — подготовку мира откладываем
                _pendingWorldPrep = true;
            }
            else
            {
                PrepareWorldAndStartTimer(player, isRespawnRecovery);
            }
        }
        private static void PrepareWorldAndStartTimer(Player player, bool isRespawnRecovery)
        {
            if (player == null || !player.active || player.dead)
            {
                _preparationInProgress = false;
                _pendingWorldPrep = true; // ждём респавна
                _state = BossState.Recovering;
                return;
            }

            if (_currentBoss == null)
            {
                _preparationInProgress = false;
                _pendingWorldPrep = false;
                ResetToIdle();
                TryStartNext();
                return;
            }

            // Если дошли сюда — все объекты валидные
            PrepareWorldForBoss(player, _currentBoss);
            EnableMoonLordDistortion();

            BossCountdownSystem.BossName = _currentBoss.DisplayName;
            BossCountdownSystem.Timer = _currentBoss.CountdownSeconds;
            BossCountdownSystem.Active = true;

            _preparationInProgress = false;
            _pendingWorldPrep = false;

            _ = RunCountdownAsync(isRespawnRecovery);
        }

        private static async Task RunCountdownAsync(bool isRespawnRecovery)
        {
            while (BossCountdownSystem.Timer > 0)
            {
                await Task.Delay(1000);

                Player player = Main.LocalPlayer;
                if (player == null || !player.active || player.dead)
                {
                    // игрок мёртв — таймер не уменьшаем, но продолжаем проверять каждую секунду
                    continue;
                }

                if (!_countdownPaused)
                    BossCountdownSystem.Timer--;
            }

            Player alivePlayer = Main.LocalPlayer;
            if (alivePlayer == null || !alivePlayer.active || alivePlayer.dead)
            {
                // оставляем таймер на 1 и ждём респавна
                BossCountdownSystem.Timer = 1;
                _state = BossState.Recovering;

                // дополнительная проверка: попробуем снова через секунду
                await Task.Delay(1000);
                _ = RunCountdownAsync(isRespawnRecovery);
                return;
            }

            BossCountdownSystem.Active = false;

            Main.QueueMainThreadAction(() =>
            {
                _preparationInProgress = false;

                if (!IsCurrentBossAlive())
                {
                    SpawnCurrentBoss();
                }
                else
                {
                    _state = BossState.ActiveBoss;
                }
            });
        }

        private static void PrepareWorldForBoss(Player player, BossDefinition boss)
        {
            if (player == null || !player.active)
                return;

            // Проверка биомов для специфичных боссов
            if (boss.NpcType == NPCID.BrainofCthulhu && !GiftEnemySpawner.WorldHasCrimson() ||
               (boss.NpcType == NPCID.EaterofWorldsHead && !GiftEnemySpawner.WorldHasCorruption()))
            {
                Main.NewText("⚠ Биомы зла не найдены — спавнится альтернативный босс", Color.Orange);

                // подбираем альтернативного босса
                var altBoss = PickAlternativeBoss(boss);
                if (altBoss != null)
                {
                    _currentBoss = altBoss;
                    _currentBossNpcWhoAmI = -1;
                }
                else
                {
                    ResetToIdle();
                    TryStartNext();
                    return;
                }
            }

            // День/ночь/кровавая луна
            if (boss.RequiresBloodMoon) WorldPrep.ForceBloodMoon();
            else
            {
                if (boss.RequiresNight) WorldPrep.ForceNight();
                if (boss.RequiresDay) WorldPrep.ForceDay();
            }

            // Телепорт
            boss.PrepareWorld?.Invoke(player);
        }

        // Метод подбирает альтернативного босса, который не требует биом
        private static BossDefinition PickAlternativeBoss(BossDefinition original)
        {
            var pool = GiftEnemySpawner.PickBossByCoinsPublic(_currentCoins);
            if (pool != null && (pool.NpcType != NPCID.BrainofCthulhu && pool.NpcType != NPCID.EaterofWorldsHead))
                return pool;
            return null;
        }

        private static void SpawnCurrentBoss()
        {
            if (_currentBoss == null)
            {
                ResetToIdle();
                return;
            }

            Player player = Main.LocalPlayer;
            if (player == null || !player.active || player.dead)
            {
                _state = BossState.Recovering;
                return;
            }

            // Если есть “extra condition” и она не выполняется — попробуем всё равно (или можно пропускать).
            if (_currentBoss.ExtraCondition != null && !_currentBoss.ExtraCondition())
            {
                // Мы уже телепортировали/подготовили — но условие всё ещё не выполнено.
                // Чтобы не стопорить очередь — всё равно попытаемся спавнить.
            }

            int npcId = NPC.NewNPC(
                player.GetSource_FromThis(),
                (int)player.Center.X,
                (int)player.Center.Y - 200,
                _currentBoss.NpcType
            );

            if (npcId >= 0)
            {
                _currentBossNpcWhoAmI = npcId;
                GiftNpcTracker.Register(npcId);

                NPC npc = Main.npc[npcId];
                npc.target = player.whoAmI;

                // Привязываем “дарителя/монеты” к боссу (чтобы текст/награды работали одинаково)
                var gift = npc.GetGlobalNPC<GiftFlyingFishGlobal>();
                gift.giverName = _currentGiver;
                gift.goldInside = _currentCoins;

                npc.netUpdate = true;

                int queuedBossesLeft = _queue.Count; // сколько боссов осталось ждать ПОСЛЕ этого

                Main.NewText(
                    $"!!! {_currentGiver} вызывает босса: {_currentBoss.DisplayName} [в очереди: {queuedBossesLeft}]",
                    Color.OrangeRed
                );
            }

            DisableMoonLordDistortion();

            _state = BossState.ActiveBoss;

            // Мониторим смерть/деспавн босса таймером (простая проверка)
            _ = MonitorBossAsync();
        }

        private static async Task MonitorBossAsync()
        {
            // пока босс жив — ждём
            while (IsCurrentBossAlive())
                await Task.Delay(1000);

            // босс умер/пропал — запускаем следующий
            Main.QueueMainThreadAction(() =>
            {
                ResetToIdle();
                TryStartNext();
            });
        }

        private static void ResetToIdle()
        {
            _state = BossState.Idle;
            _currentBoss = null;
            _currentGiver = null;
            _currentCoins = 0;
            _currentBossNpcWhoAmI = -1;
            BossCountdownSystem.Active = false;
            BossCountdownSystem.Timer = 0;
            BossCountdownSystem.BossName = null;
        }
    }

    // =========================================================
    // World helpers (teleport/biomes/night/bloodmoon)
    // =========================================================
    internal static class WorldPrep
    {
        public static void TeleportToCorruption(Player player)
        {
            TeleportToEvil(player, evilIsCrimson: false);
        }

        public static void TeleportToCrimson(Player player)
        {
            TeleportToEvil(player, evilIsCrimson: true);
        }

        private static void TeleportToEvil(Player player, bool evilIsCrimson)
        {
            int surfaceY = (int)Main.worldSurface;

            for (int attempt = 0; attempt < 2000; attempt++)
            {
                int x = WorldGen.genRand.Next(200, Main.maxTilesX - 200);
                int y = surfaceY - 20;

                for (int dy = 0; dy < 40; dy++)
                {
                    Tile tile = Framing.GetTileSafely(x, y + dy);
                    if (!tile.HasTile)
                        continue;

                    bool match =
                        evilIsCrimson
                            ? tile.TileType == TileID.CrimsonGrass || tile.TileType == TileID.Crimstone
                            : tile.TileType == TileID.CorruptGrass || tile.TileType == TileID.Ebonstone;

                    if (match)
                    {
                        Vector2 pos = new Vector2(x * 16, (y + dy - 3) * 16);
                        player.Teleport(pos);
                        return;
                    }
                }
            }

            // fallback — если биом реально уничтожен
            Main.NewText("⚠ Не удалось найти нужный биом", Color.Orange);
        }

        public static void TeleportToJungleSafe(Player player)
        {
            // 1️⃣ Пытаемся найти подземные джунгли
            for (int attempt = 0; attempt < 8000; attempt++)
            {
                int x = Main.rand.Next(200, Main.maxTilesX - 200);
                int y = Main.rand.Next((int)Main.worldSurface, Main.maxTilesY - 300);

                Tile tile = Framing.GetTileSafely(x, y);
                if (tile.HasTile && tile.TileType == TileID.JungleGrass)
                {
                    int ground = y;
                    while (ground < Main.maxTilesY && !Main.tile[x, ground].HasTile)
                        ground++;

                    if (IsSafe(player, x, ground - 3))
                    {
                        player.Teleport(new Vector2(x * 16, (ground - 3) * 16));
                        return;
                    }
                }
            }

            // 2️⃣ fallback — поверхность мира
            Main.NewText("⚠ Джунгли повреждены — телепорт на поверхность", Color.Orange);
            TeleportToSurface(player);
        }

        private static void TeleportByZone(Player player, Func<Player, bool> zoneCheck)
        {
            for (int x = 200; x < Main.maxTilesX - 200; x += 40)
            {
                int y = (int)Main.worldSurface;

                player.Teleport(new Vector2(x * 16, y * 16), 1);
                player.UpdateBiomes();

                if (zoneCheck(player))
                    return;
            }

            // fallback — база
            player.Teleport(new Vector2(Main.spawnTileX * 16, Main.spawnTileY * 16));
        }
        public static void ForceNight()
        {
            Main.dayTime = false;
            Main.time = 0;
            Main.bloodMoon = false;
        }

        public static void ForceDay()
        {
            Main.dayTime = true;
            Main.time = 0;
            Main.bloodMoon = false;
        }

        public static void ForceBloodMoon()
        {
            ForceNight();
            Main.bloodMoon = true;
        }

        private static bool IsSafe(Player player, int tileX, int tileY)
        {
            int width = player.width / 16;
            int height = player.height / 16;

            for (int x = tileX - width; x <= tileX + width; x++)
                for (int y = tileY - height; y <= tileY; y++)
                {
                    if (!WorldGen.InWorld(x, y))
                        return false;

                    if (Main.tile[x, y].HasTile && Main.tileSolid[Main.tile[x, y].TileType])
                        return false;
                }

            return true;
        }

        public static void TeleportToSurface(Player player)
        {
            for (int attempt = 0; attempt < 5000; attempt++)
            {
                int x = Main.rand.Next(200, Main.maxTilesX - 200);

                int y = (int)Main.worldSurface;
                while (y > 50 && !Main.tile[x, y].HasTile)
                    y++;

                if (IsSafe(player, x, y - 3))
                {
                    player.Teleport(new Vector2(x * 16, (y - 3) * 16));
                    return;
                }
            }

            // fallback — spawn мира
            player.Teleport(new Vector2(Main.spawnTileX * 16, (Main.spawnTileY - 3) * 16));
        }


        public static void TeleportToOcean(Player player)
        {
            int x = Main.rand.NextBool() ? 300 : Main.maxTilesX - 300;

            for (int y = (int)Main.worldSurface; y < Main.maxTilesY - 200; y++)
            {
                if (!Main.tile[x, y].HasTile && Main.tile[x, y + 1].HasTile)
                {
                    if (IsSafe(player, x, y - 2))
                    {
                        player.Teleport(new Vector2(x * 16, (y - 2) * 16));
                        return;
                    }
                }
            }

            TeleportToSurface(player);
        }

        public static void TeleportToBiomeByTile(Player player, ushort tileType, bool preferUnderground = false)
        {
            int yMin = preferUnderground ? (int)Main.worldSurface : 50;
            int yMax = preferUnderground ? Main.maxTilesY - 200 : (int)Main.worldSurface;

            for (int attempt = 0; attempt < 8000; attempt++)
            {
                int x = Main.rand.Next(200, Main.maxTilesX - 200);
                int y = Main.rand.Next(yMin, yMax);

                if (!Main.tile[x, y].HasTile || Main.tile[x, y].TileType != tileType)
                    continue;

                int ground = y;
                while (ground < Main.maxTilesY && !Main.tile[x, ground].HasTile)
                    ground++;

                if (IsSafe(player, x, ground - 3))
                {
                    player.Teleport(new Vector2(x * 16, (ground - 3) * 16));
                    return;
                }
            }

            TeleportToSurface(player);
        }
    }

    // =========================================================
    // Gift spawner (tiers 1-6)
    // =========================================================
    public static class GiftEnemySpawner
    {
        // ================================
        // ====== ПУЛЫ ПО СЛОЖНОСТИ ======
        // ================================
        public static bool WorldHasCrimson()
        {
            for (int x = 0; x < Main.maxTilesX; x++)
            {
                for (int y = 0; y < Main.maxTilesY; y++)
                {
                    Tile tile = Framing.GetTileSafely(x, y);
                    if (tile.HasTile &&
                        (tile.TileType == TileID.CrimsonGrass
                         || tile.TileType == TileID.Crimstone
                         || tile.TileType == TileID.Crimsand))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static bool WorldHasCorruption()
        {
            for (int x = 0; x < Main.maxTilesX; x++)
            {
                for (int y = 0; y < Main.maxTilesY; y++)
                {
                    Tile tile = Framing.GetTileSafely(x, y);
                    if (tile.HasTile &&
                        (tile.TileType == TileID.CorruptGrass
                         || tile.TileType == TileID.Ebonstone
                         || tile.TileType == TileID.Ebonsand))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        public static void ResetAll()
        {
            SpawnQueue.Clear();
            ActiveSpawned = 0;
        }
        public static void NotifyNpcSlotFreed(int count)
        {
            ActiveSpawned = Math.Max(ActiveSpawned - count, 0);
            TrySpawnNext();
        }
        private static readonly int[] Tier1Enemies = {
            NPCID.EaterofSouls,
            NPCID.MotherSlime,
            NPCID.AngryBones,
            NPCID.CaveBat,
            NPCID.JungleBat,
            NPCID.CorruptGoldfish,
            NPCID.Vulture,
            NPCID.Crab,
            NPCID.Antlion,
            NPCID.SpikeBall,
            NPCID.GoblinScout,
            NPCID.ToxicSludge,
            NPCID.SnowBalla,
            NPCID.IceSlime,
            NPCID.IceBat,
            NPCID.CorruptPenguin,
            NPCID.Crimera,
            NPCID.CochinealBeetle,
            NPCID.CyanBeetle,
            NPCID.LacBeetle,
            NPCID.SeaSnail,
            NPCID.Raven,
            NPCID.SlimeMasked,
            NPCID.SlimeRibbonWhite,
            NPCID.SlimeRibbonYellow,
            NPCID.SlimeRibbonGreen,
            NPCID.SlimeRibbonRed,
            NPCID.CrimsonPenguin,
            NPCID.DD2GoblinT1,
            NPCID.LarvaeAntlion,
            NPCID.Dandelion,
            NPCID.ShimmerSlime,
            NPCID.SandSlime,
        };

        private static readonly int[] Tier2Enemies = {
            NPCID.HornetFatty,
            NPCID.HornetHoney,
            NPCID.HornetLeafy,
            NPCID.HornetSpikey,
            NPCID.HornetStingy,
            NPCID.AnglerFish,
            NPCID.Pixie,
            NPCID.FireImp,
            NPCID.MeteorHead,
            NPCID.CursedSkull,
            NPCID.Tim,
            NPCID.CorruptBunny,
            NPCID.Harpy,
            NPCID.GiantBat,
            NPCID.Slimer,
            NPCID.SnowmanGangsta,
            NPCID.MisterStabby,
            NPCID.Lavabat,
            NPCID.Wolf,
            NPCID.SwampThing,
            NPCID.FaceMonster,
            NPCID.FloatyGross,
            NPCID.Crimslime,
            NPCID.SpikedIceSlime,
            NPCID.SnowFlinx,
            NPCID.SpikedJungleSlime,
            NPCID.HoppinJack,
            NPCID.Ghost,
            NPCID.CrimsonBunny,
            NPCID.GiantShelly,
            NPCID.GiantShelly2,
            NPCID.GiantWalkingAntlion,
            NPCID.GiantFlyingAntlion,
            NPCID.SlimeSpiked,
            NPCID.DD2GoblinT2,
            NPCID.DD2GoblinBomberT1,
            NPCID.DD2WyvernT1,
            NPCID.DD2JavelinstT1,
            NPCID.DD2SkeletonT1,
            NPCID.WalkingAntlion,
            NPCID.FlyingAntlion,
            NPCID.SporeBat,
        };

        private static readonly int[] Tier3Enemies = {
            NPCID.MartianTurret,
            NPCID.PigronCorruption,
            NPCID.PigronHallow,
            NPCID.PigronCrimson,
            NPCID.Gastropod,
            NPCID.Corruptor,
            NPCID.IceElemental,
            NPCID.Demon,
            NPCID.VoodooDemon,
            NPCID.ArmoredSkeleton,
            NPCID.Mummy,
            NPCID.DarkMummy,
            NPCID.LightMummy,
            NPCID.CorruptSlime,
            NPCID.Wraith,
            NPCID.CursedHammer,
            NPCID.EnchantedSword,
            NPCID.Mimic,
            NPCID.Unicorn,
            NPCID.SkeletonArcher,
            NPCID.ChaosElemental,
            NPCID.GiantFlyingFox,
            NPCID.GiantTortoise,
            NPCID.IceTortoise,
            NPCID.RuneWizard,
            NPCID.Herpling,
            NPCID.MossHornet,
            NPCID.Derpling,
            NPCID.CrimsonAxe,
            NPCID.Lihzahrd,
            NPCID.Moth,
            NPCID.IcyMerman,
            NPCID.FlyingSnake,
            NPCID.RainbowSlime,
            NPCID.AngryNimbus,
            NPCID.Parrot,
            NPCID.ZombieMushroom,
            NPCID.ZombieMushroomHat,
            NPCID.AnomuraFungus,
            NPCID.MushiLadybug,
            NPCID.IchorSticker,
            NPCID.SkeletonSniper,
            NPCID.SkeletonCommando,
            NPCID.AngryBonesBig,
            NPCID.AngryBonesBigMuscle,
            NPCID.AngryBonesBigHelmet,
            NPCID.CultistArcherBlue,
            NPCID.CultistArcherWhite,
            NPCID.DeadlySphere,
            NPCID.ShadowFlameApparition,
            NPCID.DesertGhoul,
            NPCID.DesertGhoulCorruption,
            NPCID.DesertGhoulCrimson,
            NPCID.DesertGhoulHallow,
            NPCID.DesertLamiaLight,
            NPCID.DesertLamiaDark,
            NPCID.DesertScorpionWalk,
            NPCID.DesertBeast,
            NPCID.DesertDjinn,
            NPCID.SandShark,
            NPCID.SandsharkCorrupt,
            NPCID.SandsharkCrimson,
            NPCID.SandsharkHallow,
            NPCID.DD2GoblinT3,
            NPCID.DD2GoblinBomberT2,
            NPCID.DD2GoblinBomberT3,
            NPCID.DD2WyvernT2,
            NPCID.DD2JavelinstT2,
            NPCID.DD2DarkMageT1,
            NPCID.DD2SkeletonT3,
            NPCID.DD2DrakinT2,
            NPCID.DD2KoboldWalkerT2,
            NPCID.DD2KoboldWalkerT3,
            NPCID.BloodMummy,
            NPCID.QueenSlimeMinionBlue,
            NPCID.QueenSlimeMinionPink,
            NPCID.QueenSlimeMinionPurple,
        };

        private static readonly int[] Tier4Enemies = {
            NPCID.RedDevil,
            NPCID.LihzahrdCrawler,
            NPCID.Golem,
            NPCID.RaggedCaster,
            NPCID.RaggedCasterOpenCoat,
            NPCID.Necromancer,
            NPCID.NecromancerArmored,
            NPCID.DiabolistRed,
            NPCID.DiabolistWhite,
            NPCID.DungeonSpirit,
            NPCID.GiantCursedSkull,
            NPCID.StardustJellyfishBig,
            NPCID.StardustSpiderBig,
            NPCID.StardustSoldier,
            NPCID.SolarDrakomire,
            NPCID.SolarDrakomireRider,
            NPCID.SolarSroller,
            NPCID.SolarCorite,
            NPCID.SolarSolenian,
            NPCID.NebulaBrain,
            NPCID.NebulaHeadcrab,
            NPCID.NebulaBeast,
            NPCID.NebulaSoldier,
            NPCID.VortexRifleman,
            NPCID.VortexHornetQueen,
            NPCID.VortexHornet,
            NPCID.VortexLarva,
            NPCID.VortexSoldier,
            NPCID.GoblinSummoner,
            NPCID.DD2WyvernT3,
            NPCID.DD2JavelinstT3,
            NPCID.DD2DrakinT3,
            NPCID.IceMimic,
            NPCID.RockGolem,
            NPCID.PirateGhost,
            NPCID.BigMimicCorruption,
            NPCID.BigMimicCrimson,
            NPCID.BigMimicHallow,
            NPCID.BigMimicJungle,
        };

        // ================================
        // ====== Tier5/6 Boss Pools ======
        // ================================
        private static readonly BossDefinition[] Tier5Bosses_PreHard = new[]
        {
            new BossDefinition {
                NpcType = NPCID.SandElemental, DisplayName = "Песчаный элементаль",
                RequiresHardmode = false, RequiresNight = false, CountdownSeconds = 20,
                PrepareWorld = null // телепорт не нужен
            },
            new BossDefinition {
                NpcType = NPCID.Deerclops, DisplayName = "Циклоп-Олень",
                RequiresHardmode = false, RequiresNight = false, CountdownSeconds = 20,
                PrepareWorld = null
            },
            new BossDefinition {
                NpcType = NPCID.BloodNautilus, DisplayName = "Кровавый наутилус",
                RequiresHardmode = false, RequiresBloodMoon = true, CountdownSeconds = 20,
                PrepareWorld = null
            },
            new BossDefinition {
                NpcType = NPCID.KingSlime, DisplayName = "Король слизней",
                RequiresHardmode = false, CountdownSeconds = 20,
                PrepareWorld = null
            },
            new BossDefinition {
                NpcType = NPCID.EyeofCthulhu, DisplayName = "Глаз Ктулху",
                RequiresHardmode = false, RequiresNight = true, CountdownSeconds = 20,
                PrepareWorld = null
            },
            new BossDefinition {
                NpcType = NPCID.BrainofCthulhu,
                DisplayName = "Мозг Ктулху",
                RequiresHardmode = false,
                CountdownSeconds = 20,
                ExtraCondition = () => WorldHasCrimson(),
                PrepareWorld = (p) => { WorldPrep.TeleportToCrimson(p); }
            },
            new BossDefinition {
                NpcType = NPCID.EaterofWorldsHead,
                DisplayName = "Пожиратель миров",
                RequiresHardmode = false,
                CountdownSeconds = 20,
                ExtraCondition = () => WorldHasCorruption(),
                PrepareWorld = (p) => { WorldPrep.TeleportToCorruption(p); }
            },
            new BossDefinition {
                NpcType = NPCID.SkeletronHead, DisplayName = "Скелетрон",
                RequiresHardmode = false, RequiresNight = true, CountdownSeconds = 20,
                PrepareWorld = null
            },
            new BossDefinition {
                NpcType = NPCID.QueenBee, DisplayName = "Королева пчёл",
                RequiresHardmode = false, CountdownSeconds = 20,
                PrepareWorld = (p) => {
                    WorldPrep.TeleportToJungleSafe(p);
                }
            },
        };


        private static readonly BossDefinition[] Tier5Bosses_Hardmode = new[]
        {
            new BossDefinition {
                NpcType = NPCID.PirateShip, DisplayName = "Пиратский корабль",
                RequiresHardmode = true, CountdownSeconds = 20,
                PrepareWorld = null
            },
            new BossDefinition {
                NpcType = NPCID.WyvernHead, DisplayName = "Виверна",
                RequiresHardmode = true, CountdownSeconds = 20,
                PrepareWorld = null
            },
            new BossDefinition {
                // твины — спавним только одного из пары за донат? ты хотел "Retinazer+Spazmatism".
                // В ванилле это два NPC. Мы добавим "вызов пары" отдельным кейсом ниже.
                NpcType = NPCID.Retinazer, DisplayName = "Близнецы (1/2)",
                RequiresHardmode = true, RequiresNight = true, CountdownSeconds = 20,
                PrepareWorld = null
            },
            new BossDefinition {
                NpcType = NPCID.TheDestroyer, DisplayName = "Разрушитель",
                RequiresHardmode = true, RequiresNight = true, CountdownSeconds = 20,
                PrepareWorld = null
            },
            new BossDefinition {
                NpcType = NPCID.SkeletronPrime, DisplayName = "Скелетрон Прайм",
                RequiresHardmode = true, RequiresNight = true, CountdownSeconds = 20,
                PrepareWorld = null
            },
            new BossDefinition {
                NpcType = NPCID.Plantera, DisplayName = "Плантера",
                RequiresHardmode = true, CountdownSeconds = 20,
                PrepareWorld = (p) => { WorldPrep.TeleportToBiomeByTile(p, TileID.JungleGrass, preferUnderground:true); }
            },
            new BossDefinition {
                NpcType = NPCID.Golem, DisplayName = "Голем",
                RequiresHardmode = true, CountdownSeconds = 20,
                PrepareWorld = (p) => { WorldPrep.TeleportToBiomeByTile(p, TileID.LihzahrdBrick, preferUnderground:true); }
            },
            new BossDefinition {
                NpcType = NPCID.HallowBoss, DisplayName = "Императрица Света",
                RequiresHardmode = true, RequiresNight = true, CountdownSeconds = 20,
                PrepareWorld = null
            },
            new BossDefinition {
                NpcType = NPCID.DukeFishron, DisplayName = "Герцог Рыброн",
                RequiresHardmode = true, CountdownSeconds = 20,
                PrepareWorld = (p) => { WorldPrep.TeleportToOcean(p); }
            },
            new BossDefinition {
                // “QueenBee в лесу” — просто телепорт на поверхность
                NpcType = NPCID.QueenBee, DisplayName = "Королева пчёл (лес)",
                RequiresHardmode = true, CountdownSeconds = 20,
                PrepareWorld = (p) => { WorldPrep.TeleportToSurface(p); }
            },
        };

        private static readonly BossDefinition[] Tier6Bosses = new[]
        {
            new BossDefinition {
                NpcType = NPCID.DungeonGuardian, DisplayName = "Страж данжа",
                RequiresHardmode = false, CountdownSeconds = 30,
                PrepareWorld = null
            },
            new BossDefinition {
                NpcType = NPCID.AncientCultistSquidhead, DisplayName = "Древний культист (сквид)",
                RequiresHardmode = false, CountdownSeconds = 30,
                PrepareWorld = null
            },
            new BossDefinition {
                NpcType = NPCID.DD2Betsy, DisplayName = "Бетси",
                RequiresHardmode = true, CountdownSeconds = 30,
                PrepareWorld = null
            },
            new BossDefinition {
                NpcType = NPCID.CultistDragonHead, DisplayName = "Культистский дракон",
                RequiresHardmode = true, CountdownSeconds = 30,
                PrepareWorld = null
            },
            new BossDefinition {
                // Императрица днём
                NpcType = NPCID.HallowBoss, DisplayName = "Императрица Света (днём)",
                RequiresHardmode = true, RequiresDay = true, CountdownSeconds = 30,
                PrepareWorld = null
            },
        };

        // ================================
        // ====== MOB QUEUE (tiers 1-4) ===
        // ================================
        private class QueueItem
        {
            public string GiverName;
            public int GiftPrice;
        }

        private static readonly Queue<QueueItem> SpawnQueue = new();
        private static int ActiveSpawned = 0;
        private const int MaxActive = 10;

        // ================================
        // ====== PUBLIC API ==============
        // ================================
        public static void SpawnGiftEnemy(string giverName, int giftCount, int giftPrice)
        {
            // 1 подарок = 1 элемент очереди. giftCount обрабатывай снаружи циклом.
            SpawnQueue.Enqueue(new QueueItem
            {
                GiverName = giverName,
                GiftPrice = giftPrice
            });

            TrySpawnNext();
        }

        // ================================
        // ====== QUEUE PROCESS ===========
        // ================================
        private static void TrySpawnNext()
        {
            if (Main.netMode == NetmodeID.MultiplayerClient)
                return;

            Main.QueueMainThreadAction(async () =>
            {
                while (SpawnQueue.Count > 0)
                {
                    if (ActiveSpawned >= MaxActive)
                        return;

                    Player player = Main.LocalPlayer;
                    if (player == null || !player.active || player.dead)
                    {
                        // ждем игрока 1 секунду, потом пробуем снова
                        await Task.Delay(1000);
                        continue;
                    }

                    var item = SpawnQueue.Peek();

                    int coins = item.GiftPrice;

                    if (IsTier5(coins) || IsTier6(coins))
                    {
                        var boss = PickBossByCoinsPublic(coins);
                        if (boss != null)
                        {
                            BossQueueManager.EnqueueBoss(boss, item.GiverName, coins);
                        }
                        SpawnQueue.Dequeue();
                        continue;
                    }

                    int npcType = PickEnemyByCoins(coins);
                    int spawnX = (int)player.Center.X + Main.rand.Next(-400, 400);
                    int spawnY = (int)player.Center.Y - 300;

                    int npcId = NPC.NewNPC(player.GetSource_FromThis(), spawnX, spawnY, npcType);

                    if (npcId < 0)
                        continue;

                    GiftNpcTracker.Register(npcId);

                    if (npcId < 0)
                        continue;

                    NPC npc = Main.npc[npcId];
                    npc.target = player.whoAmI;

                    var gift = npc.GetGlobalNPC<GiftFlyingFishGlobal>();
                    gift.giverName = item.GiverName;
                    gift.goldInside = coins;

                    float scale = 1f + MathHelper.Clamp(coins * 0.04f, 0f, 5f);
                    npc.lifeMax = (int)(npc.lifeMax * scale);
                    npc.life = npc.lifeMax;
                    npc.damage = (int)(npc.damage * scale);
                    npc.defense = (int)(npc.defense * scale);

                    npc.netUpdate = true;

                    int queuedLeft = SpawnQueue.Count; // сколько ещё ждёт

                    Main.NewText(
                        $"{item.GiverName} призвал {Lang.GetNPCNameValue(npcType)} ({coins} монет) [в очереди: {queuedLeft}]",
                        Color.OrangeRed
                    );

                    ActiveSpawned++;

                    GiftFlyingFishGlobal.OnGiftEnemyKilled -= HandleEnemyKilled;
                    GiftFlyingFishGlobal.OnGiftEnemyKilled += HandleEnemyKilled;

                    SpawnQueue.Dequeue();
                }
            });
        }

        private static void HandleEnemyKilled()
        {
            ActiveSpawned = Math.Max(ActiveSpawned - 1, 0);
            TrySpawnNext();
        }

        // ================================
        // ====== TIER LOGIC ==============
        // ================================
        private static bool IsTier5(int coins) => coins > 30 && coins <= 99;
        private static bool IsTier6(int coins) => coins >= 100;

        private static int PickEnemyByCoins(int coins)
        {
            if (coins <= 5) return Pick(Tier1Enemies);
            if (coins <= 10) return Pick(Tier2Enemies);
            if (coins <= 20) return Pick(Tier3Enemies);
            return Pick(Tier4Enemies);
        }

        public static BossDefinition PickBossByCoinsPublic(int coins)
        {
            bool hard = Main.hardMode;

            if (IsTier6(coins))
            {
                // Tier6: фильтруем по hardmode и доп-условиям
                var pool6 = Tier6Bosses.Where(b =>
                    (!b.RequiresHardmode || hard)
                ).ToArray();

                return pool6.Length == 0 ? null : pool6[Main.rand.Next(pool6.Length)];
            }

            if (IsTier5(coins))
            {
                var basePool = hard ? Tier5Bosses_Hardmode : Tier5Bosses_PreHard;

                var pool5 = basePool.Where(b =>
                    (!b.RequiresHardmode || hard)
                ).ToArray();

                return pool5.Length == 0 ? null : pool5[Main.rand.Next(pool5.Length)];
            }

            return null;
        }

        private static int Pick(int[] pool) => pool[Main.rand.Next(pool.Length)];
    }
}

public class WorldExitCleanupSystem : ModSystem
{
    public override void OnWorldUnload()
    {
        BossQueueManager.ResetAll();
        GiftEnemySpawner.ResetAll();
        GiftNpcTracker.ResetAll();
    }
}