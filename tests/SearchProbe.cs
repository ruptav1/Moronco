using System;
using System.IO;
using System.Reflection;
using System.Web;

class SearchProbe
{
    static int Main(string[] args)
    {
        var assembly = Assembly.LoadFrom(args[0]);
        var method = assembly.GetType("MiniumBrowser.BrowserHost").GetMethod("SearchUrl", BindingFlags.NonPublic | BindingFlags.Static);
        string query = "\"tài liệu\" site:example.org C++ & # ? 日本語";
        foreach (string engine in new[] { "brave", "searxng" })
        {
            var url = new Uri((string)method.Invoke(null, new object[] { query, engine, "https://example.org/searx/search" }));
            var values = HttpUtility.ParseQueryString(url.Query);
            if (values["q"] != query) throw new Exception("Search query changed or injected parameters.");
            if (engine == "brave" && (url.Host != "search.brave.com" || values["summary"] != "0")) throw new Exception("Incorrect Brave route.");
            if (engine == "searxng" && (url.AbsolutePath != "/searx/search" || values["engines"] != "bing,duckduckgo,mwmbl,wiby")) throw new Exception("Incorrect SearXNG route or engines.");
        }
        var normalize = assembly.GetType("MiniumBrowser.BrowserStore").GetMethod("TrySearxInstance", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (string input in new[] { "javascript:alert(1)", "file:///tmp/x", "https://user:pass@example.org", "https://example.org/?q=x", "https://example.org/#x", "http://example.org", "https://example.org\\evil", "https://example.org\n" })
            if ((bool)normalize.Invoke(null, new object[] { input, null })) throw new Exception("Unsafe instance accepted: " + input);
        foreach (string input in new[] { "https://example.org", "https://example.org/searx/", "http://localhost:8080/", "http://127.0.0.1:8080/search" })
            if (!(bool)normalize.Invoke(null, new object[] { input, null })) throw new Exception("Valid instance rejected: " + input);
        Console.WriteLine("PASS search URLs preserve Unicode/operators, exclude Google, and validate instance addresses.");
        return 0;
    }
}
