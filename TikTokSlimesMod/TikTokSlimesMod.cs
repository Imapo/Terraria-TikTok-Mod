using Terraria.ModLoader;

namespace TikTokSlimesMod
{
    public class TikTokSlimesMod : Mod
    {
        public override void Load()
        {
            Logger.Info("TikTokSlimesMod загружен");
            TikFont.Load(this);
        }

        public override void Unload()
        {
            Logger.Info("TikTokSlimesMod выгружен");
        }
    }
}
