using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using Terraria;
using Terraria.ModLoader;

namespace ImapoTikTokIntegrationMod
{
    public class TestNpcNameGlobal : GlobalNPC
    {
        public override bool InstancePerEntity => true;

        public string customText;

        public override void PostDraw(
            NPC npc,
            SpriteBatch spriteBatch,
            Vector2 screenPos,
            Color drawColor)
        {
            if (string.IsNullOrEmpty(customText))
                return;

            Vector2 pos = npc.Top - screenPos + new Vector2(0, -20);

            spriteBatch.DrawString(
                Terraria.GameContent.FontAssets.MouseText.Value,
                customText,
                pos,
                Color.Gold,
                0f,
                Vector2.Zero,
                0.9f,
                SpriteEffects.None,
                0f
            );
        }
    }
}
