using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimplePrint.Common;

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static T LoadOrCreate<T>(string path, Func<T> factory) where T : class
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            var item = factory();
            Save(path, item);
            return item;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? factory();
        }
        catch
        {
            var backup = path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Copy(path, backup, true); } catch { }
            var item = factory();
            Save(path, item);
            return item;
        }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, true);
    }
}
