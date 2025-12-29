using System.Text.Json;
using Terraria.ModLoader;

namespace ImapoTikTokIntegrationMod
{
    public class ImapoTikTokIntegrationMod : Mod
    {
        public override void Load()
        {
            Logger.Info("ImapoTikTokIntegrationMod загружен");
        }

        public override void Unload()
        {
            Logger.Info("ImapoTikTokIntegrationMod выгружен");
        }
    }

    public class TikGiftEvent
    {
        public string UserName;
        public int RepeatCount;
    }

    public class TikShareEvent
    {
        public string UserId;
        public string UserName;
        public bool isModerator;
        public bool isFollowing;
    }

    public class TikSubscribeEvent
    {
        public string UserId;
        public string UserName;
        public bool isModerator;
        public bool isFollowing;
    }

    public class TikJoinEvent
    {
        public string UserId;
        public string UserName;
    }

    public class TikLikeEvent
    {
        public int count;
    }
}
