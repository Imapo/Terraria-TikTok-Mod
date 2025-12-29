using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Microsoft.Xna.Framework;

namespace ImapoTikTokIntegrationMod
{
    public static class GiftEnemySpawner
    {
        // ====== ПУЛЫ ВРАГОВ ======

        private static readonly int[] ForestEnemies =
        {
            NPCID.Zombie,
            NPCID.DemonEye,
            NPCID.GreenSlime
        };

        private static readonly int[] DesertEnemies =
        {
            NPCID.Vulture,
            NPCID.Antlion
        };

        private static readonly int[] SnowEnemies =
        {
            NPCID.IceSlime,
            NPCID.UndeadViking
        };

        private static readonly int[] JungleEnemies =
        {
            NPCID.Hornet,
            NPCID.JungleSlime
        };

        private static readonly int[] CorruptionEnemies =
        {
            NPCID.EaterofSouls
        };

        private static readonly int[] CrimsonEnemies =
        {
            NPCID.Crimera,
            NPCID.FaceMonster
        };

        private static readonly int[] UndergroundEnemies =
        {
            NPCID.CaveBat,
            NPCID.Skeleton
        };

        private static readonly int[] SkyEnemies =
        {
            NPCID.Harpy
        };

        // 👹 fallback для нестандартных биомов (Hallow, Ocean, ModBiome и т.д.)
        private static readonly int[] FallbackEnemies =
        {
            NPCID.AngryBones,
            NPCID.Wraith,
            NPCID.ChaosElemental
        };

        // ====== ПУБЛИЧНЫЙ МЕТОД ======

        public static void SpawnGiftEnemy(string giverName, int giftPower)
        {
            if (Main.netMode == NetmodeID.MultiplayerClient)
                return;

            Main.QueueMainThreadAction(() =>
            {
                Player player = Main.LocalPlayer;
                if (player == null || !player.active)
                    return;

                int enemyType = GetEnemyForBiome(player);

                int spawnX = (int)player.Center.X + Main.rand.Next(-400, 400);
                int spawnY = (int)player.Center.Y - 300;

                int npcId = NPC.NewNPC(
                    player.GetSource_FromThis(),
                    spawnX,
                    spawnY,
                    enemyType
                );

                if (npcId < 0)
                    return;

                NPC npc = Main.npc[npcId];
                npc.target = player.whoAmI;

                // ====== ПРИВЯЗКА ПОДАРКА ======
                var gift = npc.GetGlobalNPC<GiftFlyingFishGlobal>();
                gift.giverName = giverName;
                gift.goldInside = giftPower;

                // ====== УСИЛЕНИЕ ======
                float scale = 1f + MathHelper.Clamp(giftPower * 0.05f, 0f, 3f);

                npc.lifeMax = (int)(npc.lifeMax * scale);
                npc.life = npc.lifeMax;
                npc.damage = (int)(npc.damage * scale);
                npc.defense = (int)(npc.defense * scale);

                npc.netUpdate = true;

                Main.NewText(
                    $"🎁 {giverName} призвал {Lang.GetNPCNameValue(enemyType)}!",
                    Color.OrangeRed
                );
            });
        }

        // ====== ОПРЕДЕЛЕНИЕ ВРАГА ПО БИОМУ ======

        private static int GetEnemyForBiome(Player player)
        {
            if (player.ZoneSkyHeight)
                return Pick(SkyEnemies);

            if (player.ZoneJungle)
                return Pick(JungleEnemies);

            if (player.ZoneSnow)
                return Pick(SnowEnemies);

            if (player.ZoneDesert)
                return Pick(DesertEnemies);

            if (player.ZoneCorrupt)
                return Pick(CorruptionEnemies);

            if (player.ZoneCrimson)
                return Pick(CrimsonEnemies);

            if (player.ZoneRockLayerHeight || player.ZoneDirtLayerHeight)
                return Pick(UndergroundEnemies);

            if (player.ZoneForest)
                return Pick(ForestEnemies);

            // 👹 нестандартные / модовые / Hallow / Ocean
            return Pick(FallbackEnemies);
        }

        private static int Pick(int[] pool)
        {
            return pool[Main.rand.Next(pool.Length)];
        }
    }
}
