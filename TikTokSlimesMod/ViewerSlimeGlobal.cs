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
    public string commentText;
    public bool isSeagull;

    public override void PostDraw(NPC npc, SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        if (string.IsNullOrEmpty(viewerName))
            return;

        string text = viewerName;

        if (isSeagull && !string.IsNullOrEmpty(commentText))
            text = $"{viewerName}: {commentText}";

        if (text.Length > 40)
            text = text.Substring(0, 37) + "...";

        Vector2 position = npc.Top - new Vector2(0, 20) - screenPos;

        float scale = 0.8f; // текущий
        if (text.Length > 30)
            scale = 0.6f;

        ChatManager.DrawColorCodedStringWithShadow(
            spriteBatch,
            FontAssets.MouseText.Value,
            text,
            position,
            Color.White,
            0f,
            Vector2.Zero,
            new Vector2(scale)
        );
    }
}