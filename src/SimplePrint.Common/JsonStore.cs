using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimplePrint.Common;

public static class JsonStore
{
    private const int IoAttempts = 12;
    private static readonly TimeSpan IoRetryDelay = TimeSpan.FromMilliseconds(80);

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Lädt eine JSON-Datei. Kurzzeitige Zugriffskonflikte (GUI und Dienst greifen
    /// gleichzeitig zu) werden wiederholt und führen nie zum Überschreiben der Datei.
    /// Nur bei tatsächlich beschädigtem Inhalt wird zuerst die letzte gültige
    /// Sicherung (.bak) und erst danach die Standardvorgabe verwendet.
    /// </summary>
    public static T LoadOrCreate<T>(string path, Func<T> factory) where T : class
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var backupPath = BackupPath(path);

        var text = ReadAllTextWithRetry(path);

        if (text is null)
        {
            if (TryReadValid<T>(backupPath, out var restored))
            {
                Save(path, restored);
                return restored;
            }

            var created = factory();
            Save(path, created);
            return created;
        }

        if (TryDeserialize<T>(text, out var value))
            return value;

        PreserveCorrupt(path);

        if (TryReadValid<T>(backupPath, out var fromBackup))
        {
            Save(path, fromBackup);
            return fromBackup;
        }

        var fresh = factory();
        Save(path, fresh);
        return fresh;
    }

    /// <summary>
    /// Speichert atomar: eindeutige Temp-Datei schreiben, dann per File.Replace
    /// austauschen. Die vorherige Fassung bleibt als .bak erhalten.
    /// </summary>
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var json = JsonSerializer.Serialize(value, Options);
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

        try
        {
            using (var stream = new FileStream(
                       tmp,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                        File.Replace(tmp, path, BackupPath(path), ignoreMetadataErrors: true);
                    else
                        File.Move(tmp, path);

                    return;
                }
                catch (IOException) when (attempt < IoAttempts)
                {
                    Thread.Sleep(IoRetryDelay);
                }
                catch (UnauthorizedAccessException) when (attempt < IoAttempts)
                {
                    Thread.Sleep(IoRetryDelay);
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
            }
        }
    }

    private static string BackupPath(string path) => path + ".bak";

    private static string? ReadAllTextWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException) when (attempt < 4)
            {
                // Während File.Replace kann die Datei für einen Moment fehlen.
                Thread.Sleep(IoRetryDelay);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < IoAttempts)
            {
                Thread.Sleep(IoRetryDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < IoAttempts)
            {
                Thread.Sleep(IoRetryDelay);
            }
        }
    }

    private static bool TryDeserialize<T>(string text, out T value) where T : class
    {
        value = null!;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            var result = JsonSerializer.Deserialize<T>(text, Options);
            if (result is null)
                return false;

            value = result;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryReadValid<T>(string path, out T value) where T : class
    {
        value = null!;

        try
        {
            if (!File.Exists(path))
                return false;

            var text = ReadAllTextWithRetry(path);
            return text is not null && TryDeserialize(text, out value);
        }
        catch
        {
            return false;
        }
    }

    private static void PreserveCorrupt(string path)
    {
        try
        {
            var target = path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(path, target, true);
        }
        catch
        {
        }
    }
}
