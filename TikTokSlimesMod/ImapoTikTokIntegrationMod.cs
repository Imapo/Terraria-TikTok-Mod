using Terraria.ModLoader;

namespace ImapoTikTokIntegrationMod
{
    public class ImapoTikTokIntegrationMod : Mod
    {
        public override void Load()
        {
            Logger.Info("ImapoTikTokIntegrationMod загружен");
            TikFont.Load(this);
        }

        public override void Unload()
        {
            Logger.Info("ImapoTikTokIntegrationMod выгружен");
        }
    }
}
