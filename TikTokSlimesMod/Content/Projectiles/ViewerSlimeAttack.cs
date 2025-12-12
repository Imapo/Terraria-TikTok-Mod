using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;

namespace TikTokSlimesMod.Content.Projectiles
{
    public class ViewerSlimeAttack : GlobalProjectile
    {
        // Ник зрителя
        public string viewerName;

        // Нужно возвращать true, чтобы поля были уникальны для каждого экземпляра миньона
        public override bool InstancePerEntity => true;

        // Рисуем ник над миньоном
        public override bool PreDraw(Projectile projectile, ref Color lightColor)
        {
            if (!string.IsNullOrEmpty(viewerName))
            {
                Vector2 drawPos = projectile.Center - Main.screenPosition + new Vector2(0, -projectile.height);
                Utils.DrawBorderString(Main.spriteBatch, viewerName, drawPos, Color.LightGreen);
            }
            return true; // Рисуем стандартный спрайт миньона
        }
    }
}
