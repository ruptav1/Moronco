using System;
using System.Globalization;
using System.Net;

namespace MiniumBrowser
{
    // Keep address interpretation independent of the UI and of WebView2.
    public static class AddressInput
    {
        public static bool TryResolve(string input, out string url, out string error)
        {
            bool isSearch;
            return TryResolve(input, out url, out error, out isSearch);
        }

        public static bool TryResolve(string input, out string url, out string error, out bool isSearch)
        {
            isSearch = false;
            url = null;
            error = null;
            string value = (input ?? String.Empty).Trim();
            if (value.Length == 0)
            {
                error = "Enter a web address or search query.";
                return false;
            }
            if (String.Equals(value, "about:blank", StringComparison.OrdinalIgnoreCase))
            {
                url = "about:blank";
                return true;
            }

            string scheme = GetScheme(value);
            if (scheme == "http" || scheme == "https")
                return TryHttpUrl(value, out url, out error);

            // Block explicit non-web protocols before interpreting spaces as a search.
            if (scheme != null && (value.StartsWith(scheme + "://", StringComparison.OrdinalIgnoreCase)
                || IsKnownProtocol(scheme)))
            {
                error = "Only HTTP, HTTPS and about:blank are supported.";
                return false;
            }

            if (!ContainsWhitespace(value))
            {
                string authority = GetAuthority(value);
                string host = authority;
                int at = host.LastIndexOf('@');
                if (at >= 0) host = host.Substring(at + 1);
                bool hasPort = false;
                if (host.StartsWith("[", StringComparison.Ordinal))
                {
                    int close = host.IndexOf(']');
                    if (close >= 0) host = host.Substring(0, close + 1);
                }
                else
                {
                    int colon = host.LastIndexOf(':');
                    if (colon >= 0)
                    {
                        string port = host.Substring(colon + 1);
                        hasPort = port.Length > 0;
                        for (int i = 0; i < port.Length; i++)
                            if (port[i] < '0' || port[i] > '9') hasPort = false;
                        host = host.Substring(0, colon);
                    }
                }

                IPAddress ip;
                string ipHost = host.Trim('[', ']');
                bool isIp = (host.IndexOf('.') >= 0 || host.IndexOf(':') >= 0)
                    && IPAddress.TryParse(ipHost, out ip);
                bool isLocal = String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
                bool isDomain = host.IndexOf('.') >= 0;
                bool isNamedPort = hasPort && Uri.CheckHostName(host) == UriHostNameType.Dns;
                if (isIp || isLocal || isDomain || isNamedPort)
                {
                    string prefix = isIp || isLocal || (isNamedPort && !isDomain) ? "http://" : "https://";
                    return TryHttpUrl(prefix + value, out url, out error);
                }
                if (scheme != null)
                {
                    error = "Only HTTP, HTTPS and about:blank are supported.";
                    return false;
                }
            }

            try
            {
                isSearch = true;
                url = "https://search.brave.com/search?q=" + Uri.EscapeDataString(value) + "&summary=0";
                return true;
            }
            catch (UriFormatException)
            {
                error = "Search query is too long.";
                return false;
            }
        }

        private static bool TryHttpUrl(string value, out string url, out string error)
        {
            url = null;
            error = "Invalid web address. Example: https://example.com";
            int separator = value.IndexOf("://", StringComparison.Ordinal);
            if (separator < 0 || value.IndexOf('\\') >= 0) return false;
            for (int i = 0; i < value.Length; i++)
                if (Char.IsControl(value[i])) return false;
            string authority = GetAuthority(value.Substring(separator + 3));
            if (authority.Length == 0 || ContainsWhitespace(authority)) return false;
            if (authority.IndexOf('@') >= 0)
            {
                error = "Addresses containing usernames or passwords are not supported.";
                return false;
            }

            Uri parsed;
            if (!Uri.TryCreate(value, UriKind.Absolute, out parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
                || parsed.Host.Length == 0 || parsed.UserInfo.Length != 0
                || parsed.HostNameType == UriHostNameType.Unknown
                || parsed.HostNameType == UriHostNameType.Basic) return false;
            try
            {
                // A canonical ASCII host also lets WebView2 handle IDN consistently.
                if (parsed.HostNameType == UriHostNameType.Dns)
                {
                    UriBuilder builder = new UriBuilder(parsed);
                    builder.Host = new IdnMapping().GetAscii(parsed.DnsSafeHost);
                    parsed = builder.Uri;
                }
                url = parsed.AbsoluteUri;
                error = null;
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (UriFormatException) { return false; }
        }

        private static string GetAuthority(string value)
        {
            int end = value.IndexOfAny(new char[] { '/', '?', '#' });
            return end < 0 ? value : value.Substring(0, end);
        }

        private static bool ContainsWhitespace(string value)
        {
            for (int i = 0; i < value.Length; i++)
                if (Char.IsWhiteSpace(value[i])) return true;
            return false;
        }

        private static string GetScheme(string value)
        {
            int colon = value.IndexOf(':');
            if (colon <= 0 || !IsAsciiLetter(value[0])) return null;
            for (int i = 1; i < colon; i++)
            {
                char c = value[i];
                if (!IsAsciiLetter(c) && !(c >= '0' && c <= '9') && c != '+' && c != '-' && c != '.')
                    return null;
            }
            return value.Substring(0, colon).ToLowerInvariant();
        }

        private static bool IsAsciiLetter(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        private static bool IsKnownProtocol(string scheme)
        {
            switch (scheme)
            {
                case "javascript": case "vbscript": case "data": case "file":
                case "mailto": case "ftp": case "about": case "chrome": case "edge":
                    return true;
                default:
                    return false;
            }
        }
    }
}
