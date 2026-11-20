using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

[assembly: System.Reflection.AssemblyTitle("Moronco")]
[assembly: System.Reflection.AssemblyProduct("Moronco")]
[assembly: System.Reflection.AssemblyDescription("Moronco web browser")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

namespace MiniumBrowser
{
    internal static class Program
    {
        internal static string Root = AppDomain.CurrentDomain.BaseDirectory;
        internal static string LogPath, SmokeMode, InitialUrl, InitialInput;
        internal static int SmokeSeconds = 25;
        internal static bool NavigationSucceeded, UiSucceeded, SmokeSucceeded;
        internal static bool ResetUi;
        internal static bool ShowTestWindow;
        [STAThread]
        internal static int Main(string[] args)
        {
            foreach (string arg in args)
            {
                if (arg.StartsWith("--data-dir=")) Root = Path.GetFullPath(arg.Substring(11));
                if (arg.StartsWith("--smoke=")) SmokeMode = arg.Substring(8);
                if (arg.StartsWith("--url=")) InitialUrl = arg.Substring(6);
                if (arg.StartsWith("--input=")) InitialInput = arg.Substring(8);
                if (arg == "--reset-ui") ResetUi = true;
                if (arg == "--show-test-window") ShowTestWindow = true;
                int seconds;
                if (arg.StartsWith("--seconds=") && Int32.TryParse(arg.Substring(10), out seconds)) SmokeSeconds = Math.Max(5, Math.Min(120, seconds));
            }
            Directory.CreateDirectory(Path.Combine(Root, "logs"));
            LogPath = Path.Combine(Root, "logs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".log");
            if (SingleFileBootstrap.PayloadDirectory != null) CoreWebView2Environment.SetLoaderDllFolderPath(SingleFileBootstrap.PayloadDirectory);
            string name;
            using (var sha = SHA256.Create()) name = "Local\\MiniumBrowser-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(Root).ToLowerInvariant()))).Replace("-", "").Substring(0, 24);
            bool first;
            using (var instance = new Mutex(true, name, out first))
            {
                if (!first) { Log("ALREADY_RUNNING"); if (SmokeMode == null) MessageBox.Show("Moronco is already using this profile. Open more tabs in the existing window.", "Moronco"); return 0; }
                try
                {
                    Log("START edition=anime memory_limit=off smoke=" + (SmokeMode ?? "none"));
                    string error;
                    if (!MemoryBudget.TryApply(false, out error)) { Log("JOB_FAILED " + error); return 2; }
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                    Application.Run(new BrowserHost());
                    Log("EXIT");
                    if (SmokeMode != null && !UiSucceeded) return 5;
                    if ((SmokeMode == "ui" || SmokeMode == "integration" || SmokeMode == "restore" || SmokeMode == "search" || SmokeMode == "deep" || SmokeMode == "upgrade") && !SmokeSucceeded) return 7;
                    if ((SmokeMode == "browse" || SmokeMode == "blank") && !NavigationSucceeded) return 6;
                    return 0;
                }
                catch (Exception ex) { Log("FATAL " + ex); if (SmokeMode == null) MessageBox.Show("Could not start Moronco.\n" + ex.Message, "Moronco"); return 3; }
                finally { instance.ReleaseMutex(); }
            }
        }
        internal static void Log(string message) { if (SmokeMode == null) return; try { File.AppendAllText(LogPath, DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine); } catch { } }
    }
}
