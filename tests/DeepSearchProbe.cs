using System;
using System.Reflection;
class DeepSearchProbe
{
    static int Main(string[] args)
    {
        var host = Assembly.LoadFrom(args[0]).GetType("MiniumBrowser.BrowserHost");
        var query = host.GetMethod("DeepQuery",BindingFlags.Static|BindingFlags.NonPublic);
        var exact = (string)query.Invoke(null,new object[]{" tài liệu ","exact","Example.org","bản cũ","pdf"});
        if(exact != "\"tài liệu\" site:example.org -\"bản cũ\" filetype:pdf") throw new Exception("Incorrect exact query");
        var broad = (string)query.Invoke(null,new object[]{"C++ & # 日本語","broad","","",""});
        if(broad != "C++ & # 日本語") throw new Exception("Broad query changed");
        foreach(var site in new[]{"https://example.org", "example.org other", "example.org/path", "example.org&x=y"})
        {
            bool rejected=false;
            try { query.Invoke(null,new object[]{"test","broad",site,"",""}); }
            catch(TargetInvocationException e) { rejected=e.InnerException is ArgumentException; }
            if(!rejected) throw new Exception("Invalid domain accepted");
        }
        var page = host.GetMethod("DeepPageUrl",BindingFlags.Static|BindingFlags.NonPublic);
        if((string)page.Invoke(null,new object[]{"https://example.org/search?q=a%26b",2,"Brave"})!="https://example.org/search?q=a%26b&offset=1") throw new Exception("Brave pagination failed");
        if((string)page.Invoke(null,new object[]{"https://example.org/search?q=a%26b",5,"SearXNG"})!="https://example.org/search?q=a%26b&pageno=5") throw new Exception("SearXNG pagination failed");
        Console.WriteLine("PASS deep exact/broad queries, Unicode, domain validation and pagination.");
        return 0;
    }
}
