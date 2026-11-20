using System;

namespace MiniumBrowser
{
    // Optional, conservative policy. Call only for WebResourceContext.Image.
    // This does not identify advertising, filter media, or promise RAM savings.
    internal static class YouTubeImagePolicy
    {
        internal static bool IsYouTubePage(string url)
        {
            Uri uri;
            return TryWebUri(url, out uri) && IsYouTubeHost(uri.Host);
        }

        internal static bool ShouldBlock(string topLevelUrl, string resourceUrl)
        {
            Uri top, resource;
            if (!TryWebUri(topLevelUrl, out top) || !IsYouTubeHost(top.Host)
                || !TryWebUri(resourceUrl, out resource)) return false;

            string host = resource.Host;
            string path = resource.AbsolutePath;
            if (String.Equals(host, "yt3.ggpht.com", StringComparison.OrdinalIgnoreCase)
                || String.Equals(host, "yt3.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
                return path.Length > 1;

            if (!IsThumbnailHost(host)) return false;
            string[] parts = path.Split('/');
            if (parts.Length != 4 || !IsVideoId(parts[2])) return false;
            bool thumbnail = (parts[1] == "vi" && parts[3].EndsWith(".jpg", StringComparison.Ordinal))
                || (parts[1] == "vi_webp" && parts[3].EndsWith(".webp", StringComparison.Ordinal));
            if (!thumbnail || parts[3].IndexOf('%') >= 0) return false;

            // Keep the current video's poster and related player thumbnails.
            return !IsCurrentVideo(top, parts[2]);
        }

        private static bool TryWebUri(string value, out Uri uri)
        {
            uri = null;
            if (String.IsNullOrEmpty(value)) return false;
            for (int i = 0; i < value.Length; i++)
                if (Char.IsWhiteSpace(value[i]) || Char.IsControl(value[i]) || value[i] == '\\')
                    return false;
            int schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0) return false;
            int authorityEnd = value.IndexOfAny(new char[] { '/', '?', '#' }, schemeEnd + 3);
            if (authorityEnd < 0) authorityEnd = value.Length;
            // Reject even empty userinfo (https://@host), which Uri.UserInfo
            // represents as an empty string just like an ordinary URL.
            if (value.IndexOf('@', schemeEnd + 3, authorityEnd - schemeEnd - 3) >= 0)
                return false;
            return Uri.TryCreate(value, UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                && uri.IsDefaultPort && String.IsNullOrEmpty(uri.UserInfo);
        }

        private static bool IsYouTubeHost(string host)
        {
            return String.Equals(host, "youtube.com", StringComparison.OrdinalIgnoreCase)
                || String.Equals(host, "www.youtube.com", StringComparison.OrdinalIgnoreCase)
                || String.Equals(host, "m.youtube.com", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsThumbnailHost(string host)
        {
            if (String.Equals(host, "i.ytimg.com", StringComparison.OrdinalIgnoreCase)) return true;
            return host.Length == 12 && (host[0] == 'i' || host[0] == 'I')
                && host[1] >= '1' && host[1] <= '9'
                && String.Equals(host.Substring(2), ".ytimg.com", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVideoId(string value)
        {
            if (value.Length != 11) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z')
                    && !(c >= '0' && c <= '9') && c != '_' && c != '-') return false;
            }
            return true;
        }

        private static bool IsCurrentVideo(Uri top, string videoId)
        {
            string[] path = top.AbsolutePath.Split('/');
            if (path.Length >= 3 && (path[1] == "shorts" || path[1] == "embed")
                && path[2] == videoId) return true;
            if (top.AbsolutePath != "/watch") return false;
            string[] parameters = top.Query.TrimStart('?').Split('&');
            for (int i = 0; i < parameters.Length; i++)
            {
                int separator = parameters[i].IndexOf('=');
                if (separator < 0) continue;
                if (Uri.UnescapeDataString(parameters[i].Substring(0, separator)) == "v"
                    && Uri.UnescapeDataString(parameters[i].Substring(separator + 1)) == videoId)
                    return true;
            }
            return false;
        }
    }
}
