using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using Terraria;
using Terraria.ModLoader;
using static TikFinityClient;

public static class TikFinityDatabase
{
    private const string ModDataFolderName = "ImapoTikTokIntegrationModBD";
    public static string CurrentStreamerKey = "default";

    public static Dictionary<string, ViewerInfo> ViewerDatabase { get; private set; } = new();
    public static List<SubscriberDatabaseEntry> SubscriberDatabase { get; private set; } = new();
    public static Dictionary<string, ModeratorInfo> ModeratorDatabase { get; private set; } = new();
    public static List<GiftDatabaseEntry> GiftDatabase { get; private set; } = new();
    public static HashSet<string> SubscriberIds { get; private set; } = new();
    public static HashSet<string> GiftGiverIds { get; private set; } = new();

    private static string GetModDataRoot() => Path.Combine(Main.SavePath, ModDataFolderName);
    public static string GetStreamerPath()
    {
        string safeName = MakeSafeFolderName(CurrentStreamerKey);
        return Path.Combine(GetModDataRoot(), safeName);
    }

    public static string MakeSafeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    public static string ViewerDatabaseFilePath => Path.Combine(GetStreamerPath(), "ViewerDatabase.json");
    public static string SubscriberDatabaseFilePath => Path.Combine(GetStreamerPath(), "SubscriberDatabase.json");
    public static string ModeratorDatabaseFilePath => Path.Combine(GetStreamerPath(), "ModeratorDatabase.json");
    public static string GiftDatabaseFilePath => Path.Combine(GetStreamerPath(), "GiftDatabase.json");

    public static void EnsureStreamerDirectory()
    {
        Directory.CreateDirectory(GetStreamerPath());
    }

    #region Viewer Database

    public static void ExportViewerDatabase()
    {
        try
        {
            var list = ViewerDatabase.Values.ToList();
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            string json = JsonSerializer.Serialize(list, options);
            File.WriteAllText(ViewerDatabaseFilePath, json);
            ModContent.GetInstance<ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                .Logger.Info($"[Tikfinity] Viewer database exported to {ViewerDatabaseFilePath}");
        }
        catch (Exception ex)
        {
            ModContent.GetInstance<ImapoTikTokIntegrationMod.ImapoTikTokIntegrationMod>()
                .Logger.Info($"[Tikfinity ERROR] Failed to export viewer database: {ex}");
        }
    }

    public static void ClearCaches()
    {
        ViewerDatabase.Clear();
        ModeratorDatabase.Clear();
        SubscriberDatabase.Clear();
        GiftDatabase.Clear();
    }

    public static void ImportViewerDatabase()
    {
        try
        {
            if (!File.Exists(ViewerDatabaseFilePath)) return;

            string json = File.ReadAllText(ViewerDatabaseFilePath);
            var list = JsonSerializer.Deserialize<List<ViewerInfo>>(json);

            if (list != null)
            {
                ViewerDatabase.Clear();
                foreach (var v in list)
                {
                    if (!string.IsNullOrEmpty(v.Key))
                        ViewerDatabase[v.Key] = v;
                }
                Main.NewText($"[Tikfinity] Viewer database imported from {ViewerDatabaseFilePath}", 50, 255, 50);
            }
        }
        catch (Exception ex)
        {
            Main.NewText($"[Tikfinity ERROR] Failed to import viewer database: {ex}", 255, 100, 0);
        }
    }

    public static void UpdateViewerDatabaseJson()
    {
        try
        {
            EnsureStreamerDirectory();
            var list = ViewerDatabase.Values.ToList();
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(ViewerDatabaseFilePath, JsonSerializer.Serialize(list, options));
        }
        catch (Exception ex)
        {
            Main.NewText($"[Tikfinity ERROR] Failed to update viewer JSON: {ex}", 255, 100, 0);
        }
    }

    #endregion

    #region Subscriber Database

    public static void ImportSubscriberDatabase()
    {
        try
        {
            if (!File.Exists(SubscriberDatabaseFilePath)) return;

            string json = File.ReadAllText(SubscriberDatabaseFilePath);
            var list = JsonSerializer.Deserialize<List<SubscriberDatabaseEntry>>(json);
            if (list != null)
            {
                SubscriberDatabase = list;
                RebuildSubscriberCache();
            }
        }
        catch (Exception ex)
        {
            Main.NewText($"[Tikfinity ERROR] Failed to import subscriber Database: {ex}", 255, 100, 0);
        }
    }

    public static void UpdateSubscriberDatabaseJson(SubscriberDatabaseEntry entry)
    {
        try
        {
            EnsureStreamerDirectory();
            SubscriberDatabase.Add(entry);
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(SubscriberDatabaseFilePath, JsonSerializer.Serialize(SubscriberDatabase, options));
            RebuildSubscriberCache();
        }
        catch (Exception ex)
        {
            Main.NewText($"[Tikfinity ERROR] Failed to update subscriber Database JSON: {ex}", 255, 100, 0);
        }
    }

    public static void RebuildSubscriberCache()
    {
        SubscriberIds.Clear();
        foreach (var s in SubscriberDatabase)
        {
            if (!string.IsNullOrEmpty(s.Key))
                SubscriberIds.Add(s.Key);
        }
    }

    #endregion

    #region Moderator Database

    public static void ImportModeratorDatabase()
    {
        try
        {
            if (!File.Exists(ModeratorDatabaseFilePath)) return;

            string json = File.ReadAllText(ModeratorDatabaseFilePath);
            var list = JsonSerializer.Deserialize<List<ModeratorInfo>>(json);

            if (list != null)
            {
                ModeratorDatabase.Clear();
                foreach (var m in list)
                {
                    if (!string.IsNullOrEmpty(m.Key))
                        ModeratorDatabase[m.Key] = m;
                }
                Main.NewText($"[Tikfinity] Moderator database imported from {ModeratorDatabaseFilePath}", 50, 255, 50);
            }
        }
        catch (Exception ex)
        {
            Main.NewText($"[Tikfinity ERROR] Failed to import moderator database: {ex}", 255, 100, 0);
        }
    }

    public static void UpdateModeratorDatabaseJson()
    {
        try
        {
            EnsureStreamerDirectory();
            var list = ModeratorDatabase.Values.ToList();
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(ModeratorDatabaseFilePath, JsonSerializer.Serialize(list, options));
        }
        catch (Exception ex)
        {
            Main.NewText($"[Tikfinity ERROR] Failed to update moderator JSON: {ex}", 255, 100, 0);
        }
    }

    #endregion

    #region Gift Database

    public static void ImportGiftDatabase()
    {
        try
        {
            if (!File.Exists(GiftDatabaseFilePath)) return;

            string json = File.ReadAllText(GiftDatabaseFilePath);
            var list = JsonSerializer.Deserialize<List<GiftDatabaseEntry>>(json);

            if (list != null)
            {
                GiftDatabase.Clear();
                foreach (var g in list)
                {
                    if (!string.IsNullOrEmpty(g.Key))
                        GiftDatabase.Add(g);
                }
                RebuildGiftGiverCache();
                Main.NewText($"[Tikfinity] Gift database imported from {GiftDatabaseFilePath}", 50, 255, 50);
            }
        }
        catch (Exception ex)
        {
            Main.NewText($"[Tikfinity ERROR] Failed to import gift database: {ex}", 255, 100, 0);
        }
    }

    public static void UpdateGiftDatabaseJson()
    {
        try
        {
            EnsureStreamerDirectory();
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(GiftDatabaseFilePath, JsonSerializer.Serialize(GiftDatabase, options));
        }
        catch (Exception ex)
        {
            Main.NewText($"[Tikfinity ERROR] Failed to update gift JSON: {ex}", 255, 100, 0);
        }
    }

    private static void RebuildGiftGiverCache()
    {
        GiftGiverIds.Clear();
        foreach (var g in GiftDatabase)
        {
            if (!string.IsNullOrEmpty(g.Key))
                GiftGiverIds.Add(g.Key);
        }
    }

    public static void AddGiftDatabase(string key, string nickname, int coins)
    {
        GiftDatabase.Add(new GiftDatabaseEntry
        {
            Key = key,
            Nickname = nickname,
            Coins = coins,
            Time = DateTime.Now.ToString("dd.MM.yy HH:mm:ss")
        });
        UpdateGiftDatabaseJson();
        RebuildGiftGiverCache();
    }

    #endregion
}
