using System;
using Terraria;

namespace ImapoTikTokIntegrationMod
{
    public static class TikTestFactory
    {
        private static readonly string[] Names =
        {
            "TestUser",
            "Viewer123",
            "CoolGuy",
            "GiftMaster",
            "ModeratorOne"
        };

        public static string RandomName()
            => Names[Main.rand.Next(Names.Length)];

        public static string RandomId()
            => Guid.NewGuid().ToString("N")[..10];
    }
}