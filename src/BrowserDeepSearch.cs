using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MiniumBrowser
{
    internal sealed class DeepResult
    {
        public string url, title, description;
        public List<string> sources = new List<string>();
    }
    internal sealed class DeepSource
    {
        public string name, url, tabId, status = "Loading…";
        public List<DeepResult> results = new List<DeepResult>();
    }
    internal sealed class DeepSearchState
    {
        public string query = "", mode = "broad";
        public bool busy;
        public int page = 1;
        public List<DeepSource> sources = new List<DeepSource>();
        public List<DeepResult> results = new List<DeepResult>();
    }
    internal sealed partial class BrowserHost
    {
        private DeepSearchState deepSearch = new DeepSearchState();
        private int deepGeneration;

        internal static string DeepQuery(string text, string mode, string site, string exclude, string file)
        {
            text = (text ?? "").Trim(); site = (site ?? "").Trim(); exclude = (exclude ?? "").Trim();
            if (text.Length == 0 || text.Length > 2000) throw new ArgumentException("Enter a query between 1 and 2,000 characters.");
            if (site.Length > 253 || (site.Length > 0 && !Regex.IsMatch(site, @"^(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}$"))) throw new ArgumentException("Enter a domain such as example.org without a path.");
            if (exclude.Length > 200) throw new ArgumentException("The excluded phrase must be at most 200 characters.");
            if (!new[] { "", "pdf", "txt", "docx" }.Contains(file)) throw new ArgumentException("Invalid file type.");
            string query = mode == "exact" ? "\"" + text.Replace("\"", " ").Trim() + "\"" : text;
            if (site.Length > 0) query += " site:" + site.ToLowerInvariant();
            if (exclude.Length > 0) query += " -\"" + exclude.Replace("\"", " ") + "\"";
            if (file.Length > 0) query += " filetype:" + file;
            return query;
        }

        internal static List<DeepResult> MergeDeepResults(IEnumerable<DeepSource> sources)
        {
            var output = new List<DeepResult>();
            var seen = new Dictionary<string, DeepResult>(StringComparer.Ordinal);
            var all = sources.ToList();
            // Interleave source ranks; do not invent a relevance score.
            for (int rank = 0; rank < 150; rank++) foreach (var source in all)
            {
                if (rank >= source.results.Count) continue;
                var item = source.results[rank]; Uri uri;
                if (!Uri.TryCreate(item.url, UriKind.Absolute, out uri) || !IsWebUrl(item.url) || item.url.Length > 8192) continue;
                var builder = new UriBuilder(uri) { Fragment = "" };
                string key = builder.Uri.AbsoluteUri;
                DeepResult result;
                if (!seen.TryGetValue(key, out result))
                {
                    result = new DeepResult { url = item.url, title = item.title, description = item.description };
                    seen.Add(key, result); output.Add(result);
                }
                if (!result.sources.Contains(source.name)) result.sources.Add(source.name);
            }
            return output;
        }

        private void CancelDeepSearch()
        {
            deepGeneration++; deepSearch.busy = false;
            foreach (var source in deepSearch.sources) if (source.status == "Loading…") source.status = "Collection stopped.";
            Publish();
        }

        private async Task StartDeepSearch(Dictionary<string, object> command)
        {
            if (deepSearch.busy) { Toast("Wait for the current search or press Stop."); return; }
            if (tabs.Count > 48) { Toast("Make room for two source tabs."); return; }
            string mode = TextValue(command, "mode") == "exact" ? "exact" : "broad";
            string query;
            try { query = DeepQuery(TextValue(command,"text"), mode, TextValue(command,"site"), TextValue(command,"exclude"), TextValue(command,"file")); }
            catch (ArgumentException ex) { Toast(ex.Message); return; }
            int generation = ++deepGeneration;
            var run = new DeepSearchState { query = query, mode = mode, busy = true };
            run.sources.Add(new DeepSource { name = "Brave", url = SearchUrl(query,"brave",store.Settings.SearxInstance) });
            run.sources.Add(new DeepSource { name = "SearXNG", url = SearchUrl(query,"searxng",store.Settings.SearxInstance) });
            deepSearch = run; Publish();
            try
            {
                foreach (var source in run.sources)
                {
                    if (closing || generation != deepGeneration) return;
                    var sourceTab = await NewTab(source.url, false);
                    if (sourceTab != null) source.tabId = sourceTab.Id;
                }
                Publish();
                await Task.WhenAll(run.sources.Select(source => CollectDeepSource(run, source, generation)));
            }
            finally { if (generation == deepGeneration) { run.busy = false; Publish(); } }
        }

        internal static string DeepPageUrl(string url, int page, string source)
        {
            if (page < 2 || page > 5) throw new ArgumentException("Page must be between 2 and 5.");
            return url + (source == "Brave" ? "&offset=" + (page - 1) : "&pageno=" + page);
        }

        private async Task MoreDeepSearch()
        {
            var run = deepSearch;
            if (run.busy || run.sources.Count != 2 || run.page >= 5) return;
            var sourceTabs = run.sources.Select(s => tabs.FirstOrDefault(t => t.Id == s.tabId && !t.Closed && t.Url == s.url)).ToArray();
            if (tabs.Count + sourceTabs.Count(t => t == null) > 50) { Toast("Close some tabs before loading more results."); return; }
            int generation = ++deepGeneration; run.busy = true; run.page++; Publish();
            try
            {
                for (int i = 0; i < run.sources.Count; i++)
                {
                    if (closing || generation != deepGeneration) return;
                    var source = run.sources[i];
                    // Keep the original instance for this search even if settings change.
                    string rootUrl = Regex.Replace(source.url, @"&(offset|pageno)=\d+", "");
                    source.url = DeepPageUrl(rootUrl, run.page, source.name); source.status = "Loading…";
                    var tab = sourceTabs[i];
                    if (tab != null && tab.Web != null) { tab.Loading = true; tab.Url = source.url; tab.Web.Navigate(source.url); }
                    else { tab = await NewTab(source.url, false); source.tabId = tab == null ? null : tab.Id; }
                }
                Publish();
                await Task.WhenAll(run.sources.Select(source => CollectDeepSource(run, source, generation)));
            }
            finally { if (generation == deepGeneration) { run.busy = false; Publish(); } }
        }

        private async Task CollectDeepSource(DeepSearchState run, DeepSource source, int generation)
        {
            var deadline = DateTime.UtcNow.AddSeconds(35);
            try
            {
                while (!closing && generation == deepGeneration && DateTime.UtcNow < deadline)
                {
                    var tab = tabs.FirstOrDefault(t => t.Id == source.tabId);
                    if (tab == null || tab.Closed) { source.status = "Source tab was closed."; break; }
                    if (tab.Web != null && !tab.Loading)
                    {
                        string script = DeepExtractScript(source.name, source.url);
                        string raw = await tab.Web.ExecuteScriptAsync(script);
                        if (closing || generation != deepGeneration) return;
                        var items = json.Deserialize<DeepResult[]>(raw);
                        if (items != null && items.Length > 0)
                        {
                            source.results.AddRange(items.Take(30));
                            source.status = "Read " + source.results.Count + " results from the source page.";
                            break;
                        }
                    }
                    await Task.Delay(1000);
                }
                if (closing || generation != deepGeneration) return;
                if (source.status == "Loading…") source.status = "No results could be read. The source may require verification, have no results, or have changed its layout. Open the source tab to check.";
            }
            catch { if (generation == deepGeneration) source.status = "Could not read the source tab. Open it to search directly."; }
            if (!closing && generation == deepGeneration) { run.results = MergeDeepResults(run.sources); Publish(); }
        }

        private string DeepExtractScript(string source, string expected)
        {
            // Only extract result cards on the exact requested search endpoint/query, never arbitrary pages.
            return @"(()=>{const expected=new URL(" + json.Serialize(expected) + @");const here=new URL(location.href);
if(here.origin!==expected.origin||here.pathname!==expected.pathname||['q','offset','pageno'].some(k=>here.searchParams.get(k)!==expected.searchParams.get(k)))return [];
const brave=" + (source == "Brave" ? "true" : "false") + @";
return Array.from(document.querySelectorAll(brave?'.snippet[data-type=web]':'article.result')).slice(0,30).flatMap(card=>{
const heading=card.querySelector(brave?'.search-snippet-title':'h3');const a=heading&&(heading.closest('a')||heading.querySelector('a'));
if(!a)return [];let u;try{u=new URL(a.href);}catch{return [];}if(!/^https?:$/.test(u.protocol)||u.username||u.password)return [];
const description=card.querySelector(brave?'.generic-snippet .content':'.content');
return [{url:u.href.slice(0,8192),title:heading.textContent.trim().slice(0,500),description:description?description.textContent.trim().slice(0,1500):''}];});})()";
        }
    }
}
