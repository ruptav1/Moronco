using System;
using MiniumBrowser;

internal static class AddressInputProbe
{
    private static int Main()
    {
        string[,] accepted = new string[,]
        {
            { "  example.com  ", "https://example.com/" },
            { "www.example.com/path?q=one#part", "https://www.example.com/path?q=one#part" },
            { "example.com:8080/path", "https://example.com:8080/path" },
            { "HTTP://EXAMPLE.COM", "http://example.com/" },
            { "https://example.com/a b?q=one&two=2#part", "https://example.com/a%20b?q=one&two=2#part" },
            { "bücher.de", "https://xn--bcher-kva.de/" },
            { "https://bücher.de/lesen", "https://xn--bcher-kva.de/lesen" },
            { "localhost", "http://localhost/" },
            { "localhost:8080/test", "http://localhost:8080/test" },
            { "app.localhost:3000", "http://app.localhost:3000/" },
            { "intranet:8080/path", "http://intranet:8080/path" },
            { "127.0.0.1:9000", "http://127.0.0.1:9000/" },
            { "192.168.1.20", "http://192.168.1.20/" },
            { "[::1]:8080/test", "http://[::1]:8080/test" },
            { "[2001:db8::1]", "http://[2001:db8::1]/" },
            { "about:blank", "about:blank" },
            { "ABOUT:BLANK", "about:blank" },
            { "youtube", "https://search.brave.com/search?q=youtube&summary=0" },
            { "cách nấu phở", "https://search.brave.com/search?q=c%C3%A1ch%20n%E1%BA%A5u%20ph%E1%BB%9F&summary=0" },
            { "cats & dogs #music", "https://search.brave.com/search?q=cats%20%26%20dogs%20%23music&summary=0" },
            { "C++", "https://search.brave.com/search?q=C%2B%2B&summary=0" },
            { "hello?x=1&next=https://example.com/#x", "https://search.brave.com/search?q=hello%3Fx%3D1%26next%3Dhttps%3A%2F%2Fexample.com%2F%23x&summary=0" },
            { "  two\twords  ", "https://search.brave.com/search?q=two%09words&summary=0" }
        };
        string[] rejected = new string[]
        {
            null, "", " \t\r\n ", "http:", "https:example.com", "http:/example.com",
            "https:///example.com", "http://", "https://bad host.com", "http://example.com:99999",
            "http://example.com\n.evil.com", "https://example.com\\@evil.com",
            "https://user:password@example.com", "https://@example.com", "user:password@example.com",
            "javascript:alert(1)", "javascript: alert(1)", "javascript:123",
            "file:///C:/Windows/win.ini", "data:text/html,hello", "mailto:person@example.com",
            "custom://example.com", "custom://example.com hello", "custom:payload", "about:config"
        };
        int failures = 0;
        for (int i = 0; i < accepted.GetLength(0); i++)
        {
            string url, error;
            bool result = AddressInput.TryResolve(accepted[i, 0], out url, out error);
            // Framework 4.x expands IPv6 literals in AbsoluteUri; compare against
            // the runtime's equivalent canonical spelling, including path/query.
            string expected = new Uri(accepted[i, 1]).AbsoluteUri;
            if (!result || url != expected || error != null)
            {
                failures++;
                Console.Error.WriteLine("FAIL accept [{0}]: result={1}, url=[{2}], error=[{3}], expected=[{4}]",
                    accepted[i, 0], result, url, error, expected);
            }
        }
        for (int i = 0; i < rejected.Length; i++)
        {
            string url, error;
            bool result = AddressInput.TryResolve(rejected[i], out url, out error);
            if (result || url != null || String.IsNullOrWhiteSpace(error))
            {
                failures++;
                Console.Error.WriteLine("FAIL reject [{0}]: result={1}, url=[{2}], error=[{3}]", rejected[i], result, url, error);
            }
        }
        Console.WriteLine("{0}: {1} address/search cases; {2} failures.", failures == 0 ? "PASS" : "FAIL",
            accepted.GetLength(0) + rejected.Length, failures);
        return failures == 0 ? 0 : 1;
    }
}
