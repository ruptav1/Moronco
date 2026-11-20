using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace MiniumBrowser
{
    internal sealed partial class BrowserHost
    {
        private async Task HandleCommand(Dictionary<string, object> command)
        {
            if (closing || privacyClosing || privacyCleaned) return;
            string action = TextValue(command, "action"); BrowserTab tab = Active;
            switch (action)
            {
                case "ready":
                    if (uiReady) { Publish(); break; }
                    uiReady = true; Program.UiSucceeded = true; Publish();
                    await EnsureAdBlocker();
                    await RefreshVpn();
                    if (!String.IsNullOrWhiteSpace(Program.InitialInput)) await NavigateInput(Program.InitialInput);
                    else if (!String.IsNullOrWhiteSpace(Program.InitialUrl)) await NavigateInput(Program.InitialUrl);
                    else if (Program.SmokeMode == "blank") await NavigateInput("about:blank");
                    else if (tab != null && !IsInternal(tab.Url)) await EnsureTab(tab);
                    if (Program.SmokeMode != null && !smokeStarted) { smokeStarted = true; RunSmoke(); }
                    if (!String.IsNullOrEmpty(store.RecoveryMessage)) Toast(store.RecoveryMessage);
                    break;
                case "vpn-toggle": await ToggleVpn(); break;
                case "vpn-refresh": await RefreshVpn(); break;
                case "vpn-open": OpenWarp(); break;
                case "adblock-toggle": await ToggleAdBlocker(); break;
                case "adblock-refresh": await EnsureAdBlocker(); UpdateAdBlockStatus(); break;
                case "bounds":
                    pageBounds = Rectangle.Intersect(ClientRectangle, new Rectangle(Number(command,"x",60), Number(command,"y",92), Math.Max(1, Number(command,"width",ClientSize.Width-60)), Math.Max(1, Number(command,"height",ClientSize.Height-92)))); LayoutControllers(); break;
                case "overlay": if (Bool(command,"visible")) await LeaveFullscreen(); overlay = Bool(command,"visible"); LayoutControllers(); break;
                case "deep-search": await StartDeepSearch(command); break;
                case "deep-more": await MoreDeepSearch(); break;
                case "deep-cancel": CancelDeepSearch(); break;
                case "search": await Search(TextValue(command,"text"), TextValue(command,"engine",store.Settings.SearchEngine), false); break;
                case "search-both": await Search(TextValue(command,"text"), "brave", true); break;
                case "navigate": await NavigateInput(TextValue(command,"text")); break;
                case "tab-new": await NewTab(TextValue(command,"url","minium://newtab"), true); break;
                case "tab-select": await SelectTab(TextValue(command,"id")); break;
                case "tab-close": CloseTab(TextValue(command,"id",activeTabId)); break;
                case "tab-pin":
                    var pin = tabs.FirstOrDefault(t => t.Id == TextValue(command,"id",activeTabId));
                    if (pin != null) { pin.Pinned = !pin.Pinned; var ordered = tabs.OrderByDescending(t => t.Pinned).ToList(); tabs.Clear(); tabs.AddRange(ordered); DirtyPublish(); } break;
                case "tab-duplicate":
                    var duplicate = tabs.FirstOrDefault(t => t.Id == TextValue(command,"id",activeTabId)); if (duplicate != null) await NewTab(duplicate.Url,true); break;
                case "tab-reorder":
                    object ids;
                    if (command.TryGetValue("ids", out ids) && ids is object[])
                    {
                        var order = ((object[])ids).OfType<string>().Distinct().ToList();
                        if (order.Count == tabs.Count && order.All(id => tabs.Any(t => t.Id == id))) { var ordered = order.Select(id => tabs.First(t => t.Id == id)).ToList(); tabs.Clear(); tabs.AddRange(ordered); DirtyPublish(); }
                    } break;
                case "tab-reopen":
                    if (recentlyClosed.Count > 0) { var last = recentlyClosed[0]; recentlyClosed.RemoveAt(0); var reopened = await NewTab(last.Url,true); if (reopened != null) reopened.Pinned = last.Pinned; DirtyPublish(); } break;
                case "back": if (tab != null && tab.Web != null && tab.Web.CanGoBack) tab.Web.GoBack(); break;
                case "forward": if (tab != null && tab.Web != null && tab.Web.CanGoForward) tab.Web.GoForward(); break;
                case "reload": if (tab != null && tab.Web != null && !IsInternal(tab.Url)) tab.Web.Reload(); else Publish(); break;
                case "stop": if (tab != null && tab.Web != null) tab.Web.Stop(); break;
                case "home": await NavigateInput(store.Settings.HomePage); break;
                case "bookmark-add":
                    string url = TextValue(command,"url",tab == null ? "" : tab.Url), title = TextValue(command,"title",tab == null ? "" : tab.Title);
                    if (!IsWebUrl(url)) { Toast("Only HTTP/HTTPS pages can be bookmarked."); break; }
                    var existing = store.Bookmarks.FirstOrDefault(b => b.Id == TextValue(command,"id"));
                    if (existing != null) { existing.Url = url; existing.Title = title; }
                    else store.AddBookmark(url,title);
                    DirtyPublish(); Toast("Bookmark saved."); break;
                case "bookmark-remove": store.RemoveBookmark(TextValue(command,"id")); DirtyPublish(); break;
                case "history-clear": store.History.Clear(); DirtyPublish(); break;
                case "history-remove": store.History.RemoveAll(h => h.Id == TextValue(command,"id")); DirtyPublish(); break;
                case "notes-save": string notes = TextValue(command,"text"); store.Notes = notes.Substring(0,Math.Min(200000,notes.Length)); dirty = true; break;
                case "settings-save": object patch; if (command.TryGetValue("settings",out patch) && patch is Dictionary<string,object>) { var values = (Dictionary<string,object>)patch; object instanceValue; string normalizedInstance;
                    if (values.TryGetValue("searxInstance", out instanceValue) && (!(instanceValue is string) || !BrowserStore.TrySearxInstance((string)instanceValue, out normalizedInstance))) { Toast("Enter an HTTPS server or HTTP localhost address without query parameters or credentials."); Publish(); break; }
                    store.ApplySettings(values); DirtyPublish(); } break;
                case "settings-reset": bool adEnabled = store.Settings.AdBlockEnabled; store.ResetSettings(); store.Settings.AdBlockEnabled = adEnabled; DirtyPublish(); break;
                case "theme-import": ImportTheme(); break;
                case "theme-export": ExportTheme(); break;
                case "wallpaper-import": ImportWallpaper(); break;
                case "clear-data": await ClearBrowsingData(); break;
                case "find":
                    if (tab != null && tab.Web != null && !IsInternal(tab.Url))
                    { string result = await tab.Web.ExecuteScriptAsync("window.find(" + json.Serialize(TextValue(command,"text")) + ",false," + (Bool(command,"backwards") ? "true" : "false") + ",true)"); if (result == "false") Toast("No matching text found on this page."); } break;
                case "zoom": if (tab != null && tab.Controller != null) { tab.Controller.ZoomFactor = Math.Max(.25,Math.Min(5,Real(command,"value",1))); Publish(); } break;
                case "mute": if (tab != null && tab.Web != null) { tab.Web.IsMuted = !tab.Web.IsMuted; Publish(); } break;
                case "print": if (tab != null && tab.Web != null && !IsInternal(tab.Url)) tab.Web.ShowPrintUI(); break;
                case "screenshot": await SaveScreenshot(tab); break;
                case "pip":
                    if (tab != null && tab.Web != null) { await tab.Web.ExecuteScriptAsync("(()=>{const v=document.querySelector('video');if(v&&document.pictureInPictureEnabled)v.requestPictureInPicture().catch(()=>{});})()"); Toast("Picture-in-picture requires a supported video. Try the video context menu."); } break;
                case "devtools": if (tab != null && tab.Web != null) tab.Web.OpenDevToolsWindow(); break;
                case "download-open": OpenDownload(TextValue(command,"id"),false); break;
                case "download-folder": OpenDownload(TextValue(command,"id"),true); break;
                case "download-cancel": var cancel = downloads.FirstOrDefault(d => d.Id == TextValue(command,"id")); if (cancel != null) cancel.Operation.Cancel(); Publish(); break;
                case "download-pause": var pause = downloads.FirstOrDefault(d => d.Id == TextValue(command,"id")); if (pause != null) pause.Operation.Pause(); Publish(); break;
                case "download-resume": var resume = downloads.FirstOrDefault(d => d.Id == TextValue(command,"id")); if (resume != null && resume.Operation.CanResume) resume.Operation.Resume(); Publish(); break;
                case "downloads-clear": downloads.RemoveAll(d => d.Operation.State != CoreWebView2DownloadState.InProgress); Publish(); break;
                case "open-external": if (IsWebUrl(TextValue(command,"url"))) await NewTab(TextValue(command,"url"),true); break;
                case "window-minimize": WindowState = FormWindowState.Minimized; break;
                case "window-maximize": ToggleMaximize(); break;
                case "window-close": Close(); break;
                case "window-drag": if (WindowState != FormWindowState.Maximized) { ReleaseCapture(); SendMessage(Handle,0x00A1,new IntPtr(2),IntPtr.Zero); } break;
            }
        }

        private void RequestPermission(BrowserTab tab, CoreWebView2PermissionRequestedEventArgs e)
        {
            e.State = CoreWebView2PermissionState.Deny; e.SavesInProfile = false;
            Uri origin; if (Program.SmokeMode != null || !e.IsUserInitiated || !Uri.TryCreate(e.Uri,UriKind.Absolute,out origin) || origin.Scheme != "https") return;
            ulong permissionNavigation = tab.NavigationId;
            var deferral = e.GetDeferral();
            BeginInvoke(new Action(delegate
            {
                try
                {
                    if (closing || tab.Closed || tab.Id != activeTabId || tab.NavigationId != permissionNavigation) return;
                    if (MessageBox.Show(this,origin.GetLeftPart(UriPartial.Authority) + " requests permission: " + e.PermissionKind + ".\n\nAllow this site for this session?","Website permission",MessageBoxButtons.YesNo,MessageBoxIcon.Question) == DialogResult.Yes && !closing && !tab.Closed && tab.Id == activeTabId && tab.NavigationId == permissionNavigation) e.State = CoreWebView2PermissionState.Allow;
                }
                finally { deferral.Complete(); }
            }));
        }
        private void OnDownload(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            var source = tabs.FirstOrDefault(t => t.Web == sender);
            if (source != null) source.DownloadNavigationId = source.NavigationId;
            var deferral = e.GetDeferral(); e.Handled = true;
            BeginInvoke(new Action(delegate
            {
                try
                {
                    if (closing) { e.Cancel = true; return; }
                    if (Program.SmokeMode != null)
                    {
                        Uri testUri;
                        if (Program.SmokeMode != "integration" || !Uri.TryCreate(e.DownloadOperation.Uri, UriKind.Absolute, out testUri) || testUri.Host != "127.0.0.1" || testUri.AbsolutePath != "/download") { e.Cancel = true; return; }
                        string testDirectory = Path.Combine(Program.Root, "downloads"); Directory.CreateDirectory(testDirectory);
                        e.ResultFilePath = Path.Combine(testDirectory, "fixture.txt");
                        TrackDownload(e.ResultFilePath, e.DownloadOperation); return;
                    }
                    using (var dialog = new SaveFileDialog { FileName = Path.GetFileName(e.ResultFilePath), Title = "Save download", OverwritePrompt = true })
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK) { e.Cancel = true; return; }
                        e.ResultFilePath = dialog.FileName;
                        TrackDownload(dialog.FileName, e.DownloadOperation);
                    }
                }
                catch (Exception ex) { e.Cancel = true; Program.Log("DOWNLOAD_FAILED " + ex.Message); }
                finally { deferral.Complete(); }
            }));
        }
        private void TrackDownload(string path, CoreWebView2DownloadOperation operation)
        {
            var download = new BrowserDownload { Name = Path.GetFileName(path), Path = path, Operation = operation }; downloads.Add(download);
            operation.BytesReceivedChanged += delegate { if ((DateTime.UtcNow-lastDownloadUpdate).TotalMilliseconds > 400) { lastDownloadUpdate = DateTime.UtcNow; Publish(); } };
            operation.StateChanged += delegate { Publish(); }; Publish(); Toast("Downloading: " + download.Name);
        }
        private void OpenDownload(string id, bool folder)
        {
            var item = downloads.FirstOrDefault(d => d.Id == id); if (item == null) return;
            if (folder) { string directory = Path.GetDirectoryName(item.Path); if (Directory.Exists(directory)) Process.Start(new ProcessStartInfo("explorer.exe","\""+directory+"\"") { UseShellExecute = true }); }
            else if (item.Operation.State == CoreWebView2DownloadState.Completed && File.Exists(item.Path))
            {
                if (MessageBox.Show(this,"Open downloaded file?\n"+item.Name,"Moronco",MessageBoxButtons.YesNo,MessageBoxIcon.Question) == DialogResult.Yes) Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            }
        }
        private void ImportWallpaper()
        {
            using (var dialog = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp", Title = "Choose your wallpaper" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (new FileInfo(dialog.FileName).Length > 25000000) { Toast("Choose an image smaller than 25 MB."); return; }
                using (var original = Image.FromFile(dialog.FileName))
                {
                    if (original.Width > 12000 || original.Height > 12000) { Toast("Choose an image below 12000 px in each dimension."); return; }
                    using (var bitmap = new Bitmap(original)) bitmap.Save(Path.Combine(Program.Root,"themes","wallpaper.jpg"),System.Drawing.Imaging.ImageFormat.Jpeg);
                }
                store.ApplySettings(new Dictionary<string,object> { { "wallpaper","custom" } }); DirtyPublish();
            }
        }
        private void ExportTheme()
        {
            using (var dialog = new SaveFileDialog { Filter = "Moronco theme|*.json", FileName = "moronco-theme.json", Title = "Export appearance settings" })
            { if (dialog.ShowDialog(this) == DialogResult.OK) { File.WriteAllText(dialog.FileName,json.Serialize(SettingsForUi())); Toast("Theme exported; custom images remain stored separately on this device."); } }
        }
        private void ImportTheme()
        {
            using (var dialog = new OpenFileDialog { Filter = "Moronco theme|*.json", Title = "Import appearance settings" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (new FileInfo(dialog.FileName).Length > 500000) { Toast("Theme file exceeds 500 KB."); return; }
                var settings = json.DeserializeObject(File.ReadAllText(dialog.FileName)) as Dictionary<string,object>;
                if (settings == null) { Toast("Invalid theme file."); return; }
                store.ApplySettings(settings); DirtyPublish(); Toast("Appearance settings imported.");
            }
        }
        private async Task SaveScreenshot(BrowserTab tab)
        {
            var web = tab != null && tab.Web != null && !IsInternal(tab.Url) ? tab.Web : chrome.CoreWebView2;
            using (var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "minium-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".png", Title = "Save visible page screenshot" })
            { if (dialog.ShowDialog(this) == DialogResult.OK) using (var output = File.Create(dialog.FileName)) await web.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,output); }
        }
        private async Task ClearBrowsingData()
        {
            if (MessageBox.Show(this,"Clear website cookies and cache? You will be signed out of websites. Bookmarks and appearance settings will be kept.","Clear browsing data",MessageBoxButtons.YesNo,MessageBoxIcon.Question) != DialogResult.Yes) return;
            var web = tabs.Select(t => t.Web).FirstOrDefault(w => w != null);
            if (web == null) { var tab = await NewTab("about:blank",false); web = tab == null ? null : tab.Web; }
            if (web != null) { await web.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.AllSite); Toast("Website data cleared."); }
        }
        private bool privacyClosing, privacyCleaned;
        private async void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (privacyCleaned) { SaveState(); return; }
            if (privacyClosing) { e.Cancel = true; return; }
            if (Program.SmokeMode == null && downloads.Any(d => d.Operation.State == CoreWebView2DownloadState.InProgress))
                if (MessageBox.Show(this,"Downloads are in progress. Close the browser and stop downloading?","Moronco",MessageBoxButtons.YesNo,MessageBoxIcon.Question) != DialogResult.Yes) { e.Cancel = true; return; }
            if (store.Settings.ClearOnExit && protectionController != null)
            {
                e.Cancel = true; privacyClosing = true;
                try
                {
                    var profile = protectionController.CoreWebView2.Profile;
                    CancelDeepSearch();
                    foreach (var tab in tabs.ToArray()) ReleaseTabView(tab);
                    await profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllSite | CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory);
                    if (Program.SmokeMode == "upgrade" && (await profile.CookieManager.GetCookiesAsync("https://privacy.example")).Any(c=>c.Name=="moronco_privacy_probe")) throw new Exception("Privacy fixture cookie survived cleanup.");
                    Program.Log("PRIVACY_CLEANUP_PASS");
                    privacyCleaned = true;
                }
                catch (Exception ex)
                {
                    Program.Log("PRIVACY_CLEANUP_FAILED " + ex.GetType().Name);
                    if (Program.SmokeMode == null) MessageBox.Show(this,"Website data could not be cleared. Moronco has stayed open. Close again to retry.","Privacy cleanup");
                }
                finally { privacyClosing = false; }
                if (privacyCleaned) BeginInvoke(new Action(Close));
                return;
            }
            SaveState();
        }
        private void OnAccelerator(object sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
        {
            if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown && e.KeyEventKind != CoreWebView2KeyEventKind.SystemKeyDown) return;
            Keys key = (Keys)e.VirtualKey | Control.ModifierKeys; if (!IsShortcut(key) && !(key == Keys.Escape && fullscreen)) return;
            e.Handled = true; BeginInvoke(new Action(async delegate { await Shortcut(key); }));
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (IsShortcut(keyData) || (keyData == Keys.Escape && fullscreen)) { BeginInvoke(new Action(async delegate { await Shortcut(keyData); })); return true; }
            return base.ProcessCmdKey(ref msg,keyData);
        }
        private static bool IsShortcut(Keys key)
        {
            return key == (Keys.Control|Keys.L) || key == (Keys.Control|Keys.T) || key == (Keys.Control|Keys.W) || key == (Keys.Control|Keys.Shift|Keys.T)
                || key == (Keys.Control|Keys.Tab) || key == (Keys.Control|Keys.Shift|Keys.Tab) || key == (Keys.Control|Keys.D) || key == (Keys.Control|Keys.H)
                || key == (Keys.Control|Keys.J) || key == (Keys.Control|Keys.F) || key == (Keys.Control|Keys.K) || key == (Keys.Control|Keys.Shift|Keys.P)
                || key == Keys.F11 || key == Keys.F5 || key == (Keys.Control|Keys.R) || key == (Keys.Control|Keys.Alt|Keys.R) || key == (Keys.Alt|Keys.Left) || key == (Keys.Alt|Keys.Right)
                || key == (Keys.Control|Keys.Shift|Keys.O) || key == (Keys.Alt|Keys.D) || key == (Keys.Control|Keys.P) || key == Keys.F12;
        }
        private async Task Shortcut(Keys key)
        {
            try
            {
                if (key == Keys.Escape && fullscreen) { await LeaveFullscreen(); return; }
                if (key != Keys.F11 && key != Keys.F5 && key != (Keys.Control|Keys.R)) await LeaveFullscreen();
                if (key == (Keys.Control|Keys.Alt|Keys.R)) { store.ResetSettings(); overlay = false; LayoutControllers(); DirtyPublish(); Toast("Default appearance restored."); return; }
                if (key == (Keys.Control|Keys.L) || key == (Keys.Alt|Keys.D)) { chrome.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); Post(new { type = "focusAddress" }); }
                else if (key == (Keys.Control|Keys.T)) await NewTab("minium://newtab",true);
                else if (key == (Keys.Control|Keys.W)) CloseTab(activeTabId);
                else if (key == (Keys.Control|Keys.Shift|Keys.T)) await HandleCommand(new Dictionary<string,object> { { "action","tab-reopen" } });
                else if (key == (Keys.Control|Keys.Tab) || key == (Keys.Control|Keys.Shift|Keys.Tab)) { int index = tabs.FindIndex(t => t.Id == activeTabId); await SelectTab(tabs[(index+(key.HasFlag(Keys.Shift) ? tabs.Count-1 : 1))%tabs.Count].Id); }
                else if (key == (Keys.Control|Keys.H)) await NewTab("minium://history",true);
                else if (key == (Keys.Control|Keys.J)) await NewTab("minium://downloads",true);
                else if (key == (Keys.Control|Keys.Shift|Keys.O)) await NewTab("minium://bookmarks",true);
                else if (key == (Keys.Control|Keys.P)) await HandleCommand(new Dictionary<string,object> { { "action","print" } });
                else if (key == Keys.F12) await HandleCommand(new Dictionary<string,object> { { "action","devtools" } });
                else if (key == (Keys.Control|Keys.D)) await HandleCommand(new Dictionary<string,object> { { "action","bookmark-add" } });
                else if (key == (Keys.Control|Keys.F)) { chrome.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); Post(new { type = "find" }); }
                else if (key == (Keys.Control|Keys.K) || key == (Keys.Control|Keys.Shift|Keys.P)) { chrome.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); Post(new { type = "commandPalette" }); }
                else if (key == Keys.F11) { if (fullscreen) await LeaveFullscreen(); else SetFullscreen(true); }
                else if (Active != null && Active.Web != null)
                {
                    if (key == Keys.F5 || key == (Keys.Control|Keys.R)) Active.Web.Reload();
                    if (key == (Keys.Alt|Keys.Left) && Active.Web.CanGoBack) Active.Web.GoBack();
                    if (key == (Keys.Alt|Keys.Right) && Active.Web.CanGoForward) Active.Web.GoForward();
                }
            }
            catch (Exception ex) { Toast("Action could not be completed: "+ex.Message); }
        }
        private void ToggleMaximize() { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; }
        private async Task LeaveFullscreen()
        {
            if (!fullscreen) return;
            var tab = Active;
            if (tab != null && tab.Web != null)
            { try { await tab.Web.ExecuteScriptAsync("if(document.fullscreenElement)document.exitFullscreen().catch(()=>{});"); } catch { } }
            SetFullscreen(false);
        }
        private void SetFullscreen(bool value)
        {
            if (fullscreen == value) return; fullscreen = value;
            if (value) { beforeFullscreen = Bounds; beforeFullscreenState = WindowState; WindowState = FormWindowState.Normal; Bounds = Screen.FromControl(this).Bounds; }
            else { Bounds = beforeFullscreen; WindowState = beforeFullscreenState; } LayoutControllers();
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x84 && !fullscreen && WindowState == FormWindowState.Normal)
            {
                int value = unchecked((int)m.LParam.ToInt64()); var p = PointToClient(new Point((short)(value & 0xffff),(short)(value >> 16)));
                bool left = p.X < 6, right = p.X >= ClientSize.Width-6, top = p.Y < 6, bottom = p.Y >= ClientSize.Height-6;
                if (left || right || top || bottom) m.Result = new IntPtr(top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : 11);
            }
        }
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h,int msg,IntPtr w,IntPtr l);
        private static bool IsWebUrl(string value) { Uri uri; return Uri.TryCreate(value,UriKind.Absolute,out uri) && (uri.Scheme == "https" || uri.Scheme == "http") && String.IsNullOrEmpty(uri.UserInfo); }
        private static bool IsInternal(string value) { return new[] { "minium://deepsearch","minium://newtab","minium://settings","minium://bookmarks","minium://history","minium://downloads","minium://notes" }.Contains(value); }
        private static string InternalTitle(string url) { switch (url) { case "minium://deepsearch": return "Deep Search"; case "minium://settings": return "Settings"; case "minium://bookmarks": return "Bookmarks"; case "minium://history": return "History"; case "minium://downloads": return "Downloads"; case "minium://notes": return "Notes"; default: return "New tab"; } }
        private static string TextValue(IDictionary<string,object> data,string key,string fallback = "") { object value; return data.TryGetValue(key,out value) && value is string ? (string)value : fallback; }
        private static bool Bool(IDictionary<string,object> data,string key) { object value; return data.TryGetValue(key,out value) && value is bool && (bool)value; }
        private static int Number(IDictionary<string,object> data,string key,int fallback) { return (int)Math.Max(-100000,Math.Min(100000,Real(data,key,fallback))); }
        private static double Real(IDictionary<string,object> data,string key,double fallback) { object value; double parsed; return data.TryGetValue(key,out value) && Double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out parsed) && !Double.IsNaN(parsed) && !Double.IsInfinity(parsed) ? parsed : fallback; }
    }
}
