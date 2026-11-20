using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MiniumBrowser
{
    // Checks the latest GitHub release once per launch. When a newer
    // Moronco.exe is published, the user is asked before anything is
    // downloaded; the new file is verified, then swapped in after exit.
    internal static class AutoUpdater
    {
        private const string Repository = "ruptav1/Moronco";
        private const string AssetName = "Moronco.exe";
        private static int started;

        internal static Version CurrentVersion { get { return Assembly.GetExecutingAssembly().GetName().Version; } }

        internal static void CheckInBackground(Form owner)
        {
            if (Program.SmokeMode != null) return;
            // Only the single-file release is self-contained; the dist build
            // uses a different profile folder and loose DLLs.
            if (SingleFileBootstrap.PayloadDirectory == null) return;
            if (Interlocked.Exchange(ref started, 1) != 0) return;
            var worker = new Thread(delegate()
            {
                try { Check(owner); } catch { }
            }) { IsBackground = true, Name = "Moronco updater" };
            worker.Start();
        }

        private static void Check(Form owner)
        {
            Thread.Sleep(4000);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string body = Fetch("https://api.github.com/repos/" + Repository + "/releases/latest");
            var release = new JavaScriptSerializer().DeserializeObject(body) as IDictionary<string, object>;
            if (release == null) return;
            object draft, pre;
            if (release.TryGetValue("draft", out draft) && draft is bool && (bool)draft) return;
            if (release.TryGetValue("prerelease", out pre) && pre is bool && (bool)pre) return;
            string tag = release.ContainsKey("tag_name") ? release["tag_name"] as string : null;
            Version latest;
            if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out latest)) return;
            if (Normalize(latest) <= Normalize(CurrentVersion)) return;

            string url = null, digest = null; long size = -1;
            var assets = release.ContainsKey("assets") ? release["assets"] as IEnumerable : null;
            if (assets == null) return;
            foreach (object item in assets)
            {
                var asset = item as IDictionary<string, object>;
                if (asset == null || !String.Equals(asset["name"] as string, AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                url = asset["browser_download_url"] as string;
                object value;
                if (asset.TryGetValue("size", out value) && value != null) size = Convert.ToInt64(value);
                if (asset.TryGetValue("digest", out value)) digest = value as string;
                break;
            }
            if (url == null || !url.StartsWith("https://github.com/" + Repository + "/releases/download/", StringComparison.OrdinalIgnoreCase)) return;

            bool accepted = false;
            owner.Invoke(new Action(delegate
            {
                if (owner.IsDisposed) return;
                accepted = MessageBox.Show(owner,
                    "A new version of Moronco is available.\n\nCurrent version: " + Normalize(CurrentVersion) + "\nNew version: " + Normalize(latest) + "\n\nDownload and install it now? Moronco will restart.",
                    "Moronco update", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes;
            }));
            if (!accepted) return;

            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string staged = exe + ".update";
            try
            {
                Download(url, staged);
                Verify(staged, size, digest, latest);
                string script = WriteSwapScript(exe, staged);
                Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                owner.BeginInvoke(new Action(delegate { if (!owner.IsDisposed) owner.Close(); }));
            }
            catch (Exception ex)
            {
                try { if (File.Exists(staged)) File.Delete(staged); } catch { }
                owner.BeginInvoke(new Action(delegate
                {
                    if (!owner.IsDisposed) MessageBox.Show(owner, "The update could not be installed.\n" + ex.Message, "Moronco update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }));
            }
        }

        private static Version Normalize(Version v)
        {
            return new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
        }

        private static string Fetch(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Moronco-Updater";
            request.Accept = "application/vnd.github+json";
            request.Timeout = 15000;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private static void Download(string url, string path)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Moronco-Updater";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 60000;
            request.AllowAutoRedirect = true;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var input = response.GetResponseStream())
            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
        }

        private static void Verify(string path, long size, string digest, Version expected)
        {
            var info = new FileInfo(path);
            if (size > 0 && info.Length != size) throw new IOException("Downloaded file size does not match the release.");
            if (!String.IsNullOrEmpty(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                string actual;
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create()) actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!String.Equals(actual, digest.Substring(7), StringComparison.OrdinalIgnoreCase)) throw new IOException("Downloaded file checksum does not match the release.");
            }
            AssemblyName name = AssemblyName.GetAssemblyName(path);
            if (!String.Equals(name.Name, "Moronco", StringComparison.OrdinalIgnoreCase) || Normalize(name.Version) != Normalize(expected))
                throw new IOException("Downloaded file is not the expected Moronco version.");
        }

        private static string WriteSwapScript(string exe, string staged)
        {
            int pid = Process.GetCurrentProcess().Id;
            string script = Path.Combine(Path.GetTempPath(), "moronco-update-" + Guid.NewGuid().ToString("N") + ".cmd");
            var text = new StringBuilder();
            text.AppendLine("@echo off");
            text.AppendLine(":wait");
            text.AppendLine("tasklist /FI \"PID eq " + pid + "\" /NH | find \"" + pid + "\" >nul && (timeout /t 1 /nobreak >nul & goto wait)");
            text.AppendLine("set n=0");
            text.AppendLine(":move");
            text.AppendLine("move /y \"" + staged + "\" \"" + exe + "\" >nul 2>&1 && goto run");
            text.AppendLine("set /a n+=1");
            text.AppendLine("if %n% geq 15 goto run");
            text.AppendLine("timeout /t 1 /nobreak >nul");
            text.AppendLine("goto move");
            text.AppendLine(":run");
            text.AppendLine("start \"\" \"" + exe + "\"");
            text.AppendLine("del \"%~f0\"");
            File.WriteAllText(script, text.ToString(), Encoding.Default);
            return script;
        }
    }
}
