using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI.Chat;

public class ViewerSlimeGlobal : GlobalNPC
{
    public override bool InstancePerEntity => true;

    public string viewerName;

    public override void PostDraw(NPC npc, SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        if (npc.type == NPCID.BlueSlime && !string.IsNullOrEmpty(viewerName))
        {
            Vector2 namePosition = npc.Top - new Vector2(0, 18) - screenPos;

            ChatManager.DrawColorCodedStringWithShadow(
                spriteBatch,
                FontAssets.MouseText.Value,
                viewerName,
                namePosition,
                Color.White,
                0f,
                Vector2.Zero,
                new Vector2(0.8f)
            );
        }
    }
}
