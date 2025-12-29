using Terraria;
using Terraria.ModLoader;
using Microsoft.Xna.Framework;

namespace ImapoTikTokIntegrationMod
{
    public class TikTestCommand : ModCommand
    {
        public override string Command => "tiktest";
        public override CommandType Type => CommandType.Chat;
        public override string Description => "TikFinity test simulator";

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            if (args.Length == 0)
            {
                Main.NewText("Usage: /tiktest gift|share|sub|like|join", Color.Gray);
                return;
            }

            switch (args[0])
            {
                case "gift":
                    int giftCount = args.Length > 1 ? int.Parse(args[1]) : 1;
                    int giftPrice = args.Length > 2 ? int.Parse(args[2]) : 1; // если указано, берём третий аргумент
                    TikFinityClient.TestGift(count: giftCount, diamonds: giftPrice);
                    break;

                case "share":
                    TikFinityClient.TestShare();
                    break;

                case "sub":
                    TikFinityClient.TestSubscribe();
                    break;

                case "like":
                    int likes = args.Length > 1 ? int.Parse(args[1]) : 10;
                    TikFinityClient.TestLike(likes);
                    break;

                case "join":
                    if (args.Length > 1 && args[1] == "mod")
                        TikFinityClient.TestModeratorJoin();
                    if (args.Length > 1 && args[1] == "gift")
                        TikFinityClient.TestGifterJoin();
                    else
                        TikFinityClient.TestSubscriberJoin();
                    break;

                default:
                    Main.NewText("Unknown test command", Color.Red);
                    break;
            }
        }
    }
}
