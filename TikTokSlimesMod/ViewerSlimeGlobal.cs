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
    private bool fadingOut = false;
    private int fadeTicks = 0;
    private int fadeDuration = 0;

    public void StartFadeOut(int duration)
    {
        fadingOut = true;
        fadeTicks = 0;
        fadeDuration = duration;
    }

    public override void AI(NPC npc)
    {
        if (!fadingOut) return;

        fadeTicks++;
        float progress = fadeTicks / (float)fadeDuration;
        npc.alpha = (int)(255 * progress); // прозрачность 0..255
        if (fadeTicks >= fadeDuration)
            npc.active = false;
    }

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
        float scale = text.Length > 30 ? 0.6f : 0.8f;

        Color textColor = Color.White;
        if (fadingOut)
            textColor *= 1f - npc.alpha / 255f;

        ChatManager.DrawColorCodedStringWithShadow(
            spriteBatch,
            FontAssets.MouseText.Value,
            text,
            position,
            textColor,
            0f,
            Vector2.Zero,
            new Vector2(scale)
        );
    }
}

public class ViewerButterflyGlobal : GlobalNPC
{
    public override bool InstancePerEntity => true;

    public int lifetime = 0;
    public bool isViewerButterfly = false;
    public string viewerName = "";

    public override void AI(NPC npc)
    {
        if (!isViewerButterfly) return;

        lifetime++;

        // fade за последние 60 тиков
        if (lifetime > 540)
            npc.alpha = (int)MathHelper.Clamp((lifetime - 540) * 4.25f, 0, 255);

        if (lifetime > 600) // 10 секунд
            npc.active = false;
    }

    public override void PostDraw(NPC npc, SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        if (!isViewerButterfly || string.IsNullOrEmpty(viewerName)) return;

        Vector2 position = npc.Top - new Vector2(0, 20) - screenPos;
        Color textColor = Color.LightPink * (1f - npc.alpha / 255f);

        ChatManager.DrawColorCodedStringWithShadow(
            spriteBatch,
            FontAssets.MouseText.Value,
            viewerName,
            position,
            textColor,
            0f,
            Vector2.Zero,
            new Vector2(0.8f)
        );
    }
}

public class ViewerFireflyGlobal : GlobalNPC
{
    public override bool InstancePerEntity => true;

    public string viewerName = "";
    public string commentText = "";
    public bool isComment = false;
    public bool isViewer = false;

    private bool fadingOut = false;
    private int fadeTicks = 0;
    private int lifeTimer = 0;
    private const int LifeBeforeFade = 600; // 10 секунд
    private const int FadeTime = 120;       // 2 секунды fade

    public void StartFadeOut()
    {
        if (!isViewer) return;
        fadingOut = true;
        fadeTicks = 0;
    }

    public override void AI(NPC npc)
    {
        if (!isViewer) return;

        lifeTimer++;

        if (lifeTimer < LifeBeforeFade)
        {
            npc.alpha = 0;
            return;
        }

        fadingOut = true;
        fadeTicks++;
        float progress = fadeTicks / (float)FadeTime;
        npc.alpha = (int)(255 * progress);

        if (fadeTicks >= FadeTime)
            npc.active = false;
    }

    public override void PostDraw(NPC npc, SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        if (!isViewer || string.IsNullOrEmpty(viewerName)) return;

        Vector2 position = npc.Top - new Vector2(0, 20) - screenPos;

        string text = viewerName;
        if (isComment && !string.IsNullOrEmpty(commentText))
            text = $"{viewerName}: {commentText}";

        if (text.Length > 40)
            text = text.Substring(0, 37) + "...";

        Color textColor = Color.Yellow;
        if (fadingOut)
            textColor *= 1f - npc.alpha / 255f;

        ChatManager.DrawColorCodedStringWithShadow(
            spriteBatch,
            FontAssets.MouseText.Value,
            text,
            position,
            textColor,
            0f,
            Vector2.Zero,
            new Vector2(0.8f)
        );
    }
}

public class GiftZombieGlobal : GlobalNPC
{
    public override bool InstancePerEntity => true;

    public string giverName = "";
    public int goldInside = 0;

    public override void PostDraw(NPC npc, SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        if (string.IsNullOrEmpty(giverName)) return;

        Vector2 position = npc.Top - new Vector2(0, 20) - screenPos;

        ChatManager.DrawColorCodedStringWithShadow(
            spriteBatch,
            FontAssets.MouseText.Value,
            $"🎁 {giverName}",
            position,
            Color.Gold,
            0f,
            Vector2.Zero,
            new Vector2(0.8f)
        );
    }

    public override void OnKill(NPC npc)
    {
        if (goldInside <= 0) return;

        for (int i = 0; i < goldInside; i++)
        {
            Item.NewItem(
                npc.GetSource_Loot(),
                npc.getRect(),
                ItemID.GoldCoin
            );
        }
    }
}
