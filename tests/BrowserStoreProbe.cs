using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MiniumBrowser;

internal static class BrowserStoreProbe
{
    private static int checks;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        checks++;
    }

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Pass a writable test artifact directory.");
            string root = Path.Combine(Path.GetFullPath(args[0]), "store-" + Guid.NewGuid().ToString("N"));
            BrowserStore store = new BrowserStore(root);
            string migrationRoot = root + "-retention";
            Directory.CreateDirectory(migrationRoot);
            File.WriteAllText(Path.Combine(migrationRoot,"browser-state.json"),"{\"settings\":{\"saveHistory\":false,\"restoreSession\":false,\"clearOnExit\":true}}");
            var migrated = new BrowserStore(migrationRoot);
            Check(migrated.Settings.SaveHistory && migrated.Settings.RestoreSession && migrated.Settings.ClearOnExit, "previous privacy defaults migrate without changing cleanup");
            migrated.Settings.SaveHistory=false; migrated.Settings.RestoreSession=false; migrated.Save();
            var explicitChoice = new BrowserStore(migrationRoot);
            Check(!explicitChoice.Settings.SaveHistory && !explicitChoice.Settings.RestoreSession, "later explicit retention choices remain respected");
            Check(store.Settings.Accent == "#b83c4c" && store.Settings.Wallpaper == "default" && store.Settings.WallpaperOpacity == 0.78 && store.Settings.RestoreSession && store.Settings.SaveHistory && store.Settings.ClearOnExit, "history and session defaults");
            store.Settings.SaveHistory=false; store.Settings.RestoreSession=false;
            store.AddHistory("https://privacy.example/", "private");
            store.SessionTabs.Add(new SavedTab { Id="private", Url="https://privacy.example/", Title="private" });
            store.Save();
            Check(store.History.Count==0 && new BrowserStore(root).SessionTabs.Count==0, "privacy defaults do not persist history or tabs");
            store.Settings.SaveHistory=true;
            Check(store.Settings.SearchEngine == "brave", "Brave is default");
            Check(store.Settings.AdBlockEnabled, "ad blocker enabled for new or legacy profiles");
            store.ApplySettings(new Dictionary<string, object> {
                { "accent", "#ABCDEF" }, { "theme", "midnight" }, { "wallpaper", "dantalian" },
                { "wallpaperOpacity", 0.7 }, { "wallpaperBlur", 12 }, { "tabLayout", "vertical" },
                { "searchEngine", "searxng" }, { "searxInstance", "http://localhost:8080/searx/" }, { "compact", true }, { "sidebar", false },
                { "adBlockEnabled", false }, { "bookmarkBar", false }, { "restoreSession", true }, { "homePage", "https://example.com/" },
                { "customCss", "body::after { content: '</script><script>alert(1)</script>\\\"'; }" },
                { "webviewArguments", "--disable-web-security" }
            });
            Check(store.Settings.Accent == "#abcdef" && store.Settings.Theme == "midnight" && store.Settings.TabLayout == "vertical", "valid settings patch");
            store.Notes = "Ghi chép: \"Victorique\"\n</script><img src=x onerror=alert(1)>\\終";
            store.AddBookmark("https://example.com/anime?q=\"hello\"", "<script>bookmark</script>");
            store.AddBookmark("https://example.com/anime?q=\"hello\"", "Updated bookmark");
            Check(store.Bookmarks.Count == 1 && store.Bookmarks[0].Title == "Updated bookmark", "bookmark deduplicates normalized URL");
            string bookmarkId = store.Bookmarks[0].Id;
            store.AddHistory("https://example.com/anime", "Rozen Maiden");
            store.AddHistory("https://example.com/anime", "New title");
            Check(store.History.Count == 1 && store.History[0].Title == "New title", "consecutive navigation deduplication");
            store.SessionTabs.Add(new SavedTab { Id = "tab-1", Url = "minium://newtab", Title = "New", Pinned = false });
            store.SessionTabs.Add(new SavedTab { Id = "tab-2", Url = "https://example.com/", Title = "Example", Pinned = true });
            store.SessionTabs.Add(new SavedTab { Id = "tab-3", Url = "about:blank", Title = "Blank", Pinned = false });
            store.ActiveTabId = "tab-2";
            store.Save();
            BrowserStore loaded = new BrowserStore(root);
            Check(loaded.Settings.SearchEngine == "searxng" && loaded.Settings.SearxInstance == "http://localhost:8080/searx/", "search provider and instance persist");
            Check(!loaded.Settings.AdBlockEnabled, "ad blocker disabled preference persists");
            Check(loaded.Settings.Accent == "#abcdef" && loaded.Settings.WallpaperOpacity == 0.7 && loaded.Settings.WallpaperBlur == 12 && loaded.Settings.RestoreSession, "settings persist");
            Check(loaded.Notes == store.Notes && loaded.Settings.CustomCss == store.Settings.CustomCss, "JSON escaping round trip");
            Check(loaded.Bookmarks.Count == 1 && loaded.Bookmarks[0].Id == bookmarkId && loaded.History.Count == 1, "entries and stable IDs persist");
            Check(loaded.SessionTabs.Count == 3 && loaded.SessionTabs[1].Pinned && loaded.SessionTabs[2].Url == "about:blank" && loaded.ActiveTabId == "tab-2", "session metadata including blank tabs persists");
            string raw = File.ReadAllText(Path.Combine(root, "browser-state.json"));
            Check(raw.Contains("\"schemaVersion\":1") && raw.Contains("\"restoreSession\":true") && raw.Contains("\"saveHistory\":true") && !raw.Contains("webviewArguments"), "only known schema serialized in lower camel case");

            loaded.ApplySettings(new Dictionary<string, object> {
                { "accent", "red; background:url(x)" }, { "theme", "unknown" }, { "wallpaper", "../../secrets" },
                { "tabLayout", "diagonal" }, { "searchEngine", "javascript:bad" }, { "compact", "false" },
                { "wallpaperOpacity", Double.NaN }, { "homePage", "javascript:alert(1)" }
            });
            Check(loaded.Settings.Accent == "#abcdef" && loaded.Settings.Theme == "midnight" && loaded.Settings.Wallpaper == "dantalian" && loaded.Settings.Compact && loaded.Settings.WallpaperOpacity == 0.7 && loaded.Settings.HomePage == "https://example.com/", "invalid patch values leave existing settings");
            loaded.ApplySettings(new Dictionary<string, object> { { "wallpaperOpacity", 42 }, { "wallpaperBlur", -20 }, { "customCss", new string('x', BrowserStore.MaxCustomCss + 100) } });
            Check(loaded.Settings.WallpaperOpacity == 1 && loaded.Settings.WallpaperBlur == 0 && loaded.Settings.CustomCss.Length == BrowserStore.MaxCustomCss, "settings ranges and CSS length bounded");
            foreach (string unsafeUrl in new[] { "javascript:alert(1)", "file:///C:/secret.txt", "https://user:pass@example.com/", "https://example.com/\nheader", "minium://newtab" })
            {
                loaded.AddBookmark(unsafeUrl, "unsafe");
                loaded.AddHistory(unsafeUrl, "unsafe");
            }
            Check(loaded.Bookmarks.Count == 1 && loaded.History.Count == 1, "bookmarks/history reject non-web URLs and credentials");
            loaded.RemoveBookmark(bookmarkId);
            Check(loaded.Bookmarks.Count == 0, "bookmark removal by ID");
            for (int i = 0; i < BrowserStore.MaxBookmarks + 25; i++) loaded.AddBookmark("https://example.com/bookmark/" + i, new string('b', 1000));
            Check(loaded.Bookmarks.Count == BrowserStore.MaxBookmarks && loaded.Bookmarks[0].Title.Length == 256, "bookmark count and title bounds");
            for (int i = 0; i < BrowserStore.MaxHistory + 25; i++) loaded.AddHistory("https://example.com/history/" + i, "History " + i);
            Check(loaded.History.Count == BrowserStore.MaxHistory && loaded.History[0].Url.EndsWith("/2024") && loaded.History[1999].Url.EndsWith("/25"), "history retains latest 2000 entries");
            loaded.Notes = new string('n', BrowserStore.MaxNotes + 20);
            loaded.SessionTabs.Clear();
            loaded.SessionTabs.Add(new SavedTab { Id = "bad", Url = "file:///C:/secret.txt", Title = "bad" });
            for (int i = 0; i < BrowserStore.MaxTabs + 5; i++) loaded.SessionTabs.Add(new SavedTab { Id = "duplicate", Url = "https://example.com/tab/" + i });
            loaded.ActiveTabId = "not-a-tab";
            loaded.Settings.Accent = "invalid-direct-edit";
            loaded.Save();
            Check(loaded.Notes.Length == BrowserStore.MaxNotes && loaded.SessionTabs.Count == BrowserStore.MaxTabs && loaded.ActiveTabId == loaded.SessionTabs[0].Id, "notes/session limits and active tab normalization");
            Check(loaded.Settings.Accent == "#b83c4c" && loaded.SessionTabs[0].Id != loaded.SessionTabs[1].Id, "direct model edits normalized and duplicate IDs repaired");
            loaded = new BrowserStore(root);
            Check(loaded.Bookmarks.Count == BrowserStore.MaxBookmarks && loaded.History.Count == BrowserStore.MaxHistory && loaded.Notes.Length == BrowserStore.MaxNotes, "bounded state reload");
            loaded.ResetSettings();
            Check(loaded.Settings.Theme == "gothic" && loaded.Settings.CustomCss == "" && loaded.History.Count == BrowserStore.MaxHistory, "reset affects settings only");

            // A crash or manual corruption cannot turn the last good JSON into partial state.
            string recoveryRoot = Path.Combine(root, "recovery");
            BrowserStore recovery = new BrowserStore(recoveryRoot);
            recovery.Notes = "first durable save";
            recovery.Save();
            recovery.Notes = "second durable save";
            recovery.Save();
            Check(File.Exists(Path.Combine(recoveryRoot, "browser-state.json.bak")), "atomic replacement keeps prior backup");
            File.WriteAllText(Path.Combine(recoveryRoot, "browser-state.json"), "{broken JSON", Encoding.UTF8);
            recovery = new BrowserStore(recoveryRoot);
            Check(recovery.Notes == "first durable save" && !String.IsNullOrEmpty(recovery.RecoveryMessage), "corrupt primary recovers backup");
            Check(Directory.GetFiles(recoveryRoot, "browser-state.json.corrupt-*").Length == 1, "corrupt original preserved");
            recovery.Save();
            Check(new BrowserStore(recoveryRoot).Notes == "first durable save", "recovered state saves cleanly");
            string noBackupRoot = Path.Combine(root, "no-backup");
            Directory.CreateDirectory(noBackupRoot);
            File.WriteAllText(Path.Combine(noBackupRoot, "browser-state.json"), "[]");
            recovery = new BrowserStore(noBackupRoot);
            Check(recovery.Settings.Theme == "gothic" && recovery.Bookmarks.Count == 0 && !String.IsNullOrEmpty(recovery.RecoveryMessage), "wrong root type recovers defaults");
            string maliciousRoot = Path.Combine(root, "malformed-schema");
            Directory.CreateDirectory(maliciousRoot);
            File.WriteAllText(Path.Combine(maliciousRoot, "browser-state.json"), "{\"settings\":{\"theme\":[],\"sidebar\":\"false\",\"homePage\":\"file:///C:/x\"},\"bookmarks\":[null,{}, {\"url\":\"javascript:evil()\"}],\"history\":[{\"url\":\"https://example.com\",\"visitedAt\":\"bad\"}],\"sessionTabs\":[{\"id\":\"t1\",\"url\":\"minium://newtab\"},{\"url\":\"minium://untrusted\"}],\"notes\":17}");
            recovery = new BrowserStore(maliciousRoot);
            Check(recovery.Settings.Sidebar && recovery.Settings.HomePage == "minium://newtab" && recovery.Bookmarks.Count == 0 && recovery.History.Count == 0 && recovery.SessionTabs.Count == 1 && recovery.Notes == "", "schema normalization rejects malformed field types and URLs");
            string lockedRoot = Path.Combine(root, "locked-state");
            Directory.CreateDirectory(lockedRoot);
            string lockedPath = Path.Combine(lockedRoot, "browser-state.json");
            File.WriteAllText(lockedPath, "{broken-but-important");
            using (FileStream locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                recovery = new BrowserStore(lockedRoot);
                bool blocked = false;
                try { recovery.Save(); } catch (IOException) { blocked = true; }
                Check(blocked && !String.IsNullOrEmpty(recovery.RecoveryMessage), "unreadable original cannot be overwritten when backup preservation fails");
            }
            Check(File.ReadAllText(lockedPath) == "{broken-but-important", "locked original remains intact");
            Check(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length == 0, "no incomplete temp files after save");
            Console.WriteLine("PASS BrowserStore: " + checks + " checks; artifacts=" + root);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
