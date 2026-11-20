using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace MiniumBrowser
{
    internal sealed class BrowserTab
    {
        internal string Id = Guid.NewGuid().ToString("N"), Url = "minium://newtab", Title = "New tab";
        internal bool Pinned, Loading, Closed, Initializing;
        internal ulong NavigationId;
        internal ulong DownloadNavigationId;
        internal Panel Surface;
        internal CoreWebView2Controller Controller;
        internal CoreWebView2 Web { get { return Controller == null ? null : Controller.CoreWebView2; } }
    }
    internal sealed class BrowserDownload
    {
        internal string Id = Guid.NewGuid().ToString("N"), Name, Path;
        internal CoreWebView2DownloadOperation Operation;
    }
    internal sealed partial class BrowserHost : Form
    {
        private const string UiOrigin = "https://minium-ui.local";
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4000000 };
        private readonly BrowserStore store = new BrowserStore(Program.Root);
        private readonly List<BrowserTab> tabs = new List<BrowserTab>();
        private readonly List<SavedTab> recentlyClosed = new List<SavedTab>();
        private readonly List<BrowserDownload> downloads = new List<BrowserDownload>();
        private readonly Timer persistTimer = new Timer { Interval = 800 };
        private readonly Timer smokeTimer = new Timer();
        private CoreWebView2Environment contentEnvironment, uiEnvironment;
        private CoreWebView2Controller chrome;
        private readonly Panel chromeSurface = new Panel { Dock = DockStyle.Fill, TabStop = false };
        private Task<CoreWebView2Environment> contentEnvironmentTask;
        private string activeTabId;
        private bool uiReady, closing, dirty, overlay, smokeStarted, pageProbeRunning, fullscreen;
        private Rectangle pageBounds = new Rectangle(60, 92, 1280, 748), beforeFullscreen;
        private FormWindowState beforeFullscreenState;
        private DateTime lastDownloadUpdate = DateTime.MinValue;
        private readonly Icon applicationIcon;
        private BrowserTab Active { get { return tabs.FirstOrDefault(t => t.Id == activeTabId); } }

        public BrowserHost()
        {
            using (var stream = typeof(BrowserHost).Assembly.GetManifestResourceStream("MiniumBrowser.AppIcon.ico"))
            using (var embeddedIcon = new Icon(stream, SystemInformation.IconSize))
                applicationIcon = (Icon)embeddedIcon.Clone();
            Icon = applicationIcon;
            if (Program.ResetUi) { store.ResetSettings(); store.Save(); }
            if (!File.Exists(Path.Combine(Program.Root, "browser-state.json")) && store.Bookmarks.Count == 0 && String.IsNullOrEmpty(store.RecoveryMessage))
            {
                store.AddBookmark("https://www.youtube.com/", "YouTube");
                store.AddBookmark("https://discord.com/app", "Discord");
                store.AddBookmark("https://anilist.co/", "AniList");
                store.AddBookmark("https://vi.wikipedia.org/", "Wikipedia");
            }
            Text = "Moronco"; ClientSize = new Size(1340, 840); MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(15, 14, 18);
            FormBorderStyle = FormBorderStyle.None; MaximizedBounds = Screen.FromControl(this).WorkingArea;
            Controls.Add(chromeSurface);
            persistTimer.Tick += delegate { if (dirty) SaveState(); }; persistTimer.Start();
            Shown += async delegate
            {
                if (Program.SmokeMode != null && !Program.ShowTestWindow) { ShowInTaskbar = false; Hide(); }
                try { await Initialize(); }
                catch (Exception ex) { Program.Log("INITIALIZE_FAILED " + ex); if (Program.SmokeMode != null) Close(); else MessageBox.Show(this, ex.Message, "Moronco"); }
                AutoUpdater.CheckInBackground(this);
            };
            Resize += delegate { LayoutControllers(); };
            Move += delegate { if (chrome != null) chrome.NotifyParentWindowPositionChanged(); foreach (var tab in tabs) if (tab.Controller != null) tab.Controller.NotifyParentWindowPositionChanged(); };
            FormClosing += OnClosing;
            FormClosed += delegate
            {
                closing = true; persistTimer.Dispose(); smokeTimer.Dispose(); protectionTimer.Dispose();
                foreach (var tab in tabs.ToArray()) DisposeTab(tab);
                if (protectionController != null) protectionController.Close();
                if (protectionSurface != null) protectionSurface.Dispose();
                if (chrome != null) { chrome.Close(); chrome = null; }
                applicationIcon.Dispose();
            };
        }

        private async Task Initialize()
        {
            string uiDirectory = Path.Combine(SingleFileBootstrap.PayloadDirectory ?? AppDomain.CurrentDomain.BaseDirectory, "ui");
            if (!File.Exists(Path.Combine(uiDirectory, "index.html"))) throw new FileNotFoundException("Browser interface files are missing.", uiDirectory);
            uiEnvironment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.Root, "ui-profile"), new CoreWebView2EnvironmentOptions { Language = "en-US" });
            if (closing) return;
            chrome = await uiEnvironment.CreateCoreWebView2ControllerAsync(chromeSurface.Handle);
            if (closing) { chrome.Close(); return; }
            chrome.Bounds = chromeSurface.ClientRectangle; chrome.IsVisible = true;
            var ui = chrome.CoreWebView2;
            ui.Settings.AreDefaultContextMenusEnabled = false; ui.Settings.IsStatusBarEnabled = false;
            ui.Settings.AreDevToolsEnabled = Program.SmokeMode != null; ui.Settings.IsZoomControlEnabled = false;
            ui.SetVirtualHostNameToFolderMapping("minium-ui.local", uiDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            string themes = Path.Combine(Program.Root, "themes"); Directory.CreateDirectory(themes);
            ui.SetVirtualHostNameToFolderMapping("minium-theme.local", themes, CoreWebView2HostResourceAccessKind.DenyCors);
            ui.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) { if (e.Uri != UiOrigin + "/index.html") e.Cancel = true; };
            ui.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; };
            ui.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs e) { e.State = CoreWebView2PermissionState.Deny; };
            ui.DownloadStarting += delegate(object sender, CoreWebView2DownloadStartingEventArgs e) { e.Cancel = true; };
            ui.WebMessageReceived += delegate(object sender, CoreWebView2WebMessageReceivedEventArgs e)
            {
                if (closing || e.Source != UiOrigin + "/index.html") return;
                string raw = e.WebMessageAsJson; if (raw.Length > 500000) return;
                BeginInvoke(new Action(async delegate
                {
                    try { var command = json.DeserializeObject(raw) as Dictionary<string, object>; if (command != null) await HandleCommand(command); }
                    catch (Exception ex) { Program.Log("COMMAND_FAILED " + ex.GetType().Name + " " + ex.Message); Toast("Could not complete action: " + ex.Message); }
                }));
            };
            chrome.AcceleratorKeyPressed += OnAccelerator;
            if ((Program.SmokeMode == null || Program.SmokeMode == "restore") && store.Settings.RestoreSession)
            {
                foreach (var saved in store.SessionTabs.Take(50)) tabs.Add(new BrowserTab { Id = saved.Id, Url = saved.Url, Title = saved.Title, Pinned = saved.Pinned });
                activeTabId = tabs.Any(t => t.Id == store.ActiveTabId) ? store.ActiveTabId : (tabs.Count > 0 ? tabs[0].Id : null);
            }
            if (tabs.Count == 0) { var tab = new BrowserTab(); tabs.Add(tab); activeTabId = tab.Id; }
            StartProtection();
            ui.Navigate(UiOrigin + "/index.html"); Program.Log("RUNTIME " + uiEnvironment.BrowserVersionString);
        }

        private async Task NavigateInput(string input)
        {
            await LeaveFullscreen();
            string url, error;
            if (IsInternal(input)) url = input;
            else
            {
                bool isSearch;
                if (!AddressInput.TryResolve(input, out url, out error, out isSearch)) { Toast(error); return; }
                if (isSearch) url = SearchUrl(input.Trim(), store.Settings.SearchEngine, store.Settings.SearxInstance);
            }
            var tab = Active; if (tab == null) { await NewTab(url, true); return; }
            tab.Url = url; tab.Title = IsInternal(url) ? InternalTitle(url) : "Loading…";
            if (IsInternal(url))
            {
                ReleaseTabView(tab);
                tab.Loading = false; LayoutControllers(); DirtyPublish();
            }
            else if (tab.Controller == null) await EnsureTab(tab);
            else { tab.Loading = true; tab.Web.Navigate(url); LayoutControllers(); DirtyPublish(); }
        }

        private async Task<BrowserTab> NewTab(string url, bool select, bool navigate = true)
        {
            if (select) await LeaveFullscreen();
            if (tabs.Count >= 50) { Toast("50 tabs are open. Close some tabs before opening more."); return null; }
            if (!IsInternal(url) && !IsWebUrl(url) && url != "about:blank") url = "minium://newtab";
            var tab = new BrowserTab { Url = url, Title = IsInternal(url) ? InternalTitle(url) : "Loading…" }; tabs.Add(tab);
            if (select) { activeTabId = tab.Id; overlay = false; }
            DirtyPublish(); LayoutControllers(); if (!IsInternal(url)) await EnsureTab(tab, navigate); return tab;
        }
        private async Task SelectTab(string id)
        {
            await LeaveFullscreen();
            var tab = tabs.FirstOrDefault(t => t.Id == id); if (tab == null) return;
            activeTabId = id; overlay = false; DirtyPublish(); LayoutControllers(); if (!IsInternal(tab.Url)) await EnsureTab(tab);
        }

        private async Task EnsureTab(BrowserTab tab, bool navigate = true)
        {
            if (closing || privacyClosing || privacyCleaned || tab.Closed || tab.Initializing || tab.Controller != null || IsInternal(tab.Url)) return;
            tab.Initializing = true; tab.Loading = true; Publish();
            Panel initializingSurface = null;
            bool restartInitialization = false;
            try
            {
                await EnsureAdBlocker();
                contentEnvironment = await ContentEnvironment(); if (closing || privacyClosing || privacyCleaned || tab.Closed || IsInternal(tab.Url)) return;
                // Each WebView owns a distinct HWND. Sharing the opaque chrome's
                // parent lets its compositor cover pages even after successful loads.
                tab.Surface = new Panel { Bounds = pageBounds, BackColor = BackColor, TabStop = false, Visible = false };
                Controls.Add(tab.Surface);
                initializingSurface = tab.Surface;
                var controller = await contentEnvironment.CreateCoreWebView2ControllerAsync(initializingSurface.Handle);
                try { await EnsureSessionPermissions(controller.CoreWebView2.Profile); }
                catch { controller.Close(); throw; }
                if (closing || privacyClosing || privacyCleaned || tab.Closed || initializingSurface.IsDisposed || tab.Surface != initializingSurface || IsInternal(tab.Url))
                {
                    controller.Close();
                    restartInitialization = !closing && !tab.Closed && !IsInternal(tab.Url);
                    return;
                }
                tab.Controller = controller; var web = controller.CoreWebView2;
                ApplySearchPreferences(web, tab.Url);
                controller.Bounds = tab.Surface.ClientRectangle; controller.IsVisible = false;
                controller.AcceleratorKeyPressed += OnAccelerator; controller.ZoomFactorChanged += delegate { Publish(); };
                web.Settings.AreDevToolsEnabled = true; web.Settings.AreDefaultContextMenusEnabled = true;
                web.Settings.IsStatusBarEnabled = true;
                web.Settings.IsPasswordAutosaveEnabled = false;
                web.Settings.IsGeneralAutofillEnabled = false;
                web.Settings.IsReputationCheckingRequired = true;
                web.Settings.AreHostObjectsAllowed = false;
                web.Settings.IsWebMessageEnabled = false;
                web.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced;
                web.ServerCertificateErrorDetected += delegate(object sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
                { e.Action = CoreWebView2ServerCertificateErrorAction.Cancel; };
                web.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
                {
                    if (!IsWebUrl(e.Uri) && e.Uri != "about:blank") { e.Cancel = true; Toast("This address uses an unsupported protocol."); return; }
                    ApplySearchPreferences(web, e.Uri);
                    tab.NavigationId = e.NavigationId; tab.Url = e.Uri; tab.Loading = true; DirtyPublish(); LayoutControllers();
                };
                web.SourceChanged += delegate { if (!tab.Closed && !closing) { tab.Url = web.Source; DirtyPublish(); } };
                web.DocumentTitleChanged += delegate { if (!tab.Closed && !closing) { tab.Title = web.DocumentTitle; DirtyPublish(); } };
                web.HistoryChanged += delegate { Publish(); }; web.IsMutedChanged += delegate { Publish(); }; web.IsDocumentPlayingAudioChanged += delegate { Publish(); };
                web.NavigationCompleted += async delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
                {
                    if (tab.Closed || closing) return;
                    Program.Log("NAVIGATION success=" + e.IsSuccess + " status=" + e.WebErrorStatus + " tab=" + tab.Id + " navigation=" + e.NavigationId + " http=" + e.HttpStatusCode);
                    if (e.NavigationId != tab.NavigationId) return;
                    tab.Loading = false; tab.Url = web.Source;
                    if (e.IsSuccess)
                    {
                        Program.NavigationSucceeded = true; if (IsWebUrl(tab.Url)) store.AddHistory(tab.Url, web.DocumentTitle);
                        if (Program.SmokeMode == "browse" || Program.SmokeMode == "blank") await ProbePage(tab);
                    }
                    else if (tab.Id == activeTabId && e.NavigationId != tab.DownloadNavigationId && ShouldReportNavigationError(e.WebErrorStatus, e.HttpStatusCode)) Toast("Could not load page: " + e.WebErrorStatus + ". Reload to try again.");
                    DirtyPublish();
                };
                web.NewWindowRequested += async delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e)
                {
                    e.Handled = true; if (!e.IsUserInitiated || (!IsWebUrl(e.Uri) && e.Uri != "about:blank")) return;
                    var deferral = e.GetDeferral();
                    try { var popup = await NewTab("about:blank", true, false); if (popup != null && popup.Web != null) e.NewWindow = popup.Web; }
                    catch (Exception ex) { Program.Log("POPUP_FAILED " + ex.Message); }
                    finally { deferral.Complete(); }
                };
                web.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs e) { RequestPermission(tab, e); };
                web.DownloadStarting += OnDownload;
                web.ContainsFullScreenElementChanged += delegate { if (!closing && tab.Id == activeTabId) SetFullscreen(web.ContainsFullScreenElement); };
                web.ProcessFailed += delegate(object sender, CoreWebView2ProcessFailedEventArgs e)
                {
                    Program.Log("PROCESS_FAILED " + e.ProcessFailedKind + " " + e.Reason);
                    if (closing || privacyClosing || privacyCleaned || tab.Closed) return;
                    if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited || e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                    { tab.Loading = false; Toast("This tab encountered an error. Close it and reopen the address."); Publish(); }
                };
                // Navigation may have changed while startup was awaiting the environment.
                if (IsInternal(tab.Url)) { ReleaseTabView(tab); tab.Loading = false; }
                else if (navigate) web.Navigate(tab.Url);
            }
            catch (Exception ex)
            {
                bool superseded = closing || privacyClosing || privacyCleaned || tab.Closed || (initializingSurface != null && (initializingSurface.IsDisposed || tab.Surface != initializingSurface));
                tab.Loading = false; ReleaseTabView(tab);
                if (superseded) restartInitialization = !closing && !tab.Closed && !IsInternal(tab.Url);
                else { Toast("Could not open tab: " + ex.Message); Program.Log("TAB_FAILED " + ex); }
            }
            finally
            {
                // Keep a canceled creation's HWND alive until WebView2 completes
                // its callback; reusing it early can abort the replacement view.
                if (initializingSurface != null && initializingSurface != tab.Surface && !initializingSurface.IsDisposed) initializingSurface.Dispose();
                tab.Initializing = false; LayoutControllers(); Publish();
                if (restartInitialization) BeginInvoke(new Action(async delegate { await EnsureTab(tab); }));
            }
        }

        private void CloseTab(string id)
        {
            var tab = tabs.FirstOrDefault(t => t.Id == id); if (tab == null) return; int index = tabs.IndexOf(tab);
            recentlyClosed.Insert(0, Saved(tab)); if (recentlyClosed.Count > 20) recentlyClosed.RemoveAt(20);
            DisposeTab(tab); tabs.Remove(tab); if (tabs.Count == 0) tabs.Add(new BrowserTab());
            if (activeTabId == id) activeTabId = tabs[Math.Min(index, tabs.Count - 1)].Id;
            if (fullscreen) SetFullscreen(false); overlay = false; LayoutControllers(); DirtyPublish();
            var next = Active; if (next != null && !IsInternal(next.Url) && next.Controller == null) BeginInvoke(new Action(async delegate { await EnsureTab(next); }));
        }
        private static bool ShouldReportNavigationError(CoreWebView2WebErrorStatus error, int httpStatus)
        {
            // A page may call window.stop() after successfully receiving HTTP 200.
            return error != CoreWebView2WebErrorStatus.OperationCanceled && !(error == CoreWebView2WebErrorStatus.ConnectionAborted && httpStatus >= 200 && httpStatus < 400);
        }
        private void ReleaseTabView(BrowserTab tab)
        {
            bool pendingCreation = tab.Initializing && tab.Controller == null;
            if (tab.Controller != null) { try { tab.Controller.Close(); } catch { } tab.Controller = null; }
            if (tab.Surface != null)
            {
                tab.Surface.Visible = false;
                if (!pendingCreation) tab.Surface.Dispose();
                tab.Surface = null;
            }
        }
        private void DisposeTab(BrowserTab tab) { tab.Closed = true; ReleaseTabView(tab); }
        private void LayoutControllers()
        {
            if (closing) return;
            if (chrome != null) { chrome.Bounds = chromeSurface.ClientRectangle; chrome.IsVisible = !fullscreen || Active == null || IsInternal(Active.Url); }
            foreach (var tab in tabs) if (tab.Controller != null && tab.Surface != null)
            {
                bool visible = tab.Id == activeTabId && !IsInternal(tab.Url) && (!overlay || fullscreen) && WindowState != FormWindowState.Minimized;
                tab.Surface.Bounds = fullscreen ? ClientRectangle : pageBounds;
                tab.Controller.Bounds = tab.Surface.ClientRectangle;
                tab.Controller.IsVisible = visible;
                tab.Surface.Visible = visible;
                if (visible) tab.Surface.BringToFront();
                tab.Controller.NotifyParentWindowPositionChanged();
            }
        }

        private void Publish()
        {
            if (!uiReady || closing || chrome == null) return;
            try
            {
                Post(new { type = "state", activeTabId = activeTabId,
                    tabs = tabs.Select(t => new { id = t.Id, title = t.Title, url = t.Url, loading = t.Loading, pinned = t.Pinned,
                        muted = t.Web != null && t.Web.IsMuted, audio = t.Web != null && t.Web.IsDocumentPlayingAudio,
                        canGoBack = t.Web != null && t.Web.CanGoBack, canGoForward = t.Web != null && t.Web.CanGoForward, zoom = t.Controller == null ? 1 : t.Controller.ZoomFactor }).ToArray(),
                    vpn = new { status = vpnStatus, message = vpnMessage, busy = vpnBusy },
                    adblock = new { status = adBlockStatus, message = adBlockMessage, busy = adBlockBusy },
                    deepSearch = deepSearch, settings = SettingsForUi(), bookmarks = store.Bookmarks.Select(b => new { id = b.Id, title = b.Title, url = b.Url }).ToArray(),
                    history = store.History.Take(2000).Select(h => new { id = h.Id, title = h.Title, url = h.Url, visitedAt = h.VisitedAt }).ToArray(),
                    downloads = downloads.Select(d => new { id = d.Id, name = d.Name, path = d.Path, received = d.Operation.BytesReceived, total = d.Operation.TotalBytesToReceive,
                        state = d.Operation.State == CoreWebView2DownloadState.Completed ? "completed" : d.Operation.State == CoreWebView2DownloadState.Interrupted ? (d.Operation.CanResume ? "paused" : "interrupted") : "downloading" }).ToArray(), notes = store.Notes });
                var active = Active; Text = "Moronco" + (active == null ? "" : " · " + active.Title);
            }
            catch (Exception ex) { Program.Log("STATE_FAILED " + ex.GetType().Name + " " + ex.Message); }
        }
        private object SettingsForUi()
        {
            var s = store.Settings; string wallpaper = Path.Combine(Program.Root, "themes", "wallpaper.jpg");
            return new { accent = s.Accent, theme = s.Theme, wallpaper = s.Wallpaper, wallpaperOpacity = s.WallpaperOpacity,
                wallpaperBlur = s.WallpaperBlur, tabLayout = s.TabLayout, compact = s.Compact, sidebar = s.Sidebar, bookmarkBar = s.BookmarkBar,
                searchEngine = s.SearchEngine, searxInstance = s.SearxInstance, saveHistory = s.SaveHistory, clearOnExit = s.ClearOnExit, restoreSession = s.RestoreSession, customCss = s.CustomCss, homePage = s.HomePage,
                customWallpaperUrl = "https://minium-theme.local/wallpaper.jpg?v=" + (File.Exists(wallpaper) ? File.GetLastWriteTimeUtc(wallpaper).Ticks : 0) };
        }
        private void Post(object message) { if (!closing && chrome != null) chrome.CoreWebView2.PostWebMessageAsJson(json.Serialize(message)); }
        private void Toast(string text) { if (uiReady) Post(new { type = "toast", text = text }); }
        private void DirtyPublish() { dirty = true; Publish(); }
        private static SavedTab Saved(BrowserTab tab) { return new SavedTab { Id = tab.Id, Url = tab.Url, Title = tab.Title, Pinned = tab.Pinned }; }
        private void SaveState()
        {
            try { store.SessionTabs = tabs.Select(Saved).ToList(); store.ActiveTabId = activeTabId; store.Save(); dirty = false; }
            catch (Exception ex) { Program.Log("SAVE_FAILED " + ex.Message); Toast("Could not save settings. Check data folder write permissions."); dirty = false; }
        }
    }
}
