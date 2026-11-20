using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MiniumBrowser
{
    internal sealed class BrowserSettings
    {
        public string Accent { get; set; }
        public string Theme { get; set; }
        public string Wallpaper { get; set; }
        public double WallpaperOpacity { get; set; }
        public int WallpaperBlur { get; set; }
        public string TabLayout { get; set; }
        public bool Compact { get; set; }
        public bool Sidebar { get; set; }
        public bool BookmarkBar { get; set; }
        public string SearchEngine { get; set; }
        public string SearxInstance { get; set; }
        public bool RestoreSession { get; set; }
        public bool SaveHistory { get; set; }
        public bool ClearOnExit { get; set; }
        public string CustomCss { get; set; }
        public string HomePage { get; set; }
        public bool AdBlockEnabled { get; set; }

        public BrowserSettings()
        {
            Accent = "#b83c4c";
            Theme = "gothic";
            Wallpaper = "default";
            WallpaperOpacity = 0.78;
            WallpaperBlur = 0;
            TabLayout = "horizontal";
            Compact = false;
            Sidebar = true;
            BookmarkBar = true;
            SearchEngine = "brave";
            SearxInstance = "https://searx.tiekoetter.com/";
            RestoreSession = true; ClearOnExit = true; SaveHistory = true;
            CustomCss = String.Empty;
            HomePage = "minium://newtab";
            AdBlockEnabled = true;
        }
    }

    internal sealed class BookmarkEntry
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
    }

    internal sealed class HistoryEntry
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public string VisitedAt { get; set; }
    }

    internal sealed class SavedTab
    {
        public string Id { get; set; }
        public string Url { get; set; }
        public string Title { get; set; }
        public bool Pinned { get; set; }
    }

    // This stores browser-owned preferences only. WebView's profile owns cookies and logins.
    internal sealed class BrowserStore
    {
        internal const int MaxHistory = 2000;
        internal const int MaxBookmarks = 500;
        internal const int MaxTabs = 50;
        internal const int MaxNotes = 200000;
        internal const int MaxCustomCss = 30000;
        private const int MaxUrl = 2048;
        private const int MaxTitle = 256;
        private const int MaxJson = 12 * 1024 * 1024;
        private readonly string statePath;
        private readonly JavaScriptSerializer serializer;
        private bool saveBlocked;

        public BrowserSettings Settings { get; private set; }
        public List<BookmarkEntry> Bookmarks { get; set; }
        public List<HistoryEntry> History { get; set; }
        public List<SavedTab> SessionTabs { get; set; }
        public string Notes { get; set; }
        public string ActiveTabId { get; set; }
        public string RecoveryMessage { get; private set; }

        public BrowserStore(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentException("A storage directory is required.", "root");
            Directory.CreateDirectory(root);
            statePath = Path.Combine(Path.GetFullPath(root), "browser-state.json");
            serializer = new JavaScriptSerializer { MaxJsonLength = MaxJson, RecursionLimit = 16 };
            ResetState();
            Load();
        }

        public void Save()
        {
            if (saveBlocked) throw new IOException("Unreadable browser state could not be preserved. Close other instances and restart before saving.");
            Normalize();
            string json = serializer.Serialize(ToDocument());
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            if (bytes.Length > MaxJson) throw new InvalidOperationException("Browser state exceeds its storage limit.");
            string temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                // Same-directory replacement preserves either the old or the complete new JSON.
                if (File.Exists(statePath)) File.Replace(temporary, statePath, statePath + ".bak", true);
                else File.Move(temporary, statePath);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public void AddHistory(string url, string title)
        {
            if (!Settings.SaveHistory) return;
            string safe;
            if (!TryUrl(url, false, out safe)) return;
            if (History == null) History = new List<HistoryEntry>();
            string timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            if (History.Count > 0 && History[0] != null && History[0].Url == safe)
            {
                History[0].Title = Title(title, safe);
                History[0].VisitedAt = timestamp;
                return;
            }
            History.Insert(0, new HistoryEntry { Id = NewId(), Url = safe, Title = Title(title, safe), VisitedAt = timestamp });
            if (History.Count > MaxHistory) History.RemoveRange(MaxHistory, History.Count - MaxHistory);
        }

        public void AddBookmark(string url, string title)
        {
            string safe;
            if (!TryUrl(url, false, out safe)) return;
            if (Bookmarks == null) Bookmarks = new List<BookmarkEntry>();
            foreach (BookmarkEntry item in Bookmarks)
            {
                if (item != null && item.Url == safe)
                {
                    item.Title = Title(title, safe);
                    return;
                }
            }
            if (Bookmarks.Count >= MaxBookmarks) return;
            Bookmarks.Add(new BookmarkEntry { Id = NewId(), Url = safe, Title = Title(title, safe) });
        }

        public void RemoveBookmark(string id)
        {
            if (Bookmarks != null) Bookmarks.RemoveAll(delegate(BookmarkEntry entry) { return entry != null && entry.Id == id; });
        }

        public void ResetSettings() { Settings = new BrowserSettings(); }

        public void ApplySettings(IDictionary<string, object> patch)
        {
            if (patch == null) return;
            if (Settings == null) ResetSettings();
            object value;
            if (patch.TryGetValue("accent", out value) && value is string && Regex.IsMatch((string)value, "^#[0-9a-fA-F]{6}$")) Settings.Accent = ((string)value).ToLowerInvariant();
            Settings.Theme = Option(patch, "theme", Settings.Theme, "gothic", "midnight", "parchment");
            Settings.Wallpaper = Option(patch, "wallpaper", Settings.Wallpaper, "default", "gosick", "dantalian", "rozen-maiden", "custom");
            Settings.TabLayout = Option(patch, "tabLayout", Settings.TabLayout, "horizontal", "vertical");
            Settings.SearchEngine = Option(patch, "searchEngine", Settings.SearchEngine, "brave", "searxng");
            string instance;
            if (patch.TryGetValue("searxInstance", out value) && value is string && TrySearxInstance((string)value, out instance)) Settings.SearxInstance = instance;
            double number;
            if (patch.TryGetValue("wallpaperOpacity", out value) && Number(value, out number)) Settings.WallpaperOpacity = Math.Max(0, Math.Min(1, number));
            if (patch.TryGetValue("wallpaperBlur", out value) && Number(value, out number)) Settings.WallpaperBlur = (int)Math.Max(0, Math.Min(30, number));
            Settings.Compact = Boolean(patch, "compact", Settings.Compact);
            Settings.Sidebar = Boolean(patch, "sidebar", Settings.Sidebar);
            Settings.BookmarkBar = Boolean(patch, "bookmarkBar", Settings.BookmarkBar);
            Settings.SaveHistory = Boolean(patch, "saveHistory", Settings.SaveHistory);
            Settings.ClearOnExit = Boolean(patch, "clearOnExit", Settings.ClearOnExit);
            Settings.RestoreSession = Boolean(patch, "restoreSession", Settings.RestoreSession);
            Settings.AdBlockEnabled = Boolean(patch, "adBlockEnabled", Settings.AdBlockEnabled);
            if (patch.TryGetValue("customCss", out value) && value is string) Settings.CustomCss = Bounded((string)value, MaxCustomCss);
            string safe;
            if (patch.TryGetValue("homePage", out value) && value is string && TryUrl((string)value, true, out safe)) Settings.HomePage = safe;
        }

        internal static bool TrySearxInstance(string input, out string result)
        {
            result = null;
            Uri uri;
            if (String.IsNullOrWhiteSpace(input) || input.Length > 2048 || System.Text.RegularExpressions.Regex.IsMatch(input, @"[\x00-\x20\\]") || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out uri)) return false;
            if ((uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || String.IsNullOrEmpty(uri.Host)) return false;
            string path = uri.AbsolutePath.TrimEnd('/');
            if (path.EndsWith("/search", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 7);
            result = uri.GetLeftPart(UriPartial.Authority) + path + "/";
            return true;
        }

        private void Load()
        {
            if (!File.Exists(statePath))
            {
                if (File.Exists(statePath + ".bak") && TryLoad(statePath + ".bak")) RecoveryMessage = "Recovered browser state from backup.";
                return;
            }
            if (TryLoad(statePath)) return;
            string corruptPath = statePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + NewId();
            try { File.Move(statePath, corruptPath); }
            catch (IOException) { saveBlocked = true; RecoveryMessage = "Could not preserve unreadable browser state; saving is blocked until restart."; return; }
            catch (UnauthorizedAccessException) { saveBlocked = true; RecoveryMessage = "Could not preserve unreadable browser state; saving is blocked until restart."; return; }
            bool recovered = File.Exists(statePath + ".bak") && TryLoad(statePath + ".bak");
            RecoveryMessage = recovered ? "Recovered browser state from backup; unreadable state was preserved." : "Unreadable browser state was preserved; defaults were loaded.";
        }

        private bool TryLoad(string path)
        {
            try
            {
                if (new FileInfo(path).Length > MaxJson) return false;
                IDictionary<string, object> document = serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as IDictionary<string, object>;
                if (document == null) return false;
                object version;
                double number;
                if (document.TryGetValue("schemaVersion", out version) && (!Number(version, out number) || number != 1)) return false;
                ReadDocument(document);
                return true;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (FormatException) { }
            ResetState();
            return false;
        }

        private void ResetState()
        {
            ResetSettings();
            Bookmarks = new List<BookmarkEntry>();
            History = new List<HistoryEntry>();
            SessionTabs = new List<SavedTab>();
            Notes = String.Empty;
            ActiveTabId = String.Empty;
        }

        private void ReadDocument(IDictionary<string, object> document)
        {
            ResetState();
            object value;
            if (document.TryGetValue("settings", out value)) { var saved = value as IDictionary<string, object>; ApplySettings(saved); if (saved != null && !saved.ContainsKey("retentionDefaultsVersion")) { Settings.RestoreSession = true; Settings.SaveHistory = true; } }
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> urls = new HashSet<string>(StringComparer.Ordinal);
            foreach (IDictionary<string, object> item in Items(document, "bookmarks"))
            {
                string safe;
                if (!TryUrl(Text(item, "url"), false, out safe) || !urls.Add(safe)) continue;
                Bookmarks.Add(new BookmarkEntry { Id = UniqueId(Text(item, "id"), ids), Url = safe, Title = Title(Text(item, "title"), safe) });
                if (Bookmarks.Count >= MaxBookmarks) break;
            }
            ids.Clear();
            foreach (IDictionary<string, object> item in Items(document, "history"))
            {
                string safe;
                if (!TryUrl(Text(item, "url"), false, out safe)) continue;
                DateTimeOffset timestamp;
                string visited = Text(item, "visitedAt");
                if (!DateTimeOffset.TryParse(visited, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp)) continue;
                History.Add(new HistoryEntry { Id = UniqueId(Text(item, "id"), ids), Url = safe, Title = Title(Text(item, "title"), safe), VisitedAt = timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) });
                if (History.Count >= MaxHistory) break;
            }
            ids.Clear();
            foreach (IDictionary<string, object> item in Items(document, "sessionTabs"))
            {
                string safe;
                if (!TryUrl(Text(item, "url"), true, out safe)) continue;
                SessionTabs.Add(new SavedTab { Id = UniqueId(Text(item, "id"), ids), Url = safe, Title = Title(Text(item, "title"), safe), Pinned = Boolean(item, "pinned", false) });
                if (SessionTabs.Count >= MaxTabs) break;
            }
            Notes = Bounded(Text(document, "notes"), MaxNotes);
            string active = Text(document, "activeTabId");
            ActiveTabId = ids.Contains(active) ? active : (SessionTabs.Count == 0 ? String.Empty : SessionTabs[0].Id);
        }

        private void Normalize()
        {
            // The same whitelist protects both loaded JSON and direct host-side edits.
            ReadDocument(ToDocument());
        }

        private Dictionary<string, object> ToDocument()
        {
            BrowserSettings s = Settings ?? new BrowserSettings();
            Dictionary<string, object> settings = new Dictionary<string, object> {
                { "accent", s.Accent }, { "theme", s.Theme }, { "wallpaper", s.Wallpaper },
                { "wallpaperOpacity", s.WallpaperOpacity }, { "wallpaperBlur", s.WallpaperBlur },
                { "tabLayout", s.TabLayout }, { "compact", s.Compact }, { "sidebar", s.Sidebar },
                { "bookmarkBar", s.BookmarkBar }, { "searchEngine", s.SearchEngine }, { "searxInstance", s.SearxInstance },
                { "retentionDefaultsVersion", 1 }, { "saveHistory", s.SaveHistory }, { "clearOnExit", s.ClearOnExit }, { "adBlockEnabled", s.AdBlockEnabled }, { "restoreSession", s.RestoreSession }, { "customCss", s.CustomCss }, { "homePage", s.HomePage }
            };
            List<object> bookmarks = new List<object>();
            if (Bookmarks != null) foreach (BookmarkEntry item in Bookmarks)
                if (item != null) bookmarks.Add(new Dictionary<string, object> { { "id", item.Id }, { "title", item.Title }, { "url", item.Url } });
            List<object> history = new List<object>();
            if (History != null) foreach (HistoryEntry item in History)
                if (item != null) history.Add(new Dictionary<string, object> { { "id", item.Id }, { "title", item.Title }, { "url", item.Url }, { "visitedAt", item.VisitedAt } });
            List<object> tabs = new List<object>();
            if (s.RestoreSession && SessionTabs != null) foreach (SavedTab item in SessionTabs)
                if (item != null) tabs.Add(new Dictionary<string, object> { { "id", item.Id }, { "title", item.Title }, { "url", item.Url }, { "pinned", item.Pinned } });
            return new Dictionary<string, object> { { "schemaVersion", 1 }, { "settings", settings }, { "bookmarks", bookmarks }, { "history", history }, { "sessionTabs", tabs }, { "notes", Notes }, { "activeTabId", ActiveTabId } };
        }

        private static IEnumerable<IDictionary<string, object>> Items(IDictionary<string, object> document, string key)
        {
            object value;
            if (!document.TryGetValue(key, out value) || value is string || value is IDictionary) yield break;
            IEnumerable items = value as IEnumerable;
            if (items == null) yield break;
            foreach (object item in items)
            {
                IDictionary<string, object> entry = item as IDictionary<string, object>;
                if (entry != null) yield return entry;
            }
        }

        private static bool TryUrl(string value, bool allowInternal, out string safe)
        {
            safe = null;
            value = (value ?? String.Empty).Trim();
            if (value.Length == 0 || value.Length > MaxUrl) return false;
            foreach (char c in value) if (Char.IsControl(c)) return false;
            if (allowInternal)
            {
                if (String.Equals(value, "about:blank", StringComparison.OrdinalIgnoreCase)) { safe = "about:blank"; return true; }
                foreach (string page in new[] { "newtab", "bookmarks", "history", "downloads", "settings", "notes", "deepsearch" })
                {
                    if (String.Equals(value, "minium://" + page, StringComparison.OrdinalIgnoreCase) || String.Equals(value, "minium://" + page + "/", StringComparison.OrdinalIgnoreCase))
                    { safe = "minium://" + page; return true; }
                }
            }
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || String.IsNullOrEmpty(uri.Host) || !String.IsNullOrEmpty(uri.UserInfo)) return false;
            safe = uri.AbsoluteUri;
            return safe.Length <= MaxUrl;
        }

        private static string Option(IDictionary<string, object> patch, string key, string previous, params string[] choices)
        {
            object value;
            if (patch.TryGetValue(key, out value) && value is string)
                foreach (string choice in choices) if (choice == (string)value) return choice;
            return previous;
        }

        private static bool Boolean(IDictionary<string, object> patch, string key, bool previous)
        {
            object value;
            return patch.TryGetValue(key, out value) && value is bool ? (bool)value : previous;
        }

        private static bool Number(object value, out double number)
        {
            number = 0;
            if (!(value is double || value is float || value is decimal || value is int || value is long || value is short || value is byte)) return false;
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return !Double.IsNaN(number) && !Double.IsInfinity(number);
        }

        private static string Text(IDictionary<string, object> source, string key)
        {
            object value;
            return source.TryGetValue(key, out value) ? value as string ?? String.Empty : String.Empty;
        }

        private static string Bounded(string value, int maximum)
        {
            value = value ?? String.Empty;
            if (value.Length <= maximum) return value;
            int length = maximum;
            if (Char.IsHighSurrogate(value[length - 1]) && Char.IsLowSurrogate(value[length])) length--;
            return value.Substring(0, length);
        }

        private static string Title(string value, string fallback) { return Bounded(String.IsNullOrWhiteSpace(value) ? fallback : value, MaxTitle); }
        private static string NewId() { return Guid.NewGuid().ToString("N"); }

        private static string UniqueId(string requested, HashSet<string> ids)
        {
            if (String.IsNullOrEmpty(requested) || requested.Length > 80 || !Regex.IsMatch(requested, "^[a-zA-Z0-9_-]+$") || !ids.Add(requested))
            {
                do { requested = NewId(); } while (!ids.Add(requested));
            }
            return requested;
        }
    }
}
