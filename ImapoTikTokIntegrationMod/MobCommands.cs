using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.GameContent;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ImapoTikTokIntegrationMod
{
    // =========================================================
    // /mob <NPCName>
    // =========================================================
    public class MobCommand : ModCommand
    {
        public override string Command => "mob";
        public override CommandType Type => CommandType.Chat;
        public override string Description => "Spawn NPC by internal name";

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            if (args.Length == 0)
            {
                Main.NewText("Использование: /mob <NPCName>", Color.Gray);
                return;
            }

            int npcType = NpcUtils.FindNpcByName(args[0]);
            if (npcType == -1)
            {
                Main.NewText($"NPC '{args[0]}' не найден", Color.Red);
                return;
            }

            Player player = caller.Player;

            int npcID = NPC.NewNPC(
                player.GetSource_FromThis(),
                (int)player.Center.X,
                (int)player.Center.Y,
                npcType
            );

            if (npcID >= 0)
            {
                var g = Main.npc[npcID].GetGlobalNPC<TestNpcNameGlobal>();
                g.customText = "TESTNPC";
                Main.npc[npcID].netUpdate = true;
            }
        }
    }

    // =========================================================
    // /moblist <filter>
    // =========================================================
    public class MobListCommand : ModCommand
    {
        public override string Command => "moblist";
        public override CommandType Type => CommandType.Chat;
        public override string Description => "List NPCs by name filter";

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            string filter = args.Length > 0 ? args[0] : "";

            var list = NpcUtils.GetAllNpcNames()
                .Where(n => n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(30)
                .ToList();

            if (list.Count == 0)
            {
                Main.NewText("Ничего не найдено", Color.Gray);
                return;
            }

            Main.NewText($"Найдено NPC ({list.Count}):", Color.Gold);
            foreach (var n in list)
                Main.NewText(n, Color.LightGray);
        }
    }

    // =========================================================
    // /mobrandom
    // =========================================================
    public class MobRandomCommand : ModCommand
    {
        public override string Command => "mobrandom";
        public override CommandType Type => CommandType.Chat;
        public override string Description => "Spawn random hostile NPC";

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            Player player = caller.Player;

            int npcType = NpcUtils.GetRandomHostileNpc(player);
            if (npcType == -1)
            {
                Main.NewText("Не удалось подобрать NPC", Color.Red);
                return;
            }

            NPC.NewNPC(
                player.GetSource_FromThis(),
                (int)player.Center.X,
                (int)player.Center.Y,
                npcType
            );
        }
    }

    // =========================================================
    // ОБЩИЕ УТИЛИТЫ
    // =========================================================
    internal static class NpcUtils
    {
        public static int FindNpcByName(string name)
        {
            foreach (var pair in ContentSamples.NpcsByNetId)
            {
                string internalName = NPCID.Search.GetName(pair.Key);
                if (string.Equals(internalName, name, StringComparison.OrdinalIgnoreCase))
                    return pair.Key;
            }
            return -1;
        }

        public static List<string> GetAllNpcNames()
        {
            return ContentSamples.NpcsByNetId
                .Select(p => NPCID.Search.GetName(p.Key))
                .OrderBy(n => n)
                .ToList();
        }

        public static List<string> Autocomplete(string current)
        {
            return GetAllNpcNames()
                .Where(n => n.StartsWith(current, StringComparison.OrdinalIgnoreCase))
                .Take(20)
                .ToList();
        }

        public static int GetRandomHostileNpc(Player player)
        {
            var pool = new List<int>();

            foreach (var pair in ContentSamples.NpcsByNetId)
            {
                NPC npc = pair.Value;

                if (!npc.friendly && npc.damage > 0 && !npc.boss)
                    pool.Add(pair.Key);
            }

            if (pool.Count == 0)
                return -1;

            return pool[Main.rand.Next(pool.Count)];
        }
    }
}
