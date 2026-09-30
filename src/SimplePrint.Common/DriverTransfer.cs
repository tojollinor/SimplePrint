using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimplePrint.Common;

/// <summary>
/// Architektur-Hilfen für die Treiberübertragung. Ein Druckertreiber darf nur
/// übertragen werden, wenn seine Architektur zum Betriebssystem des Clients passt.
/// </summary>
public static class DriverArchitecture
{
    public const string X64 = "x64";
    public const string Arm64 = "arm64";
    public const string X86 = "x86";
    public const string Unknown = "unbekannt";

    /// <summary>Architektur des Betriebssystems (nicht des Prozesses).</summary>
    public static string Local => FromArchitecture(RuntimeInformation.OSArchitecture);

    public static string FromArchitecture(Architecture architecture) =>
        architecture switch
        {
            Architecture.X64 => X64,
            Architecture.Arm64 => Arm64,
            Architecture.X86 => X86,
            _ => Unknown
        };

    /// <summary>
    /// Wandelt die Windows-Druckerumgebung ("Windows x64", "Windows ARM64",
    /// "Windows NT x86") in eine Architektur um.
    /// </summary>
    public static string FromPrinterEnvironment(string? environment)
    {
        if (string.IsNullOrWhiteSpace(environment))
            return Unknown;

        if (environment.Contains("ARM64", StringComparison.OrdinalIgnoreCase))
            return Arm64;

        if (environment.Contains("x64", StringComparison.OrdinalIgnoreCase))
            return X64;

        if (environment.Contains("x86", StringComparison.OrdinalIgnoreCase))
            return X86;

        return Unknown;
    }

    public static string Describe(string? architecture) =>
        (architecture ?? "").Trim().ToLowerInvariant() switch
        {
            X64 => "64-Bit (x64)",
            Arm64 => "64-Bit (ARM64)",
            X86 => "32-Bit (x86)",
            _ => "unbekannt"
        };

    public static bool IsKnown(string? architecture) =>
        (architecture ?? "").Trim().ToLowerInvariant() is X64 or Arm64 or X86;
}

public static class DriverStates
{
    /// <summary>Der Server kann den Treiber bereitstellen und wartet auf die Bestätigung.</summary>
    public const string Offer = "Offer";

    /// <summary>Der Server bereitet das Paket vor.</summary>
    public const string Preparing = "Preparing";

    /// <summary>Das Paket folgt unmittelbar nach dieser Nachricht.</summary>
    public const string Sending = "Sending";

    public const string Unavailable = "Unavailable";
    public const string ArchitectureMismatch = "ArchitectureMismatch";
    public const string NotAuthorized = "NotAuthorized";
    public const string Denied = "Denied";
    public const string Timeout = "Timeout";
    public const string Busy = "Busy";
    public const string Error = "Error";
}

public sealed class DriverRequestMessage
{
    public string DriverName { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string ClientArchitecture { get; set; } = "";
}

public sealed class DriverResponseMessage
{
    public string State { get; set; } = "";
    public string Message { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string ServerArchitecture { get; set; } = "";
    public string InfFileName { get; set; } = "";
    public long PackageBytes { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class DriverTransferException : InvalidOperationException
{
    public string State { get; }

    public DriverTransferException(string state, string message)
        : base(message)
    {
        State = state;
    }
}

public sealed record DriverDownload(string ZipPath, DriverResponseMessage Info);

/// <summary>
/// Wire-Protokoll der einmaligen Treiberübertragung (TCP 45883):
/// 40-Byte-Header ("SPRV", Version, Anfrager-GUID), danach längenpräfixierte
/// JSON-Nachrichten. Auf die Nachricht "Sending" folgt das Paket (ZIP) bytegenau.
/// </summary>
public static class DriverTransferProtocol
{
    public const int Port = 45883;
    public const int Version = 1;
    public const int HeaderLength = 40;
    public const int MaxMessageBytes = 64 * 1024;
    public const long MaxPackageBytes = 700L * 1024 * 1024;
    public const string FirewallRuleName = "SimplePrint-DriverTransfer";

    public static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(5);

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SPRV");

    public static byte[] CreateHeader(Guid requesterId)
    {
        var data = new byte[HeaderLength];
        Magic.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), Version);
        requesterId.TryWriteBytes(data.AsSpan(8, 16));
        return data;
    }

    public static bool TryParseHeader(ReadOnlySpan<byte> data, out Guid requesterId)
    {
        requesterId = Guid.Empty;
        if (data.Length != HeaderLength) return false;
        if (!data[..4].SequenceEqual(Magic)) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4)) != Version) return false;

        requesterId = new Guid(data.Slice(8, 16));
        return requesterId != Guid.Empty;
    }

    public static async Task WriteMessageAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonStore.Options);
        if (payload.Length > MaxMessageBytes)
            throw new InvalidDataException("Die Nachricht ist zu groß.");

        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<T?> ReadMessageAsync<T>(Stream stream, CancellationToken ct)
        where T : class
    {
        var length = new byte[4];
        if (!await Protocol.ReadExactAsync(stream, length, ct))
            return null;

        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size <= 0 || size > MaxMessageBytes)
            return null;

        var payload = new byte[size];
        if (!await Protocol.ReadExactAsync(stream, payload, ct))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonStore.Options);
        }
        catch
        {
            return null;
        }
    }
}

// ---------------------------------------------------------------------------
// Server: ausstehende Anfragen (Dienst schreibt, Oberfläche fragt den Benutzer)
// ---------------------------------------------------------------------------

public sealed class DriverRequestRecord
{
    public Guid Id { get; set; }
    public Guid RequesterId { get; set; }
    public string RequesterName { get; set; } = "";
    public string RequesterAddress { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public string Architecture { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class DriverDecision
{
    public Guid Id { get; set; }
    public bool Approved { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}

/// <summary>
/// Dateibasierter Austausch zwischen Dienst (Sitzung 0, keine Oberfläche) und
/// SimplePrint-Oberfläche des angemeldeten Benutzers. Pro Anfrage gibt es eine
/// .request.json (vom Dienst) und eine .decision.json (von der Oberfläche); so
/// schreiben nie zwei Prozesse dieselbe Datei.
/// </summary>
public static class DriverRequestStore
{
    public static string RequestsDirectory =>
        Path.Combine(AppPaths.DeviceRoot, "DriverRequests");

    public static string RequestPath(Guid id) =>
        Path.Combine(RequestsDirectory, id.ToString("N") + ".request.json");

    public static string DecisionPath(Guid id) =>
        Path.Combine(RequestsDirectory, id.ToString("N") + ".decision.json");

    public static void SaveRequest(DriverRequestRecord record)
    {
        Directory.CreateDirectory(RequestsDirectory);
        JsonStore.Save(RequestPath(record.Id), record);
    }

    public static List<DriverRequestRecord> LoadPending()
    {
        var result = new List<DriverRequestRecord>();
        if (!Directory.Exists(RequestsDirectory))
            return result;

        foreach (var file in Directory.EnumerateFiles(RequestsDirectory, "*.request.json"))
        {
            var record = TryRead<DriverRequestRecord>(file);
            if (record is null || record.Id == Guid.Empty)
                continue;

            if (record.ExpiresAt < DateTimeOffset.Now)
                continue;

            if (File.Exists(DecisionPath(record.Id)))
                continue;

            result.Add(record);
        }

        return result;
    }

    public static DriverDecision? ReadDecision(Guid id) =>
        TryRead<DriverDecision>(DecisionPath(id));

    public static void WriteDecision(Guid id, bool approved)
    {
        Directory.CreateDirectory(RequestsDirectory);

        var path = DecisionPath(id);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        var json = JsonSerializer.Serialize(
            new DriverDecision
            {
                Id = id,
                Approved = approved,
                DecidedAt = DateTimeOffset.Now
            },
            JsonStore.Options);

        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static void Cleanup(Guid id)
    {
        foreach (var path in new[]
                 {
                     RequestPath(id),
                     RequestPath(id) + ".bak",
                     DecisionPath(id)
                 })
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }

    /// <summary>Entfernt alle Dateien (beim Start des Dienstes) oder nur alte Reste.</summary>
    public static void CleanupAll(bool onlyStale = false)
    {
        try
        {
            if (!Directory.Exists(RequestsDirectory))
                return;

            var limit = DateTime.UtcNow.AddMinutes(-30);

            foreach (var file in Directory.EnumerateFiles(RequestsDirectory))
            {
                try
                {
                    if (!onlyStale || File.GetLastWriteTimeUtc(file) < limit)
                        File.Delete(file);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static T? TryRead<T>(string path) where T : class
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return JsonSerializer.Deserialize<T>(stream, JsonStore.Options);
        }
        catch
        {
            return null;
        }
    }
}

// ---------------------------------------------------------------------------
// Server: Treiber suchen und als Paket exportieren
// ---------------------------------------------------------------------------

public sealed class ServerDriverInfo
{
    public string Name { get; set; } = "";
    public string InfPath { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Version { get; set; } = "";

    [JsonIgnore]
    public string Architecture => DriverArchitecture.FromPrinterEnvironment(Environment);
}

public static class DriverExporter
{
    /// <summary>
    /// Liefert alle installierten Varianten (x64, ARM64, x86) des Treibers mit genau
    /// diesem Namen. Es wird nie ein vom Client geliefierter Pfad verwendet.
    /// </summary>
    public static async Task<List<ServerDriverInfo>> GetDriversAsync(string driverName)
    {
        var script =
            "$name=" + PowerShellRunner.Quote(driverName) + System.Environment.NewLine +
            """
            $ErrorActionPreference='Stop'
            $items = @(Get-PrinterDriver -Name $name -ErrorAction SilentlyContinue |
              Where-Object { ([string]$_.Name) -ieq $name } |
              ForEach-Object {
                [pscustomobject]@{
                  Name=[string]$_.Name
                  InfPath=[string]$_.InfPath
                  Environment=[string]$_.PrinterEnvironment
                  Manufacturer=[string]$_.Manufacturer
                  Version=[string]$_.DriverVersion
                }
              })
            ConvertTo-Json -InputObject $items -Compress
            """;

        var result = await PowerShellRunner.RunAsync(
            script,
            timeout: TimeSpan.FromSeconds(60));

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.StdErr)
                    ? "Die installierten Druckertreiber konnten nicht gelesen werden."
                    : result.StdErr.Trim());
        }

        var text = result.StdOut.Trim();
        if (text.Length == 0)
            return [];

        return JsonSerializer.Deserialize<List<ServerDriverInfo>>(text, JsonStore.Options) ?? [];
    }

    /// <summary>
    /// Der Treiber muss als Paket im Windows-Treiberspeicher (DriverStore\FileRepository)
    /// liegen. Nur dann ist er vollständig und signiert übertragbar.
    /// </summary>
    public static string GetPackageDirectory(ServerDriverInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.InfPath))
        {
            throw new InvalidOperationException(
                "Windows meldet für diesen Treiber keine INF-Datei. Er kann nicht als Paket übertragen werden.");
        }

        var inf = Path.GetFullPath(info.InfPath);
        var packageDir = Path.GetDirectoryName(inf) ?? "";

        var repository = Path.GetFullPath(Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.System),
            "DriverStore",
            "FileRepository"));

        if (!string.Equals(
                Path.GetDirectoryName(packageDir),
                repository,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Der Treiber liegt nicht als Treiberpaket im Windows-Treiberspeicher " +
                "(DriverStore\\FileRepository) und kann deshalb nicht übertragen werden.");
        }

        if (!File.Exists(inf) || !Directory.Exists(packageDir))
        {
            throw new InvalidOperationException(
                "Das Treiberpaket wurde im Treiberspeicher des Servers nicht gefunden.");
        }

        return packageDir;
    }

    public static async Task<(string ZipPath, string Sha256, long Bytes)> CreatePackageAsync(
        ServerDriverInfo info,
        CancellationToken ct)
    {
        var packageDir = GetPackageDirectory(info);

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(packageDir, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
            if (total > DriverTransferProtocol.MaxPackageBytes)
            {
                throw new InvalidOperationException(
                    $"Das Treiberpaket ist größer als {DriverTransferProtocol.MaxPackageBytes / (1024 * 1024)} MB und wird nicht übertragen.");
            }
        }

        var zipPath = Path.Combine(
            Path.GetTempPath(),
            $"SimplePrint-Driver-{Guid.NewGuid():N}.zip");

        try
        {
            await Task.Run(
                () => ZipFile.CreateFromDirectory(
                    packageDir,
                    zipPath,
                    CompressionLevel.Fastest,
                    includeBaseDirectory: false),
                ct);

            await using var stream = File.OpenRead(zipPath);
            var hash = await SHA256.HashDataAsync(stream, ct);

            return (
                zipPath,
                Convert.ToHexString(hash).ToLowerInvariant(),
                new FileInfo(zipPath).Length);
        }
        catch
        {
            try
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
            }
            catch
            {
            }

            throw;
        }
    }
}

// ---------------------------------------------------------------------------
// Client: Treiberpaket beim Server anfordern und herunterladen
// ---------------------------------------------------------------------------

public static class DriverTransferClient
{
    public static async Task<DriverDownload> DownloadAsync(
        string serverAddress,
        Guid ownDeviceId,
        DriverRequestMessage request,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var localArchitecture = DriverArchitecture.Local;
        request.ClientArchitecture = localArchitecture;

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(DriverTransferProtocol.ApprovalTimeout + TimeSpan.FromMinutes(12));

        using var tcp = new TcpClient { NoDelay = true };

        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(overall.Token))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));

            try
            {
                await tcp.ConnectAsync(
                    serverAddress,
                    DriverTransferProtocol.Port,
                    connectTimeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DriverTransferException(
                    DriverStates.Error,
                    $"Der Server {serverAddress} ist für die Treiberübertragung nicht erreichbar " +
                    $"(TCP {DriverTransferProtocol.Port}). Läuft dort der SimplePrint-Dienst mit " +
                    "mindestens einem freigegebenen Drucker, und ist die Firewall offen?");
            }
            catch (SocketException ex)
            {
                throw new DriverTransferException(
                    DriverStates.Error,
                    $"Der Server {serverAddress} ist für die Treiberübertragung nicht erreichbar: {ex.Message}");
            }
        }

        using var stream = tcp.GetStream();

        await stream.WriteAsync(DriverTransferProtocol.CreateHeader(ownDeviceId), overall.Token);
        await DriverTransferProtocol.WriteMessageAsync(stream, request, overall.Token);

        while (true)
        {
            var message = await DriverTransferProtocol.ReadMessageAsync<DriverResponseMessage>(
                                stream,
                                overall.Token)
                            ?? throw new DriverTransferException(
                                DriverStates.Error,
                                "Der Server hat die Verbindung ohne Antwort beendet.");

            switch (message.State)
            {
                case DriverStates.Offer:
                    // Architekturprüfung auch hier, bevor am Server überhaupt gefragt wird.
                    EnsureArchitectureMatches(message, localArchitecture);
                    progress?.Report(message.Message);
                    continue;

                case DriverStates.Preparing:
                    progress?.Report(message.Message);
                    continue;

                case DriverStates.Sending:
                    EnsureArchitectureMatches(message, localArchitecture);
                    return await ReceivePackageAsync(stream, message, progress, overall.Token);

                default:
                    throw new DriverTransferException(
                        string.IsNullOrWhiteSpace(message.State) ? DriverStates.Error : message.State,
                        string.IsNullOrWhiteSpace(message.Message)
                            ? "Der Server hat die Anfrage abgelehnt."
                            : message.Message);
            }
        }
    }

    private static void EnsureArchitectureMatches(
        DriverResponseMessage message,
        string localArchitecture)
    {
        if (string.Equals(
                message.Architecture,
                localArchitecture,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new DriverTransferException(
            DriverStates.ArchitectureMismatch,
            $"Der Treiber passt nicht zu diesem PC: Der Treiber ist für {DriverArchitecture.Describe(message.Architecture)}, " +
            $"dieser PC ist {DriverArchitecture.Describe(localArchitecture)}. " +
            "Ein Treiber lässt sich nur zwischen gleichen Architekturen übertragen.");
    }

    private static async Task<DriverDownload> ReceivePackageAsync(
        Stream stream,
        DriverResponseMessage info,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (info.PackageBytes <= 0 || info.PackageBytes > DriverTransferProtocol.MaxPackageBytes)
        {
            throw new DriverTransferException(
                DriverStates.Error,
                "Der Server meldet eine ungültige Größe des Treiberpakets.");
        }

        var zipPath = Path.Combine(
            Path.GetTempPath(),
            $"SimplePrint-Driver-{Guid.NewGuid():N}.zip");

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            var remaining = info.PackageBytes;
            long received = 0;
            long lastReported = 0;

            await using (var file = new FileStream(
                             zipPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             useAsync: true))
            {
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                        ct);

                    if (read == 0)
                    {
                        throw new DriverTransferException(
                            DriverStates.Error,
                            "Das Treiberpaket wurde unvollständig übertragen.");
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    remaining -= read;
                    received += read;

                    if (received - lastReported >= 512 * 1024 || remaining == 0)
                    {
                        lastReported = received;
                        progress?.Report(
                            $"Treiberpaket wird übertragen … {received / 1048576.0:0.0} von {info.PackageBytes / 1048576.0:0.0} MB");
                    }
                }
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new DriverTransferException(
                    DriverStates.Error,
                    "Die Prüfsumme des Treiberpakets stimmt nicht. Die Übertragung wird verworfen.");
            }

            return new DriverDownload(zipPath, info);
        }
        catch
        {
            try
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
            }
            catch
            {
            }

            throw;
        }
    }
}
