using System.Text;
using System.Text.Json;

public static class METADATA
{
    private static readonly Dictionary<string, string> Env = LoadEnv();

    private static Dictionary<string, string> LoadEnv()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // look next to the executable first, then working directory
        var paths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), ".env")
        };

        var path = paths.FirstOrDefault(File.Exists);
        if (path is null) return dict;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var idx = line.IndexOf('=');
            if (idx <= 0) continue;

            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim();

            // strip surrounding quotes if present
            if (val.Length >= 2 && val.StartsWith('"') && val.EndsWith('"'))
                val = val[1..^1];

            dict[key] = val;
        }
        return dict;
    }

    private static string Meta(string key, string fallback) =>
        Env.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;

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
    public static readonly bool RUNNINGLOCAL = MetaBool("RunningLocal", true);

    public static readonly Dictionary<string, string> BRAIN_HEADERS = MetaHeaders("BrainHeaders");
    public static readonly Dictionary<string, string> EARS_HEADERS  = MetaHeaders("EarsHeaders");
    public static readonly Dictionary<string, string> MOUTH_HEADERS = MetaHeaders("MouthHeaders");
}
