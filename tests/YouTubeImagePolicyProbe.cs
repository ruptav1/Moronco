using System;
using MiniumBrowser;

internal static class YouTubeImagePolicyProbe
{
    private const string Top = "https://www.youtube.com/watch?v=YQHsXMglC9A&pp=ygUFaGVsbG8%3D";
    private const string Other = "https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg";

    private static int Main()
    {
        string[,] blocked = new string[,]
        {
            { Top, Other },
            { "http://youtube.com/", Other },
            { "https://m.youtube.com/results?search_query=hello", Other },
            { "https://WWW.YOUTUBE.COM/", "https://I.YTIMG.COM/vi/dQw4w9WgXcQ/hqdefault.jpg?sqp=123" },
            { Top, "https://i1.ytimg.com/vi/dQw4w9WgXcQ/maxresdefault.jpg" },
            { Top, "https://i9.ytimg.com/vi_webp/dQw4w9WgXcQ/hqdefault.webp" },
            { Top, "https://yt3.ggpht.com/ytc/AIdro_hash=s88-c-k-c0x00ffffff-no-rj" },
            { Top, "https://yt3.googleusercontent.com/AGF-l78_hash=s88-c-k-c0x00ffffff-no-rj" },
            { "https://www.youtube.com/shorts/YQHsXMglC9A", Other },
            { "https://www.youtube.com/embed/YQHsXMglC9A", Other }
        };
        string[] falseTops = new string[]
        {
            null, "", "about:blank", "www.youtube.com", "https://discord.com/channels/@me",
            "https://example.com/?next=https://youtube.com/", "https://youtube.com.evil.test/",
            "https://notyoutube.com/", "https://evil.youtube.com/", "https://youtu.be/YQHsXMglC9A",
            "https://youtube.com./", "https://www.youtube.com:8443/", "https://youtube.com@evil.test/",
            "https://evil.test@youtube.com/", "https://@youtube.com/", "https://youtube.com\\@evil.test/",
            "https://youtube.com\n/", " https://youtube.com/", "ftp://youtube.com/"
        };
        string[] falseResources = new string[]
        {
            null, "", "//i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg",
            "https://i.ytimg.com.evil.test/vi/dQw4w9WgXcQ/hqdefault.jpg",
            "https://evil-i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg",
            "https://i0.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg",
            "https://i10.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg",
            "https://i.ytimg.com./vi/dQw4w9WgXcQ/hqdefault.jpg",
            "https://i.ytimg.com:8443/vi/dQw4w9WgXcQ/hqdefault.jpg",
            "https://yt3.ggpht.com.evil.test/avatar.jpg", "https://googleusercontent.com/avatar.jpg",
            "https://lh3.googleusercontent.com/avatar.jpg", "https://yt3.ggpht.com/",
            "https://evil.test@i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg",
            "https://@yt3.googleusercontent.com/avatar", "https://yt3.ggpht.com@evil.test/avatar",
            "https://i.ytimg.com\\@evil.test/vi/dQw4w9WgXcQ/hqdefault.jpg",
            "https://i.ytimg.com/vi/short/hqdefault.jpg", "https://i.ytimg.com/vi/dQw4w9WgXcQ/",
            "https://i.ytimg.com/vi/dQw4w9WgXcQ/player.js", "https://i.ytimg.com/vi/dQw4w9WgXcQ/video.mp4",
            "https://i.ytimg.com/vi/dQw4w9WgXcQ/sub/path.jpg", "https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg/extra",
            "https://i.ytimg.com/vi/dQw4w9WgXcQ/%2Fasset.jpg", "https://i.ytimg.com/vi/dQw4w9WgXcQ/../../yts/img/logo.jpg",
            "https://i.ytimg.com/sb/dQw4w9WgXcQ/storyboard3_L0/M0.jpg",
            "https://i.ytimg.com/an_webp/dQw4w9WgXcQ/mqdefault_6s.webp",
            "https://i.ytimg.com/yts/img/logo.png", "https://i.ytimg.com/yts/img/favicon_32-vflOogEID.png",
            "https://www.youtube.com/s/player/abcd/player_ias.vflset/en_US/base.js",
            "https://rr1---sn.example.googlevideo.com/videoplayback?id=123",
            "https://i.ytimg.com/vi/YQHsXMglC9A/hqdefault.jpg",
            "https://i.ytimg.com/vi_webp/YQHsXMglC9A/maxresdefault.webp"
        };
        string[,] currentVideo = new string[,]
        {
            { "https://youtube.com/shorts/dQw4w9WgXcQ", Other },
            { "https://m.youtube.com/shorts/dQw4w9WgXcQ/", Other },
            { "https://youtube.com/embed/dQw4w9WgXcQ?autoplay=1", Other },
            { "https://youtube.com/watch?list=123&v=dQw4w9WgXcQ", Other },
            { "https://youtube.com/watch?%76=dQw4w9WgXcQ", Other },
            { "https://youtube.com/watch?v=%64Qw4w9WgXcQ", Other },
            { "https://youtube.com/watch?v=YQHsXMglC9A&v=dQw4w9WgXcQ", Other }
        };
        int failures = 0, cases = 0;
        for (int i = 0; i < blocked.GetLength(0); i++)
        {
            Check(blocked[i, 0], blocked[i, 1], true, ref cases, ref failures);
            CheckPage(blocked[i, 0], true, ref cases, ref failures);
        }
        for (int i = 0; i < falseTops.Length; i++)
        {
            Check(falseTops[i], Other, false, ref cases, ref failures);
            CheckPage(falseTops[i], false, ref cases, ref failures);
        }
        for (int i = 0; i < falseResources.Length; i++)
            Check(Top, falseResources[i], false, ref cases, ref failures);
        for (int i = 0; i < currentVideo.GetLength(0); i++)
            Check(currentVideo[i, 0], currentVideo[i, 1], false, ref cases, ref failures);
        Console.WriteLine("{0}: {1} YouTube image policy cases; {2} failures.", failures == 0 ? "PASS" : "FAIL", cases, failures);
        return failures == 0 ? 0 : 1;
    }

    private static void Check(string top, string resource, bool expected, ref int cases, ref int failures)
    {
        cases++;
        bool actual = YouTubeImagePolicy.ShouldBlock(top, resource);
        if (actual == expected) return;
        failures++;
        Console.Error.WriteLine("FAIL expected={0}; actual={1}; top=[{2}]; resource=[{3}]", expected, actual, top, resource);
    }

    private static void CheckPage(string top, bool expected, ref int cases, ref int failures)
    {
        cases++;
        bool actual = YouTubeImagePolicy.IsYouTubePage(top);
        if (actual == expected) return;
        failures++;
        Console.Error.WriteLine("FAIL page expected={0}; actual={1}; top=[{2}]", expected, actual, top);
    }
}
