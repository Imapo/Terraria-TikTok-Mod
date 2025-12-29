using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using System;
using System.Collections.Generic;
using System.Text;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

public static class Fonts
{
    public static DynamicSpriteFont DefaultFont => FontAssets.MouseText.Value;
}

public class VisualLifetimeGlobalNPC : GlobalNPC
{
    public override bool InstancePerEntity => true;

    public bool isTimed = false;
    public int lifetime = 0;
    private const int FadeDuration = 300; // 7 секунд
    public bool transformedToVisual = false;

    public void SetLifetime(int seconds)
    {
        isTimed = true;
        lifetime = seconds * 60;
    }

    public override void AI(NPC npc)
    {
        if (!isTimed) return;

        int currentLifetime = lifetime;
        lifetime--;

        // fade до исчезновения
        if (currentLifetime <= FadeDuration)
        {
            float progress = 1f - (currentLifetime / (float)FadeDuration);
            npc.alpha = (int)(progress * 255f);
        }

        // превращение в стрекозу
        if (lifetime == 0 && !transformedToVisual)
        {
            transformedToVisual = true;

            // сохраняем ник до превращения
            string oldViewerName = "";
            if (npc.TryGetGlobalNPC<ViewerSlimesGlobal>(out var slimeGlobal))
                oldViewerName = slimeGlobal.viewerName;

            // 🔥 превращаем в стрекозу
            npc.Transform(NPCID.GreenDragonfly);

            // делаем стрекозу "призраком"
            npc.friendly = true;
            npc.damage = 0;
            npc.dontTakeDamage = true;
            npc.noTileCollide = true;
            npc.noGravity = true;
            npc.velocity = Vector2.Zero;

            npc.alpha = 0;
            lifetime = FadeDuration;

            // присваиваем ник стрекозе
            if (npc.TryGetGlobalNPC<ViewerButterflyGlobal>(out var butterflyGlobal))
            {
                butterflyGlobal.isViewerButterfly = true;
                butterflyGlobal.viewerName = oldViewerName;
            }
        }

        if (lifetime < -10)
            npc.active = false;
    }
}


public class ViewerSlimesGlobal : GlobalNPC
{
    public override bool InstancePerEntity => true;

    public string viewerName = "";
    public bool isViewer = false;

    public bool isVeteran = false;
    public bool isModerator = false;
    public bool isGifter = false;
    public bool isRainbow = false;

    public int attackCooldown = 0;
    public int timeLeft = 0;

    // ====== НАСТРОЙКИ ======
    private const float FollowRange = 500f;
    private const float TeleportRange = 1200f;
    private const float TargetRange = 600f;
    private const float MoveSpeed = 6f;
    private const int AttackDelay = 30;
    private const int Damage = 20;

    // Только слизни
    private bool IsSlime(NPC npc) =>
        npc.type == NPCID.BlueSlime ||
        npc.type == NPCID.RedSlime ||
        npc.type == NPCID.LavaSlime ||
        npc.type == NPCID.GoldenSlime ||
        npc.type == NPCID.RainbowSlime;

    public override void AI(NPC npc)
    {
        if (!IsSlime(npc) || !isViewer)
            return;

        // ====== СИНХРОНИЗАЦИЯ ТАЙМЕРА ======
        if (npc.TryGetGlobalNPC(out VisualLifetimeGlobalNPC lifetime) && lifetime.isTimed)
            timeLeft = lifetime.lifetime;
        else
            timeLeft = 0;

        if (attackCooldown > 0)
            attackCooldown--;

        Player owner = FindNearestPlayer(npc);
        if (owner == null)
            return;

        // NPC ведёт себя как союзный миньон
        npc.friendly = true;       // союзник
        npc.damage = 0;            // никакого контактного урона
        npc.chaseable = false;

        NPC target = FindTarget(npc);

        HandleMovement(npc, owner, target);
        HandleAttack(npc, target);
    }

    // ====== ПОИСК ИГРОКА ======
    private Player FindNearestPlayer(NPC npc)
    {
        Player best = null;
        float bestDist = float.MaxValue;

        foreach (var p in Main.player)
        {
            if (!p.active || p.dead)
                continue;

            float d = Vector2.Distance(npc.Center, p.Center);
            if (d < bestDist)
            {
                bestDist = d;
                best = p;
            }
        }

        return best;
    }

    // ====== ПОИСК ВРАГА ======
    private NPC FindTarget(NPC npc)
    {
        NPC best = null;
        float bestDist = TargetRange;

        foreach (var n in Main.npc)
        {
            if (!n.active || n.friendly || n.lifeMax <= 5)
                continue;

            float d = Vector2.Distance(n.Center, npc.Center);
            if (d < bestDist)
            {
                bestDist = d;
                best = n;
            }
        }

        return best;
    }

    private const float JumpHeight = -7f; // сила прыжка
    private int jumpCooldown = 0; // таймер задержки прыжка

    private void HandleMovement(NPC npc, Player owner, NPC target)
    {
        Vector2 destination = target != null ? target.Center : owner.Center;
        float distance = Vector2.Distance(npc.Center, destination);

        // Телепорт как у миньонов
        if (distance > TeleportRange)
        {
            npc.Center = owner.Center + new Vector2(Main.rand.Next(-60, 60), -40);
            npc.velocity = Vector2.Zero;
            npc.netUpdate = true;
            return;
        }

        // Следуем по горизонтали
        if (distance > 20f)
        {
            float dirX = destination.X > npc.Center.X ? 1f : -1f;
            float speedX = MoveSpeed * dirX;
            npc.velocity.X = MathHelper.Lerp(npc.velocity.X, speedX, 0.08f);
            npc.direction = npc.velocity.X > 0 ? 1 : -1;
        }

        // Обработка естественного прыжка
        if (jumpCooldown > 0)
            jumpCooldown--;

        bool onGround = npc.velocity.Y == 0f && npc.collideY; // на земле
        if (onGround && jumpCooldown == 0)
        {
            // Прыжок при препятствии или когда нужно догнать игрока/врага
            if ((target != null && Math.Abs(npc.Center.X - target.Center.X) > 10f) ||
                (Math.Abs(npc.Center.X - owner.Center.X) > 80f))
            {
                npc.velocity.Y = JumpHeight;
                jumpCooldown = 20 + Main.rand.Next(10); // случайная пауза между прыжками
            }
        }
    }


    // ====== АТАКА ======
    private void HandleAttack(NPC npc, NPC target)
    {
        if (target == null || attackCooldown > 0)
            return;

        if (npc.Hitbox.Intersects(target.Hitbox))
        {
            target.StrikeNPC(
                new NPC.HitInfo
                {
                    Damage = Damage,
                    Knockback = 1f,
                    HitDirection = npc.direction,
                    Crit = false
                },
                noPlayerInteraction: true
            );

            attackCooldown = AttackDelay;
        }
    }

    // ====== ОТРИСОВКА ИМЕНИ ======
    public override void PostDraw(NPC npc, SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        if (!IsSlime(npc) || !isViewer || string.IsNullOrEmpty(viewerName))
            return;

        string name = viewerName;

        if (timeLeft > 0)
        {
            int seconds = (timeLeft + 59) / 60;
            name += $" [{seconds}]";
        }

        Vector2 pos = npc.Top - new Vector2(0, 20) - screenPos;
        float scale = name.Length > 30 ? 0.6f : 0.8f;

        Color color =
            isRainbow ? Main.hslToRgb((Main.GameUpdateCount % 360) / 360f, 1f, 0.7f) :
            isModerator ? Color.Red :
            isGifter ? Color.Gold :
            isVeteran ? Color.Orange :
            Color.White;

        // Обводка
        foreach (var o in new[] { new Vector2(-1, 0), new Vector2(1, 0), new Vector2(0, -1), new Vector2(0, 1) })
            spriteBatch.DrawString(Fonts.DefaultFont, name, pos + o, Color.Black, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);

        spriteBatch.DrawString(Fonts.DefaultFont, name, pos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }
}


public class ViewerButterflyGlobal : GlobalNPC
{
    public override bool InstancePerEntity => true;

    public int lifetime = 0;
    public bool isViewerButterfly = false;
    public string viewerName = "";
    public string rawId = "";

    public static List<TikFinityClient.SubscriberDatabaseEntry> SubscriberDatabase = new List<TikFinityClient.SubscriberDatabaseEntry>();

    public override void AI(NPC npc)
    {
        if (!isViewerButterfly) return;

        lifetime++;

        // Начинаем fade после 540 тиков (9 секунд)
        if (lifetime > 540)
            npc.alpha = (int)MathHelper.Clamp((lifetime - 540) * 4.25f, 0, 255);

        if (lifetime > 600)
            npc.active = false;
    }

    public override void PostDraw(NPC npc, SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        if (!isViewerButterfly || string.IsNullOrEmpty(viewerName)) return;

        // Ник следует за npc.Top и учитывает прозрачность
        Vector2 position = npc.Top - new Vector2(0, 20) - screenPos;

        Color nameColor;

        // Цвет по типу зрителя
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

        // Прозрачность текста следует за npc.alpha
        float alphaMultiplier = 1f - npc.alpha / 255f;
        nameColor *= alphaMultiplier;

        // Обводка
        Vector2[] offsets = new Vector2[]
        {
            new Vector2(-1, 0),
            new Vector2(1, 0),
            new Vector2(0, -1),
            new Vector2(0, 1)
        };

        foreach (var o in offsets)
        {
            spriteBatch.DrawString(
                Fonts.DefaultFont,
                viewerName,
                position + o,
                Color.Black * alphaMultiplier,
                0f,
                Vector2.Zero,
                0.8f,
                SpriteEffects.None,
                0f
            );
        }

        spriteBatch.DrawString(
            Fonts.DefaultFont,
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
            Fonts.DefaultFont,
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

    // ====== ДАННЫЕ ПОДАРКА ======
    public string giverName = "";
    public int goldInside = 0;

    // ====== ОТРИСОВКА НИКА ======
    public override void PostDraw(NPC npc, SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        if (string.IsNullOrEmpty(giverName))
            return;

        Vector2 position = npc.Top - new Vector2(0, 20) - screenPos;

        spriteBatch.DrawString(
            Fonts.DefaultFont,
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

    // ====== НАГРАДА ПРИ СМЕРТИ ======
    public static event System.Action OnGiftEnemyKilled;
    public override void OnKill(NPC npc)
    {
        if (goldInside <= 0)
            return;

        for (int i = 0; i < goldInside; i++)
        {
            Item.NewItem(
                npc.GetSource_Loot(),
                npc.getRect(),
                ItemID.GoldCoin
            );
        }

        // уведомляем очередь
        OnGiftEnemyKilled?.Invoke();
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
            spriteBatch.DrawString(Fonts.DefaultFont, text, pos + o, outlineColor, 0f, Vector2.Zero, 0.6f, SpriteEffects.None, 0f);

        spriteBatch.DrawString(Fonts.DefaultFont, text, pos, mainColor, 0f, Vector2.Zero, 0.6f, SpriteEffects.None, 0f);
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