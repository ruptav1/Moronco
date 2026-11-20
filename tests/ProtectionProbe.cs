using System;
using System.Reflection;

class ProtectionProbe
{
    static int Main(string[] args)
    {
        var parse = Assembly.LoadFrom(args[0]).GetType("MiniumBrowser.BrowserHost").GetMethod("ParseWarpStatus", BindingFlags.NonPublic | BindingFlags.Static);
        string[][] cases = {
            new[] { "Status update: Connected", "(user set) Mode: WarpWithDns", "connected" },
            new[] { "Status update: Connected", "Mode: Warp", "connected" },
            new[] { "Status update: Connected", "Mode: TunnelOnly", "connected" },
            new[] { "Status update: Connected", "Mode: DnsOverHttps", "dns" },
            new[] { "Status update: Connected", "Mode: WarpProxy", "dns" },
            new[] { "Status update: Connected", "", "dns" },
            new[] { "Status update: Disconnected", "Mode: WarpWithDns", "disconnected" },
            new[] { "Status update: Connecting", "Mode: WarpWithDns", "connecting" },
            new[] { "Status update: Reconnecting", "Mode: WarpWithDns", "connecting" },
            new[] { "FailedToConnectToDaemon", "Mode: WarpWithDns", "unavailable" }
        };
        foreach (var item in cases)
            if ((string)parse.Invoke(null, new object[] { item[0], item[1] }) != item[2]) throw new Exception("Incorrect WARP state: " + item[0] + " / " + item[1]);
        Console.WriteLine("PASS 10 WARP state cases, including DNS-only and service failure.");
        return 0;
    }
}
