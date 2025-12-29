using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using Terraria;
using Terraria.Graphics;
using Terraria.ID;
using Terraria.ModLoader;

namespace ImapoTikTokIntegrationMod
{
    public static class GiftEnemySpawner
    {
        // ================================
        // ====== ПУЛЫ ПО СЛОЖНОСТИ ======
        // ================================
        private static readonly int[] Tier1Enemies = { 
            NPCID.EaterofSouls,
            NPCID.MotherSlime,
            NPCID.MeteorHead,
            NPCID.FireImp,
            NPCID.AngryBones,
            NPCID.CaveBat,
            NPCID.JungleBat,
            NPCID.CorruptGoldfish,
            NPCID.Vulture,
            NPCID.Crab,
            NPCID.Antlion,
            NPCID.SpikeBall,
            NPCID.GoblinScout,
            NPCID.Pixie,
            NPCID.AnglerFish,
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
            NPCID.HornetFatty,
            NPCID.HornetHoney,
            NPCID.HornetLeafy,
            NPCID.HornetSpikey,
            NPCID.HornetStingy,
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
            NPCID.SandSlime
        };
        private static readonly int[] Tier2Enemies = { 
            NPCID.CursedSkull,
            NPCID.Tim,
            NPCID.CorruptBunny,
            NPCID.Harpy,
            NPCID.GiantBat,
            NPCID.Corruptor,
            NPCID.Slimer,
            NPCID.Gastropod,
            NPCID.SnowmanGangsta,
            NPCID.MisterStabby,
            NPCID.Lavabat,
            NPCID.Wolf,
            NPCID.SwampThing,
            NPCID.IceElemental,
            NPCID.PigronCorruption,
            NPCID.PigronHallow,
            NPCID.PigronCrimson,
            NPCID.FaceMonster,
            NPCID.FloatyGross,
            NPCID.Crimslime,
            NPCID.SpikedIceSlime,
            NPCID.SnowFlinx,
            NPCID.SpikedJungleSlime,
            NPCID.HoppinJack,
            NPCID.Ghost,
            NPCID.MartianTurret,
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
            NPCID.SporeBat
        };
        private static readonly int[] Tier3Enemies = { 
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
            NPCID.QueenSlimeMinionPurple
        };
        private static readonly int[] Tier4Enemies = { 
            NPCID.DungeonGuardian,
            NPCID.WyvernHead,
            NPCID.RedDevil,
            NPCID.LihzahrdCrawler,
            NPCID.QueenBee,
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
            NPCID.PirateGhost
        };
        private static readonly int[] Tier5Enemies = { 
            NPCID.CultistBoss,
            NPCID.CultistDragonHead,
            NPCID.BigMimicCorruption,
            NPCID.BigMimicCrimson,
            NPCID.BigMimicHallow,
            NPCID.BigMimicJungle,
            NPCID.AncientCultistSquidhead,
            NPCID.SandElemental,
            NPCID.DD2Betsy,
            NPCID.DD2DarkMageT3,
            NPCID.DD2OgreT2,
            NPCID.DD2OgreT3,
            NPCID.DD2LightningBugT3,
            NPCID.HallowBoss,
            NPCID.QueenSlimeBoss
        };

        // ================================
        // ====== ОЧЕРЕДЬ =================
        // ================================
        private class QueueItem
        {
            public string GiverName;
            public int GiftPrice;
        }

        private static readonly Queue<QueueItem> SpawnQueue = new Queue<QueueItem>();
        private static int ActiveSpawned = 0;
        private const int MaxActive = 10;

        // ================================
        // ====== ПУБЛИЧНЫЙ МЕТОД ========
        // ================================
        public static void SpawnGiftEnemy(string giverName, int giftCount, int giftPrice)
        {
            // ⚠️ НЕ ДЕЛАЕМ ВЛОЖЕННЫЙ ЦИКЛ
            SpawnQueue.Enqueue(new QueueItem
            {
                GiverName = giverName,
                GiftPrice = giftPrice
            });

            TrySpawnNext();
        }

        // ================================
        // ====== СПАВН ПО ОЧЕРЕДИ ========
        // ================================
        private static void TrySpawnNext()
        {
            if (Main.netMode == NetmodeID.MultiplayerClient)
                return;

            Main.QueueMainThreadAction(() =>
            {
                while (ActiveSpawned < MaxActive && SpawnQueue.Count > 0)
                {
                    var item = SpawnQueue.Dequeue();

                    int totalCoins = item.GiftPrice; // можно умножать на количество, если нужно

                    Player player = Main.LocalPlayer;
                    if (player == null || !player.active)
                        continue;

                    int npcType = PickEnemyByCoins(totalCoins);

                    int spawnX = (int)player.Center.X + Main.rand.Next(-400, 400);
                    int spawnY = (int)player.Center.Y - 300;

                    int npcId = NPC.NewNPC(
                        player.GetSource_FromThis(),
                        spawnX,
                        spawnY,
                        npcType
                    );

                    if (npcId < 0)
                        continue;

                    NPC npc = Main.npc[npcId];
                    npc.target = player.whoAmI;

                    // ====== ДАННЫЕ ПОДАРКА ======
                    var gift = npc.GetGlobalNPC<GiftFlyingFishGlobal>();
                    gift.giverName = item.GiverName;
                    gift.goldInside = totalCoins;

                    // ====== УСИЛЕНИЕ ======
                    float scale = 1f + MathHelper.Clamp(totalCoins * 0.04f, 0f, 5f);
                    npc.lifeMax = (int)(npc.lifeMax * scale);
                    npc.life = npc.lifeMax;
                    npc.damage = (int)(npc.damage * scale);
                    npc.defense = (int)(npc.defense * scale);

                    npc.netUpdate = true;

                    Main.NewText(
                        $"🎁 {item.GiverName} призвал {Lang.GetNPCNameValue(npcType)} (💰{totalCoins})",
                        Color.OrangeRed
                    );

                    ActiveSpawned++;

                    // подписка на уничтожение NPC
                    GiftFlyingFishGlobal.OnGiftEnemyKilled -= HandleEnemyKilled;
                    GiftFlyingFishGlobal.OnGiftEnemyKilled += HandleEnemyKilled;
                }
            });
        }

        // ================================
        // ====== ОБРАБОТКА УБИЙСТВА =======
        // ================================
        private static void HandleEnemyKilled()
        {
            ActiveSpawned = Math.Max(ActiveSpawned - 1, 0);
            TrySpawnNext();
        }

        // ================================
        // ====== ВЫБОР ПО МОНЕТАМ ========
        // ================================
        private static int PickEnemyByCoins(int coins)
        {
            if (coins <= 1) return Pick(Tier1Enemies);
            if (coins <= 5) return Pick(Tier2Enemies);
            if (coins <= 10) return Pick(Tier3Enemies);
            if (coins <= 30) return Pick(Tier4Enemies);
            return Pick(Tier5Enemies);
        }

        private static int Pick(int[] pool)
        {
            return pool[Main.rand.Next(pool.Length)];
        }
    }
}
