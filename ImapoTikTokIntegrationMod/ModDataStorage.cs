using System.Collections.Generic;
using static TikFinityClient;

namespace ImapoTikTokIntegrationMod
{
    public static class ModDataStorage
    {
        public static Dictionary<string, TikFinityClient.ViewerInfo> ViewerDatabase = new();
        public static Dictionary<string, TikFinityClient.ModeratorInfo> ModeratorDatabase = new();
        public static List<TikFinityClient.SubscriberDatabaseEntry> SubscriberDatabase = new();
        public static Dictionary<string, GiftDatabaseEntry> giftDatabase = new();
    }
}
