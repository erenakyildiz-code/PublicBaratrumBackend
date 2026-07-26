using System.Reflection;
using System.Text;
using System.Text.Json;

public static class METADATA
{
    private static string Meta(string key, string fallback) =>
        typeof(METADATA).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value ?? fallback;

    public static bool MetaBool(string key, bool fallback) =>
        bool.TryParse(Meta(key, ""), out var b) ? b : fallback;

    private static Dictionary<string, string> MetaHeaders(string key)
    {
        var b64 = Meta(key, "");
        if (string.IsNullOrWhiteSpace(b64)) return new();
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch { return new(); }
    }

    public static readonly string BRAIN = Meta("BrainEndpoint", "http://127.0.0.1:8080");
    public static readonly string EARS  = Meta("EarsEndpoint",  "http://127.0.0.1:8000");
    public static readonly string MOUTH = Meta("MouthEndpoint", "https://devil-macbook-air-2.tail1737c0.ts.net");
    public static readonly bool RUNNINGLOCAL = MetaBool("RunningLocal", false);

    public static readonly Dictionary<string, string> BRAIN_HEADERS = MetaHeaders("BrainHeaders");
    public static readonly Dictionary<string, string> EARS_HEADERS  = MetaHeaders("EarsHeaders");
    public static readonly Dictionary<string, string> MOUTH_HEADERS = MetaHeaders("MouthHeaders");
}