using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace MiniumBrowser
{
    internal sealed partial class BrowserHost
    {
        internal static string SearchUrl(string query, string engine, string instance)
        {
            string escaped = Uri.EscapeDataString(query);
            if (engine != "searxng") return "https://search.brave.com/search?q=" + escaped + "&summary=0";
            string root;
            if (!BrowserStore.TrySearxInstance(instance, out root)) throw new ArgumentException("Invalid SearXNG address.");
            return root + "search?q=" + escaped + "&categories=general&engines=bing%2Cduckduckgo%2Cmwmbl%2Cwiby&language=all";
        }

        private async Task Search(string text, string engine, bool both)
        {
            string query = (text ?? "").Trim();
            if (query.Length == 0) { Toast("Enter a search query."); return; }
            if (query.Length > 8000) { Toast("Query is too long (maximum 8,000 characters)."); return; }
            if (both)
            {
                if (tabs.Count > 48) { Toast("Make room for two search tabs. Close some tabs and try again."); return; }
                string brave = SearchUrl(query, "brave", store.Settings.SearxInstance);
                string searx = SearchUrl(query, "searxng", store.Settings.SearxInstance);
                await NewTab(brave, true);
                await NewTab(searx, false);
                Toast("Opened Brave and SearXNG with the same query.");
            }
            else await NavigateInput(SearchUrl(query, engine, store.Settings.SearxInstance));
        }

        private void ApplySearchPreferences(CoreWebView2 web, string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Host != "search.brave.com") return;
            // Verified against Brave's own settings form: summarizer=0 disables automatic AI answers.
            var cookie = web.CookieManager.CreateCookie("summarizer", "0", "search.brave.com", "/");
            cookie.IsSecure = true;
            cookie.Expires = DateTime.UtcNow.AddYears(1);
            web.CookieManager.AddOrUpdateCookie(cookie);
        }
    }
}
