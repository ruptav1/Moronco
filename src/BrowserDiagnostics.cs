using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace MiniumBrowser
{
    internal sealed partial class BrowserHost
    {
        private void AssertPageHosting(BrowserTab tab, string stage)
        {
            if (tab == null || tab.Surface == null || tab.Controller == null || !tab.Controller.IsVisible || overlay) throw new Exception(stage + ": active web view is not visible.");
            if (chrome.ParentWindow != chromeSurface.Handle || tab.Controller.ParentWindow != tab.Surface.Handle || tab.Controller.ParentWindow == chrome.ParentWindow) throw new Exception(stage + ": WebViews share or use the wrong parent HWND.");
            if (tab.Surface.Bounds != (fullscreen ? ClientRectangle : pageBounds) || tab.Controller.Bounds != tab.Surface.ClientRectangle) throw new Exception(stage + ": content coordinates are not local to its native surface.");
            if (GetWindow(Handle, 5) != tab.Surface.Handle) throw new Exception(stage + ": opaque browser UI is above the content surface in native z-order.");
            var point = new Point(tab.Surface.Left + tab.Surface.Width / 2, tab.Surface.Top + tab.Surface.Height / 2);
            if (ChildWindowFromPointEx(Handle, point, 1) != tab.Surface.Handle) throw new Exception(stage + ": native content hit-test is covered by another surface.");
            Program.Log("HOSTING_PASS " + stage);
        }
        private void CaptureWindowForDiagnosis()
        {
            if (!Program.ShowTestWindow) return;
            using (var bitmap = new Bitmap(Width, Height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                IntPtr dc = graphics.GetHdc();
                try { if (!PrintWindow(Handle, dc, 2)) throw new Exception("Native window capture failed."); }
                finally { graphics.ReleaseHdc(dc); }
                bitmap.Save(Path.ChangeExtension(Program.LogPath, ".window.png"), System.Drawing.Imaging.ImageFormat.Png);
                Color pixel = bitmap.GetPixel(pageBounds.Left + pageBounds.Width / 2, pageBounds.Top + pageBounds.Height / 2);
                if (Math.Abs(pixel.R - 231) > 3 || Math.Abs(pixel.G - 246) > 3 || Math.Abs(pixel.B - 238) > 3) throw new Exception("Native window pixels do not show the fixture background: " + pixel);
                Program.Log("COMPOSITE_PIXELS_PASS native window shows the website surface");
            }
        }
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern IntPtr ChildWindowFromPointEx(IntPtr window, Point point, uint flags);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        private async Task ProbePage(BrowserTab tab)
        {
            if (pageProbeRunning || tab.Web == null) return; pageProbeRunning = true;
            try
            {
                string probe = "null";
                for (int i = 0; i < 50 && !closing && !tab.Closed; i++)
                {
                    await Task.Delay(200); if (closing || tab.Closed || tab.Web == null) return;
                    probe = await tab.Web.ExecuteScriptAsync("document.readyState==='complete'&&(location.href==='about:blank'||(document.body&&document.body.innerText.length>=30))?JSON.stringify({title:document.title,url:location.href,ready:document.readyState,bodyLength:document.body?document.body.innerText.length:0,searchQuery:(document.querySelector('textarea[name=q],input[name=q]')||{}).value||null,linkCount:document.links.length}):null");
                    if (probe != "null") break;
                }
                Program.Log("PAGE_PROBE "+probe);
                if (probe != "null" && Program.SmokeMode == "browse" && !closing) BeginInvoke(new Action(Close));
            }
            catch (Exception ex) { Program.Log("PAGE_PROBE_FAILED "+ex.Message); }
            finally { pageProbeRunning = false; }
        }
        private async void RunSmoke()
        {
            smokeTimer.Interval = Program.SmokeSeconds*1000; smokeTimer.Tick += delegate { smokeTimer.Stop(); Close(); }; smokeTimer.Start();
            try
            {
                await Task.Delay(2500); if (closing) return;
                Program.Log("UI_PROBE "+await chrome.CoreWebView2.ExecuteScriptAsync("window.minium&&window.minium.snapshot?window.minium.snapshot():({title:document.title,bodyLength:document.body.innerText.length})"));
                if (Program.SmokeMode == "upgrade") { await ExerciseUpgrade(); return; }
                if (Program.SmokeMode == "deep") { await ExerciseDeepSearch(); return; }
                if (Program.SmokeMode == "search") { await ExerciseSearch(); return; }
                if (Program.SmokeMode == "restore")
                {
                    if (tabs.Count < 3 || store.Settings.TabLayout != "vertical" || !store.Bookmarks.Any(b => b.Title == "Example <safe> & test") || !store.Notes.Contains("Ghi chú thử")) throw new Exception("Persisted UI state did not restore.");
                    Program.SmokeSucceeded = true; Program.Log("UI_RESTORE_PASS"); Close(); return;
                }
                if (Program.SmokeMode == "integration") { await ExerciseBrowser(); return; }
                if (Program.SmokeMode == "ui" || Program.SmokeMode == "native")
                {
                    using (var file = File.Create(Path.ChangeExtension(Program.LogPath,".png"))) await chrome.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
                    if (Program.SmokeMode == "ui") await ExerciseUi();
                }
            }
            catch (Exception ex) { Program.Log("SMOKE_FAILED "+ex); }
        }
        private async Task ExerciseUi()
        {
            store.Settings.RestoreSession=true;
            string home = await chrome.CoreWebView2.ExecuteScriptAsync("JSON.stringify({choices:document.querySelectorAll('.collection-item,.wallpaper-title,.wallpaper-byline').length,background:document.querySelector('.home-art').style.backgroundImage,buttons:!!document.querySelector('#vpn-button')&&!!document.querySelector('#adblock-button')})");
            if (!home.Contains("moronco-wallpaper.png") || !home.Contains("\\\"choices\\\":0") || !home.Contains("\\\"buttons\\\":true")) throw new Exception("Home redesign is incomplete: " + home);
            if (adBlocker == null || !adBlocker.IsEnabled) throw new Exception("Ad blocker did not initialize.");
            await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#adblock-button').click();document.querySelector('#adblock-toggle').click()");
            await WaitFor(delegate { return !adBlockBusy && !adBlocker.IsEnabled && !store.Settings.AdBlockEnabled; }, "Ad blocker UI did not disable extension.");
            await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#adblock-toggle').click()");
            await WaitFor(delegate { return !adBlockBusy && adBlocker.IsEnabled && store.Settings.AdBlockEnabled; }, "Ad blocker UI did not enable extension.");
            await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('.dialog-header button').click();document.querySelector('#vpn-button').click()");
            if ((await chrome.CoreWebView2.ExecuteScriptAsync("!!document.querySelector('#vpn-detail') && !!document.querySelector('#vpn-toggle')")) != "true") throw new Exception("VPN controls missing.");
            await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('.dialog-header button').click()");
            Program.Log("PROTECTION_UI_PASS adblock=" + adBlockStatus + " vpn=" + vpnStatus);
            await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-page=settings]').click()");
            await WaitFor(delegate { return Active != null && Active.Url == "minium://settings"; }, "Settings button did not open its page.");
            await Task.Delay(200);
            await chrome.CoreWebView2.ExecuteScriptAsync("(()=>{const c=document.querySelector('[data-setting=accent]');c.value='#ab344a';c.dispatchEvent(new Event('change'));})()");
            await WaitFor(delegate { return store.Settings.Accent == "#ab344a"; }, "Accent control did not update host settings.");
            store.ApplySettings(new Dictionary<string,object> { { "accent","#ab344a" },{ "tabLayout","vertical" } });
            store.AddBookmark("https://example.com/","Example <safe> & test"); store.Notes = "Ghi chú thử\nTiếng Việt <script> không chạy";
            DirtyPublish(); await Task.Delay(650);
            Program.Log("UI_SETTINGS_PROBE "+await chrome.CoreWebView2.ExecuteScriptAsync("window.minium.snapshot()"));
            using (var file = File.Create(Path.ChangeExtension(Program.LogPath,".settings.png"))) await chrome.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
            var scratch = await NewTab("about:blank",true); await Task.Delay(800);
            if (scratch == null || scratch.Controller == null) throw new Exception("New web tab failed to initialize.");
            await ProbeAdFiltering(scratch);
            CloseTab(scratch.Id); await HandleCommand(new Dictionary<string,object> { { "action","tab-reopen" } });
            if (Active == null || Active.Controller == null || Active.Url != "about:blank") throw new Exception("Closed tab did not reopen.");
            await Task.Delay(800); await SelectTab(tabs[0].Id); SaveState();
            Program.SmokeSucceeded = true;
            Program.Log("UI_ACTIONS_PASS tabs="+tabs.Count+" bookmarks="+store.Bookmarks.Count);
        }

        private async Task ExerciseUpgrade()
        {
            await protectionController.CoreWebView2.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Geolocation,"https://example.org",CoreWebView2PermissionState.Allow);
            var webTab = await NewTab("about:blank",false);
            var privacyCookie = webTab.Web.CookieManager.CreateCookie("moronco_privacy_probe","erase-me","privacy.example","/");
            webTab.Web.CookieManager.AddOrUpdateCookie(privacyCookie);
            if (!(await webTab.Web.CookieManager.GetCookiesAsync("https://privacy.example")).Any(c=>c.Name=="moronco_privacy_probe")) throw new Exception("Privacy fixture cookie missing.");
            if ((await webTab.Web.Profile.GetNonDefaultPermissionSettingsAsync()).Any()) throw new Exception("Saved permissions were not reset.");
            if (webTab.Web.Settings.IsPasswordAutosaveEnabled || webTab.Web.Settings.IsGeneralAutofillEnabled || webTab.Web.Settings.IsWebMessageEnabled || webTab.Web.Settings.AreHostObjectsAllowed || !webTab.Web.Settings.IsReputationCheckingRequired || webTab.Web.Profile.PreferredTrackingPreventionLevel != CoreWebView2TrackingPreventionLevel.Balanced) throw new Exception("Protection settings not applied.");
            CloseTab(webTab.Id);
            ClientSize = new System.Drawing.Size(900,600);
            for (int i=1;i<50;i++) await NewTab("minium://newtab",true);
            await Task.Delay(400);
            string check = await chrome.CoreWebView2.ExecuteScriptAsync(@"(()=>{const s=document.querySelector('#horizontal-tabs'),a=s.querySelector('.active'),r=s.getBoundingClientRect(),v=a.getBoundingClientRect(),n=document.querySelector('.new-tab-button').getBoundingClientRect(),w=document.querySelector('.window-controls').getBoundingClientRect();return s.children.length===50&&s.scrollWidth>s.clientWidth&&v.left>=r.left-1&&v.right<=r.right+1&&n.right<=w.left&&w.right<=innerWidth;})()");
            if(check!="true") throw new Exception("50-tab overflow or active visibility failed.");
            await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#horizontal-tabs').scrollLeft=0");
            Publish(); await Task.Delay(200);
            if(await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#horizontal-tabs').scrollLeft===0")!="true") throw new Exception("Manual tab scroll was reset.");
            await SelectTab(tabs[0].Id); await Task.Delay(200);
            using(var file=File.Create(Path.ChangeExtension(Program.LogPath,".tabs.png"))) await chrome.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
            store.Settings.TabLayout="vertical"; Publish(); await Task.Delay(200);
            await SelectTab(tabs[tabs.Count-1].Id); await Task.Delay(200);
            if(await chrome.CoreWebView2.ExecuteScriptAsync("(()=>{const s=document.querySelector('#vertical-tabs'),a=s.querySelector('.active').getBoundingClientRect(),r=s.getBoundingClientRect();return a.top>=r.top-1&&a.bottom<=r.bottom+1;})()")!="true") throw new Exception("Vertical active tab is hidden.");
            if(await chrome.CoreWebView2.ExecuteScriptAsync("document.documentElement.lang==='en'&&!/[à-ỹÀ-Ỹ]/u.test(document.querySelector('#titlebar').innerText)")!="true") throw new Exception("English shell missing.");
            Program.SmokeSucceeded=true; Program.Log("UPGRADE_PASS 50 tabs, horizontal/vertical visibility, scroll retention, English shell and protection settings"); Close();
        }

        private async Task ExerciseDeepSearch()
        {
            var fixture = await NewTab("about:blank",false);
            await WaitFor(delegate { return fixture.Web != null && !fixture.Loading; }, "Fixture view missing.");
            await fixture.Web.ExecuteScriptAsync("document.body.innerHTML='<div class=\"snippet\" data-type=\"web\"><a href=\"https://example.org/item#one\"><div class=\"search-snippet-title\">Title &lt;safe&gt;</div></a><div class=\"generic-snippet\"><div class=\"content\">Description</div></div></div><article class=\"result\"><h3><a href=\"https://example.org/item#two\">Same</a></h3><p class=\"content\">Second</p></article><article class=\"result\"><h3><a href=\"javascript:alert(1)\">Unsafe</a></h3></article>';");
            var first = json.Deserialize<DeepResult[]>(await fixture.Web.ExecuteScriptAsync(DeepExtractScript("Brave","about:blank")));
            var second = json.Deserialize<DeepResult[]>(await fixture.Web.ExecuteScriptAsync(DeepExtractScript("SearXNG","about:blank")));
            if (first.Length != 1 || second.Length != 1 || first[0].title != "Title <safe>") throw new Exception("Deep extraction or unsafe URL filtering failed.");
            if (json.Deserialize<DeepResult[]>(await fixture.Web.ExecuteScriptAsync(DeepExtractScript("Brave","https://example.com/search?q=other"))).Length != 0) throw new Exception("Wrong document was imported.");
            var merged = MergeDeepResults(new[] { new DeepSource {name="Brave",results=first.ToList()}, new DeepSource {name="SearXNG",results=second.ToList()} });
            if (merged.Count != 1 || merged[0].sources.Count != 2) throw new Exception("Deep deduplication failed.");
            CloseTab(fixture.Id);
            await NavigateInput("minium://deepsearch");
            await Task.Delay(350);
            await chrome.CoreWebView2.ExecuteScriptAsync("(()=>{const q=document.querySelector('#deep-form input[name=text]');q.value='searxng';q.dispatchEvent(new Event('input'));document.querySelector('#deep-form').dispatchEvent(new Event('submit',{cancelable:true}));})()");
            await WaitFor(delegate { return deepSearch.query == "searxng"; }, "Deep UI did not submit.");
            var until = DateTime.UtcNow.AddSeconds(40);
            while (deepSearch.busy && DateTime.UtcNow < until) await Task.Delay(250);
            if (deepSearch.busy || deepSearch.sources.Count != 2) throw new Exception("Deep search did not finish.");
            Program.Log("DEEP_LIVE " + json.Serialize(deepSearch));
            int firstCount=deepSearch.results.Count;
            await MoreDeepSearch();
            if(deepSearch.page!=2 || deepSearch.results.Count<firstCount) throw new Exception("Pagination lost results.");
            Program.Log("DEEP_PAGE_TWO " + json.Serialize(deepSearch));
            await Task.Delay(400);
            using (var file = File.Create(Path.ChangeExtension(Program.LogPath,".deep.png"))) await chrome.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
            await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('.deep-results h2').scrollIntoView()");
            await Task.Delay(200);
            using (var file = File.Create(Path.ChangeExtension(Program.LogPath,".results.png"))) await chrome.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
            Program.SmokeSucceeded = true; Program.Log("DEEP_PASS extraction, unsafe links, origin/query guard, deduplication, UI submission and completion"); Close();
        }

        private async Task ExerciseSearch()
        {
            const string query = "C++ tài liệu & # exact";
            await chrome.CoreWebView2.ExecuteScriptAsync("(()=>{const q=document.querySelector('#home-query');q.value=" + json.Serialize(query) + ";q.dispatchEvent(new Event('input'));const engine=document.querySelector('#home-engine');engine.value='searxng';engine.dispatchEvent(new Event('change'));})()");
            await WaitFor(delegate { return store.Settings.SearchEngine == "searxng"; }, "Search engine selection did not persist.");
            await Task.Delay(300);
            if (json.Deserialize<string>(await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#home-query').value")) != query) throw new Exception("Changing engine lost the query.");
            // Test routing in real WebViews; external service availability is checked separately.
            await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#search-both-home').click()");
            string braveUrl = SearchUrl(query, "brave", store.Settings.SearxInstance);
            string searxUrl = SearchUrl(query, "searxng", store.Settings.SearxInstance);
            await WaitFor(delegate { return tabs.Count == 3 && tabs.Any(t => t.Url == braveUrl) && tabs.Any(t => t.Url == searxUrl); }, "Search both did not create two matching-query tabs.");
            var brave = tabs.First(t => t.Url == braveUrl);
            await WaitFor(delegate { return brave.Web != null; }, "Brave WebView missing.");
            var cookies = await brave.Web.CookieManager.GetCookiesAsync("https://search.brave.com/");
            if (!cookies.Any(c => c.Name == "summarizer" && c.Value == "0")) throw new Exception("Brave AI preference was not applied.");
            await Task.Delay(500);
            await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#address').value='second query';document.querySelector('#search-both-address').click()");
            await WaitFor(delegate { return tabs.Count == 5 && tabs.Any(t => t.Url == SearchUrl("second query", "brave", store.Settings.SearxInstance)) && tabs.Any(t => t.Url == SearchUrl("second query", "searxng", store.Settings.SearxInstance)); }, "Address search-both failed.");
            Program.SmokeSucceeded = true;
            Program.Log("SEARCH_UI_PASS preserved query, both controls opened paired tabs, AI cookie verified");
            Close();
        }

        private async Task ProbeAdFiltering(BrowserTab tab)
        {
            const string url = "https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js";
            int reached = 0;
            tab.Web.AddWebResourceRequestedFilter(url + "*", CoreWebView2WebResourceContext.All);
            EventHandler<CoreWebView2WebResourceRequestedEventArgs> handler = delegate(object sender, CoreWebView2WebResourceRequestedEventArgs e)
            {
                reached++;
                e.Response = contentEnvironment.CreateWebResourceResponse(new MemoryStream(new byte[] { 47, 47, 32, 111, 107 }), 200, "OK", "Content-Type: application/javascript\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-store");
            };
            tab.Web.WebResourceRequested += handler;
            Exception failure = null;
            try
            {
                // The response is served in-process if allowed; no external ad request is sent.
                string script = "(async()=>{try{const r=await fetch('" + url + "?moronco='+Date.now(),{cache:'no-store'});return r.ok;}catch{return false;}})()";
                string blocked = await tab.Web.CallDevToolsProtocolMethodAsync("Runtime.evaluate", json.Serialize(new { expression = script, awaitPromise = true, returnByValue = true }));
                if (reached != 0) throw new Exception("Ad request reached original response while enabled: " + blocked);
                await adBlocker.EnableAsync(false);
                string allowed = await tab.Web.CallDevToolsProtocolMethodAsync("Runtime.evaluate", json.Serialize(new { expression = script, awaitPromise = true, returnByValue = true }));
                if (!allowed.Contains("\"value\":true") || reached == 0) throw new Exception("Disabled ad blocker did not allow fixture: " + allowed);
                Program.Log("ADBLOCK_FILTER_PASS blocked with uBOL enabled, local fixture delivered when disabled");
            }
            catch (Exception ex) { failure = ex; }
            finally { tab.Web.WebResourceRequested -= handler; }
            await adBlocker.EnableAsync(true); UpdateAdBlockStatus();
            if (failure != null) throw failure;
        }

        private async Task WaitFor(Func<bool> condition, string failure)
        {
            for (int attempt=0; attempt<80 && !closing; attempt++) { if (condition()) return; await Task.Delay(100); }
            throw new Exception(failure);
        }

        private async Task ExerciseBrowser()
        {
            store.Settings.SaveHistory=true;
            Uri fixture;
            if (!Uri.TryCreate(Program.InitialUrl, UriKind.Absolute, out fixture) || fixture.Scheme != "http" || fixture.Host != "127.0.0.1") throw new Exception("Integration fixture must be local HTTP.");
            string origin = fixture.GetLeftPart(UriPartial.Authority);
            var original = Active;
            await WaitFor(delegate { return original != null && original.Web != null && original.Web.DocumentTitle == "First fixture" && !original.Loading; }, "Fixture did not load.");
            AssertPageHosting(original, "initial navigation");
            CaptureWindowForDiagnosis();
            var pendingTab = NewTab(origin + "/two", true);
            var racing = Active;
            bool wasInitializing = racing.Initializing;
            await NavigateInput("minium://newtab");
            await NavigateInput(origin + "/one");
            await pendingTab;
            await WaitFor(delegate { return racing.Web != null && racing.Web.DocumentTitle == "First fixture" && !racing.Loading; }, "Navigating Home during tab creation left the tab blank.");
            AssertPageHosting(racing, "Home during tab initialization");
            Program.Log("INITIALIZATION_RACE_PASS pending=" + wasInitializing);
            CloseTab(racing.Id); await SelectTab(original.Id);
            int before = tabs.Count;
            await original.Web.ExecuteScriptAsync("window.chrome.webview.postMessage({action:'tab-new',url:'https://example.com/'})");
            await Task.Delay(250);
            if (tabs.Count != before) throw new Exception("Untrusted page invoked privileged browser command.");
            original.Web.Navigate(origin + "/two");
            await WaitFor(delegate { return original.Web.DocumentTitle == "Second fixture" && !original.Loading; }, "Second fixture did not load.");
            if (!original.Web.CanGoBack) throw new Exception("Back history missing."); original.Web.GoBack();
            await WaitFor(delegate { return original.Web.DocumentTitle == "First fixture" && !original.Loading; }, "Back navigation failed.");
            if (!original.Web.CanGoForward) throw new Exception("Forward history missing."); original.Web.GoForward();
            await WaitFor(delegate { return original.Web.DocumentTitle == "Second fixture" && !original.Loading; }, "Forward navigation failed.");
            AssertPageHosting(original, "back and forward");
            original.Controller.ZoomFactor = 1.15;
            if (Math.Abs(original.Controller.ZoomFactor-1.15) > .001) throw new Exception("Zoom did not apply.");
            string found = await original.Web.ExecuteScriptAsync("window.find('fixture',false,false,true)");
            if (found != "true") throw new Exception("Find in page failed.");
            await Shortcut(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.K);
            await WaitFor(delegate { return overlay; }, "Command palette did not open.");
            await Shortcut(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.L);
            await WaitFor(delegate { return !overlay; }, "Address shortcut left the command palette covering the page.");
            if (await chrome.CoreWebView2.ExecuteScriptAsync("document.getElementById('overlay-root').hidden&&document.activeElement.id==='address'") != "true") throw new Exception("Address shortcut did not restore visible address input.");
            AssertPageHosting(original, "toolbar focus after overlay");
            int originalTop = pageBounds.Top;
            await Shortcut(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.F);
            await WaitFor(delegate { return pageBounds.Top > originalTop && original.Controller.IsVisible && !overlay; }, "Find bar obscured the page instead of reserving space.");
            AssertPageHosting(original, "find bar");
            await chrome.CoreWebView2.ExecuteScriptAsync("document.getElementById('find-close').click()");
            await WaitFor(delegate { return pageBounds.Top == originalTop; }, "Closing find did not restore page bounds.");
            await Shortcut(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.K);
            await WaitFor(delegate { return overlay; }, "Command palette did not reopen.");
            await Shortcut(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.T);
            await Task.Delay(300);
            if (overlay || await chrome.CoreWebView2.ExecuteScriptAsync("document.getElementById('overlay-root').hidden") != "true") throw new Exception("Switching tabs left a stale overlay.");
            CloseTab(Active.Id); await SelectTab(original.Id);
            AssertPageHosting(original, "return from internal tab");
            SetFullscreen(true); AssertPageHosting(original, "fullscreen");
            await Shortcut(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.L);
            if (fullscreen || !chrome.IsVisible) throw new Exception("Fullscreen prevented address access.");
            AssertPageHosting(original, "exit fullscreen");
            await original.Web.CallDevToolsProtocolMethodAsync("Runtime.evaluate", json.Serialize(new { expression = "window.open('/popup','_blank');", userGesture = true }));
            await WaitFor(delegate { return tabs.Count == before+1 && Active != null && Active.Web != null && Active.Web.DocumentTitle == "Popup fixture" && !Active.Loading; }, "Popup did not open in a working new tab.");
            if (await Active.Web.ExecuteScriptAsync("window.opener !== null") != "true") throw new Exception("Popup lost its opener.");
            AssertPageHosting(Active, "second external tab");
            Program.Log("POPUP_PASS");
            CloseTab(Active.Id); await SelectTab(original.Id);
            AssertPageHosting(original, "return from external tab");
            if (ShouldReportNavigationError(CoreWebView2WebErrorStatus.ConnectionAborted, 200) || !ShouldReportNavigationError(CoreWebView2WebErrorStatus.ConnectionAborted, 0)) throw new Exception("ConnectionAborted status was not distinguished from a loaded page.");
            await HandleCommand(new Dictionary<string,object> { { "action","bookmark-add" },{ "url",origin+"/one" },{ "title","Fixture bookmark" } });
            await original.Web.ExecuteScriptAsync("location.href='/download'");
            await WaitFor(delegate { return downloads.Any(d => d.Operation.State == CoreWebView2DownloadState.Completed); }, "Download did not complete.");
            string file = Path.Combine(Program.Root,"downloads","fixture.txt");
            if (!File.Exists(file) || !File.ReadAllText(file).Contains("Minium download fixture")) throw new Exception("Downloaded bytes differ from fixture.");
            if (!store.History.Any(h => h.Url == origin+"/two") || !store.Bookmarks.Any(b => b.Title == "Fixture bookmark")) throw new Exception("History/bookmark state missing.");
            SaveState(); Program.SmokeSucceeded = true;
            Program.Log("INTEGRATION_PASS navigation,back,forward,popup,opener,download,zoom,find,fullscreen,bridge-isolation,bookmarks,history"); Close();
        }
    }
}
