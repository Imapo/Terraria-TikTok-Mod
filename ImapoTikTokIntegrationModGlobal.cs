using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using System.Text;
using System.Collections.Generic;

public static class TikFont
{
    public static DynamicSpriteFont Font;

    public static void Load(Mod mod)
    {
        // ✅ Никогда не загружаем шрифты на сервере
        if (Main.dedServ)
            return;

        try
        {
            // ✅ Строго через ModContent + имя мода
            Font = ModContent.Request<DynamicSpriteFont>(
                "Assets/Fonts/NotoColorEmoji",
                ReLogic.Content.AssetRequestMode.ImmediateLoad
            ).Value;

            if (Font == null)
                throw new System.Exception("Font == null after load");

            Main.NewText("✅ Unicode шрифт загружен", Color.LimeGreen);
        }
        catch (System.Exception e)
        {
            // ✅ Фолбэк на стандартный
            Font = FontAssets.MouseText.Value;

            // ❗ ВАЖНО: Main.NewText в Load иногда даёт NullReference
            if (Main.gameMenu == false)
                Main.NewText("⚠ Unicode шрифт не загружен, использован стандартный", Color.OrangeRed);

            mod.Logger.Error("Ошибка загрузки Unicode.ttf:\n" + e);
        }
    }
}


public class ImapoTikTokIntegrationModGlobal : GlobalNPC
{
    public override bool InstancePerEntity => true;

    public string viewerName;
    public string commentText;
    public bool isSeagull;
    public bool isViewer = false;
    private bool fadingOut = false;
    private int fadeTicks = 0;
    private int fadeDuration = 0;
    public int attackCooldown = 0; // тикеры до следующей атаки
    public bool isVeteran;
    public bool isModerator;
    public bool isGifter;
    public bool isRainbow = false;

    public void StartFadeOut(int duration)
    {
        fadingOut = true;
        fadeTicks = 0;
        fadeDuration = duration;
    }

    public override void AI(NPC npc)
    {
        if (attackCooldown > 0) attackCooldown--;
        // 1️⃣ Сначала проверяем, нужно ли плавно исчезнуть
        if (fadingOut)
        {
            fadeTicks++;
            float progress = fadeTicks / (float)fadeDuration;
            npc.alpha = (int)(255 * progress);
            if (fadeTicks >= fadeDuration)
                npc.active = false;
            return; // если исчезаем — не делаем AI атаки
        }

        // 2️⃣ Только для зрительских слизней
        if (!isViewer) return;

        // --- ТЕЛЕПОРТ ---
        Player player = Main.player[npc.target];
        if (player == null || !player.active) return;

        float maxDistance = 900f;
        float distToPlayer = Vector2.Distance(npc.Center, player.Center);

        if (distToPlayer > maxDistance && !Collision.CanHitLine(npc.position, npc.width, npc.height, player.position, player.width, player.height))
        {
            // Телепортируем слегка рядом с игроком, чтобы не застрять
            Vector2 offset = new Vector2(
                Main.rand.NextFloat(-60f, 60f),
                Main.rand.NextFloat(-60f, 60f)
            );

            npc.position = player.Center + offset;
            npc.velocity = Vector2.Zero; // сбрасываем скорость
            npc.netUpdate = true;
        }
        // --- /ТЕЛЕПОРТ ---

        // Найти ближайшего врага
        NPC target = null;
        float nearestDistance = 500f;
        foreach (var n in Main.npc)
        {
            if (n.active && !n.friendly && n.lifeMax > 5)
            {
                float distance = Vector2.Distance(n.Center, npc.Center);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    target = n;
                }
            }
        }

        // Куда двигаться: к врагу или к игроку
        Vector2 targetPos = target != null ? target.Center : player.Center + new Vector2(0, -50);
        Vector2 move = targetPos - npc.Center;
        float speed = 4f;
        if (move.Length() > speed)
        {
            move.Normalize();
            move *= speed;
        }

        // Плавное движение
        //npc.velocity = (npc.velocity * 20f + move) / 21f;

        // Можно добавить урон при контакте
        if (target != null && npc.Hitbox.Intersects(target.Hitbox))
        {
            if (!target.friendly && target.lifeMax > 5) // атакуем только врагов
            {
                if (attackCooldown <= 0)
                {
                    int damage = 20;
                    var hitInfo = new NPC.HitInfo
                    {
                        Damage = damage,
                        HitDirection = npc.direction,
                        Crit = false,
                        Knockback = 1f
                    };
                    target.StrikeNPC(hitInfo, noPlayerInteraction: true);
                    attackCooldown = 60; // <- увеличено (60 тиков = 1 секунда)
                }
            }
        }
    }


    public override void PostDraw(NPC npc, SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        if (!isViewer || string.IsNullOrEmpty(viewerName)) return;

        string text = viewerName;
        if (isSeagull && !string.IsNullOrEmpty(commentText))
            text = $"{viewerName}: {commentText}";

        if (text.Length > 40)
            text = text.Substring(0, 37) + "...";

        Vector2 position = npc.Top - new Vector2(0, 20) - screenPos;
        float scale = text.Length > 30 ? 0.6f : 0.8f;

        Color textColor;
        if (isRainbow)
        {
            // 🌈 Переливающийся радужный цвет
            float hue = (Main.GameUpdateCount % 360) / 360f;
            textColor = Main.hslToRgb(hue, 1f, 0.7f); // яркий, но не ослепляющий
        }
        else if (isModerator)
            textColor = Color.Red;
        else if (isGifter)
            textColor = Color.Gold;
        else if (isVeteran)
            textColor = Color.Orange;
        else
            textColor = Color.White;

        if (fadingOut)
            textColor *= 1f - npc.alpha / 255f;
        Color outlineColor = Color.Black * npc.alpha;

        spriteBatch.DrawString(
            TikFont.Font,
            text,
            position,
            textColor,
            0f,
            Vector2.Zero,
            scale,
            SpriteEffects.None,
            0f
        );
    }
}

public class ViewerButterflyGlobal : GlobalNPC
{
    public override bool InstancePerEntity => true;

    public int lifetime = 0;
    public bool isViewerButterfly = false;
    public string viewerName = "";
    public string rawId = "";
    public static List<TikFinityClient.SubscriberHistoryEntry> SubscriberHistory = new List<TikFinityClient.SubscriberHistoryEntry>();

    public override void AI(NPC npc)
    {
        if (!isViewerButterfly) return;

        lifetime++;

        if (lifetime > 540)
            npc.alpha = (int)MathHelper.Clamp((lifetime - 540) * 4.25f, 0, 255);

        if (lifetime > 600)
            npc.active = false;
    }

    public override void PostDraw(NPC npc, SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        if (!isViewerButterfly || string.IsNullOrEmpty(viewerName)) return;

        Vector2 position = npc.Top - new Vector2(0, 20) - screenPos;

        Color nameColor;

        // Проверяем, является ли зритель подписчиком
        if (TikFinityClient.GiftGiverIds.Contains(rawId))
        {
            float hue = (Main.GameUpdateCount % 360) / 360f;
            nameColor = Main.hslToRgb(hue, 1f, 0.65f);
        }
        else if (TikFinityClient.SubscriberIds.Contains(rawId))
        {
            nameColor = Color.Gold;
        }
        else
        {
            nameColor = Color.White;
        }

        // Учитываем прозрачность NPC
        nameColor = nameColor * (1f - npc.alpha / 255f);

        spriteBatch.DrawString(
            TikFont.Font,
            viewerName,
            position,
            nameColor,
            0f,
            Vector2.Zero,
            0.8f,
            SpriteEffects.None,
            0f
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

        spriteBatch.DrawString(
            TikFont.Font,
            text,
            position,
            textColor,
            0f,
            Vector2.Zero,
            0.8f,
            SpriteEffects.None,
            0f
        );
    }
}

public class GiftFlyingFishGlobal : GlobalNPC
{
    public override bool InstancePerEntity => true;

    public string giverName = "";
    public int goldInside = 0;

    public override void PostDraw(NPC npc, SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        if (string.IsNullOrEmpty(giverName)) return;

        Vector2 position = npc.Top - new Vector2(0, 20) - screenPos;

        spriteBatch.DrawString(
            TikFont.Font,
            $"🎁 {giverName}",
            position,
            Color.Gold,
            0f,
            Vector2.Zero,
            0.8f,
            SpriteEffects.None,
            0f
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

public class LikeFloatingTextGlobal : GlobalNPC
{
    public override bool InstancePerEntity => true;

    public string viewerKey;
    public string viewerName;
    public int likeCount;
    public int life;

    private Vector2 comboTarget;      // куда летим при комбо
    private int comboTimer = 0;        // таймер комбо
    private const int ComboDuration = 30; // время “подлёта” в тиках
    public const int MaxLife = 90;

    public void TriggerCombo(Vector2 targetPosition)
    {
        comboTarget = targetPosition;
        comboTimer = ComboDuration;
    }

    public override void AI(NPC npc)
    {
        if (npc.type != NPCID.GreenDragonfly)
            return;

        if (string.IsNullOrEmpty(viewerKey))
            return;

        life++;

        // 🔥 Уничтожаем NPC сразу при превышении MaxLife
        if (life >= MaxLife)
        {
            npc.active = false;
            return; // важно: выйти, чтобы не обновлять alpha/движение
        }

        npc.alpha = (int)(life / (float)MaxLife * 200f);

        if (comboTimer > 0)
        {
            comboTimer--;
            npc.Center = Vector2.Lerp(npc.Center, comboTarget, 0.3f);
            npc.velocity = Vector2.Zero;
        }
        else
        {
            npc.velocity.Y = -0.15f;
        }
    }

    public override void PostDraw(NPC npc, SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        if (string.IsNullOrEmpty(viewerName)) return;

        Vector2 pos = npc.Center - screenPos + new Vector2(0, -20);
        float fade = 1f - npc.alpha / 255f;

        Color mainColor = Color.LimeGreen;
        Color outlineColor = Color.Black * fade;

        string text = $"{viewerName} +{likeCount}❤️";

        Vector2[] offsets = new Vector2[]
        {
            new Vector2(-1,0),
            new Vector2(1,0),
            new Vector2(0,-1),
            new Vector2(0,1)
        };

        foreach (var o in offsets)
            spriteBatch.DrawString(TikFont.Font, text, pos + o, outlineColor, 0f, Vector2.Zero, 0.6f, SpriteEffects.None, 0f);

        spriteBatch.DrawString(TikFont.Font, text, pos, mainColor, 0f, Vector2.Zero, 0.6f, SpriteEffects.None, 0f);
    }
}

public static class NickSanitizer
{
    public static string Sanitize(string input)
    {
        var sb = new StringBuilder();

        foreach (char c in input)
        {
            // ❌ УДАЛЯЕМ ТОЛЬКО ОПАСНЫЕ СИМВОЛЫ:
            // 1. Управляющие символы (кроме табуляции/перевода строки)
            // 2. Символы-заполнители (�)
            // 3. Нулевой символ
            bool isDangerous =
                (char.IsControl(c) && c != '\t' && c != '\n' && c != '\r') ||
                c == '\0' ||
                c == 0xFFFD; // �

            if (isDangerous)
            {
                // Можно заменить на пробел или просто пропустить
                sb.Append(' ');
            }
            else
            {
                // ✅ ВСЁ остальное оставляем как есть
                // (эмодзи, иероглифы, арабские буквы, сердечки и т.д.)
                sb.Append(c);
            }
        }

        string result = sb.ToString().Trim();

        // Обрезаем длину
        if (result.Length > 30)
            result = result.Substring(0, 27) + "...";

        return string.IsNullOrWhiteSpace(result) ? "Viewer" : result;
    }
}