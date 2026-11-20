using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace MiniumBrowser
{
    internal sealed partial class BrowserHost
    {
        private readonly Timer protectionTimer = new Timer { Interval = 7000 };
        private string vpnStatus = "checking", vpnMessage = "Checking Cloudflare WARP…";
        private bool vpnBusy, vpnReading, adBlockBusy;
        private string adBlockStatus = "loading", adBlockMessage = "Initializing uBlock Origin Lite…";
        private CoreWebView2BrowserExtension adBlocker;
        private CoreWebView2Controller protectionController;
        private Panel protectionSurface;
        private Task adBlockInitialization;
        private Task permissionReset;
        private Task EnsureSessionPermissions(CoreWebView2Profile profile)
        {
            if (permissionReset == null) permissionReset = ResetSavedPermissions(profile);
            return permissionReset;
        }
        private async Task ResetSavedPermissions(CoreWebView2Profile profile)
        {
            foreach (var permission in await profile.GetNonDefaultPermissionSettingsAsync())
                await profile.SetPermissionStateAsync(permission.PermissionKind, permission.PermissionOrigin, CoreWebView2PermissionState.Default);
        }
        private static string WarpCli { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cloudflare", "Cloudflare WARP", "warp-cli.exe"); } }

        private Task<CoreWebView2Environment> ContentEnvironment()
        {
            if (contentEnvironmentTask == null)
                contentEnvironmentTask = CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.Root, "profile"), new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true, Language = "en-US" });
            return contentEnvironmentTask;
        }

        private void StartProtection()
        {
            protectionTimer.Tick += async delegate { await RefreshVpn(); };
            protectionTimer.Start();
        }

        private Task EnsureAdBlocker()
        {
            if (adBlockInitialization == null) adBlockInitialization = InitializeAdBlocker();
            return adBlockInitialization;
        }

        private async Task InitializeAdBlocker()
        {
            Panel surface = null;
            CoreWebView2Controller controller = null;
            try
            {
                var environment = await ContentEnvironment();
                if (closing) return;
                surface = new Panel { Visible = false, Width = 1, Height = 1 };
                Controls.Add(surface);
                controller = await environment.CreateCoreWebView2ControllerAsync(surface.Handle);
                controller.IsVisible = false;
                if (closing) return;
                string folder = Path.Combine(SingleFileBootstrap.PayloadDirectory ?? AppDomain.CurrentDomain.BaseDirectory, "extensions", "ublock-lite");
                if (!File.Exists(Path.Combine(folder, "manifest.json"))) throw new IOException("uBlock Origin Lite package is missing.");
                string marker = Path.Combine(Program.Root, "adblock-extension.txt");
                string[] previous = File.Exists(marker) ? File.ReadAllLines(marker) : new string[0];
                var profile = controller.CoreWebView2.Profile;
                var installed = await profile.GetBrowserExtensionsAsync();
                if (previous.Length == 2 && previous[1] == folder) adBlocker = installed.FirstOrDefault(e => e.Id == previous[0]);
                if (adBlocker == null)
                {
                    adBlocker = await profile.AddBrowserExtensionAsync(folder);
                    if (previous.Length > 0)
                        foreach (var old in installed.Where(e => e.Id == previous[0] && e.Id != adBlocker.Id)) await old.RemoveAsync();
                    File.WriteAllLines(marker, new[] { adBlocker.Id, folder });
                }
                await adBlocker.EnableAsync(store.Settings.AdBlockEnabled);
                protectionController = controller;
                protectionSurface = surface;
                controller = null; surface = null;
                UpdateAdBlockStatus();
            }
            catch (Exception ex)
            {
                adBlockStatus = "error"; adBlockMessage = "Could not enable ad blocking: " + ex.Message;
                Program.Log("ADBLOCK_FAILED " + ex.Message);
            }
            finally
            {
                if (controller != null) controller.Close();
                if (surface != null) { Controls.Remove(surface); surface.Dispose(); }
                Publish();
            }
        }

        private void UpdateAdBlockStatus()
        {
            if (adBlocker == null) return;
            adBlockStatus = adBlocker.IsEnabled ? "enabled" : "disabled";
            adBlockMessage = adBlocker.IsEnabled ? "Blocking ads and trackers · uBlock Origin Lite" : "Ad blocking is off · uBlock Origin Lite";
            Publish();
        }

        private async Task ToggleAdBlocker()
        {
            if (adBlockBusy) return;
            adBlockBusy = true; Publish();
            try
            {
                await EnsureAdBlocker();
                if (adBlocker == null) { Toast(adBlockMessage); return; }
                bool enabled = !adBlocker.IsEnabled;
                await adBlocker.EnableAsync(enabled);
                store.Settings.AdBlockEnabled = adBlocker.IsEnabled;
                UpdateAdBlockStatus(); DirtyPublish();
                Toast("Successfully " + (adBlocker.IsEnabled ? "enabled" : "disabled") + " ad blocking. Reload pages to apply.");
            }
            catch (Exception ex) { Program.Log("ADBLOCK_TOGGLE_FAILED " + ex); Toast("Could not change ad blocking: " + ex.Message); }
            finally { adBlockBusy = false; Publish(); }
        }

        private static async Task<string> RunWarp(string arguments)
        {
            if (!File.Exists(WarpCli)) throw new FileNotFoundException("Cloudflare WARP is not installed.");
            // All arguments are fixed application commands, never website or user input.
            using (var process = new Process { StartInfo = new ProcessStartInfo(WarpCli, "--no-ansi --no-paginate " + arguments) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } })
            {
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!await Task.Run(() => process.WaitForExit(12000)))
                { try { process.Kill(); } catch { } throw new TimeoutException("WARP is not responding. Open Cloudflare WARP to check."); }
                string result = await output, detail = await error;
                if (process.ExitCode != 0) throw new InvalidOperationException("WARP could not be reached or rejected the command. Open WARP to check its service and registration.");
                return result + "\n" + detail;
            }
        }

        internal static string ParseWarpStatus(string status, string settings)
        {
            if (Regex.IsMatch(status, @"(?im)^\s*(?:Status update:\s*)?Disconnected\s*$")) return "disconnected";
            if (Regex.IsMatch(status, @"(?im)^\s*(?:Status update:\s*)?Connected\s*$"))
                return Regex.IsMatch(settings, @"(?im)\bMode:\s*(WarpWithDns|Warp|TunnelOnly)\s*$") ? "connected" : "dns";
            if (Regex.IsMatch(status, @"(?i)connecting|reconnecting")) return "connecting";
            return "unavailable";
        }

        private async Task RefreshVpn()
        {
            if (vpnReading || closing) return;
            vpnReading = true;
            try
            {
                if (!File.Exists(WarpCli)) { vpnStatus = "missing"; vpnMessage = "Cloudflare WARP is not installed on this device."; return; }
                string status = await RunWarp("status");
                string settings = await RunWarp("settings");
                vpnStatus = ParseWarpStatus(status, settings);
                vpnMessage = vpnStatus == "connected" ? "WARP reports connected · VPN tunnel mode." :
                    vpnStatus == "disconnected" ? "VPN is off." :
                    vpnStatus == "dns" ? "WARP is connected but VPN mode is not confirmed. Enable VPN to switch to WARP." :
                    vpnStatus == "connecting" ? "WARP is connecting…" : "Status is unknown. Open WARP to check.";
            }
            catch (Exception ex) { vpnStatus = "unavailable"; vpnMessage = ex.Message; }
            finally { vpnReading = false; Publish(); }
        }

        private async Task ToggleVpn()
        {
            if (vpnBusy || vpnReading) return;
            vpnBusy = true; Publish();
            try
            {
                await RefreshVpn();
                if (vpnStatus == "connected") await RunWarp("disconnect");
                else if (vpnStatus == "disconnected" || vpnStatus == "dns")
                { await RunWarp("mode warp+doh"); await RunWarp("connect"); }
                else { Toast(vpnMessage); return; }
                await RefreshVpn();
            }
            catch (Exception ex) { vpnStatus = "unavailable"; vpnMessage = ex.Message; Toast(vpnMessage); }
            finally { vpnBusy = false; Publish(); }
        }

        private void OpenWarp()
        {
            string app = Path.Combine(Path.GetDirectoryName(WarpCli), "Cloudflare WARP.exe");
            if (File.Exists(app)) Process.Start(new ProcessStartInfo(app) { UseShellExecute = true });
            else Toast("Install or repair Cloudflare WARP from one.one.one.one and try again.");
        }
    }
}
