using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace MiniumBrowser
{
    // Only framework types here. The CLR must not resolve WebView2 before the
    // verified, embedded payload has been extracted and explicitly loaded.
    internal static class SingleFileBootstrap
    {
        internal static string PayloadDirectory;

        [STAThread]
        private static int Main(string[] args)
        {
            string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniumBrowser");
            bool smoke = false;
            try
            {
                foreach (string arg in args)
                {
                    if (arg.StartsWith("--data-dir=", StringComparison.Ordinal)) dataRoot = Path.GetFullPath(arg.Substring(11));
                    if (arg.StartsWith("--smoke=", StringComparison.Ordinal)) smoke = true;
                }
                Directory.CreateDirectory(dataRoot);
                Assembly self = Assembly.GetExecutingAssembly();
                // Separate builds never overwrite DLLs loaded by an older build.
                PayloadDirectory = Path.Combine(dataRoot, "runtime", self.ManifestModule.ModuleVersionId.ToString("N"));
                Directory.CreateDirectory(PayloadDirectory);
                string[] files = { "Microsoft.Web.WebView2.Core.dll", "WebView2Loader.dll", "WebView2-LICENSE.txt", "WebView2-NOTICE.txt" };
                foreach (string file in files) ExtractVerified(self, "MiniumBundle." + file, file);
                ExtractAdBlocker(self);
                foreach (string resourceName in self.GetManifestResourceNames())
                {
                    const string uiPrefix = "MiniumUi.";
                    if (resourceName.StartsWith(uiPrefix, StringComparison.Ordinal))
                        ExtractVerified(self, resourceName, "ui/" + resourceName.Substring(uiPrefix.Length));
                }

                string managedPath = Path.Combine(PayloadDirectory, files[0]);
                string expectedIdentity = AssemblyName.GetAssemblyName(managedPath).FullName;
                AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
                {
                    return String.Equals(new AssemblyName(e.Name).FullName, expectedIdentity, StringComparison.OrdinalIgnoreCase)
                        ? Assembly.LoadFrom(managedPath) : null;
                };
                // Preload the verified copy before the application references it,
                // instead of relying on a DLL in the EXE/current directory.
                Assembly.LoadFrom(managedPath);
                return RunApplication(args, dataRoot);
            }
            catch (Exception ex)
            {
                string detail = "Could not start Moronco: " + ex.GetType().Name + "\n" + ex.Message;
                try { File.AppendAllText(Path.Combine(dataRoot, "bootstrap-error.log"), DateTime.UtcNow.ToString("O") + " " + detail + Environment.NewLine); }
                catch { }
                if (!smoke) MessageBox.Show(detail, "Moronco", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 4;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunApplication(string[] args, string dataRoot)
        {
            Program.Root = dataRoot;
            return Program.Main(args);
        }

        private static void ExtractAdBlocker(Assembly self)
        {
            using (var stream = self.GetManifestResourceStream("MoroncoBundle.ublock-lite.zip"))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (String.IsNullOrEmpty(entry.Name)) continue;
                    string target = SafePayloadPath("extensions/ublock-lite/" + entry.FullName);
                    byte[] content;
                    using (var input = entry.Open())
                    using (var buffer = new MemoryStream()) { input.CopyTo(buffer); content = buffer.ToArray(); }
                    byte[] hash;
                    using (var sha = SHA256.Create()) hash = sha.ComputeHash(content);
                    if (Matches(target, hash)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllBytes(temporary, content);
                        if (File.Exists(target)) File.Replace(temporary, target, null);
                        else File.Move(temporary, target);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
        }

        private static void ExtractVerified(Assembly self, string resourceName, string file)
        {
            string target = SafePayloadPath(file);
            byte[] content;
            using (Stream resource = self.GetManifestResourceStream(resourceName))
            {
                if (resource == null) throw new FileNotFoundException("Embedded payload missing", file);
                using (var buffer = new MemoryStream()) { resource.CopyTo(buffer); content = buffer.ToArray(); }
            }
            byte[] expected;
            using (var sha = SHA256.Create()) expected = sha.ComputeHash(content);
            if (Matches(target, expected)) return;
            string directory = Path.GetDirectoryName(target);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".extract-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(content, 0, content.Length); output.Flush(true); }
                try
                {
                    if (File.Exists(target)) File.Replace(temporary, target, null);
                    else File.Move(temporary, target);
                }
                catch (IOException)
                {
                    // A simultaneous first launch may have just published the
                    // identical payload. Accept it only after checking its hash.
                    if (!Matches(target, expected)) throw;
                }
                if (!Matches(target, expected)) throw new IOException("Extracted payload verification failed: " + file);
            }
            finally
            {
                // Delete only the unique temporary file created by this launch.
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static string SafePayloadPath(string file)
        {
            if (String.IsNullOrWhiteSpace(file) || Path.IsPathRooted(file)) throw new IOException("Invalid embedded payload path.");
            string[] segments = file.Replace('\\', '/').Split('/');
            foreach (string segment in segments)
            {
                if (String.IsNullOrEmpty(segment) || segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new IOException("Invalid embedded payload path: " + file);
            }
            string root = Path.GetFullPath(PayloadDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Embedded payload path escapes its directory.");
            return target;
        }

        private static bool Matches(string path, byte[] expected)
        {
            if (!File.Exists(path)) return false;
            byte[] actual;
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create()) actual = sha.ComputeHash(input);
            if (actual.Length != expected.Length) return false;
            for (int i = 0; i < actual.Length; i++) if (actual[i] != expected[i]) return false;
            return true;
        }
    }
}
