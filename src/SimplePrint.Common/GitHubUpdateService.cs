using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace SimplePrint.Common;

public enum SimplePrintComponent
{
    Server,
    Client
}

public sealed record ReleaseUpdateInfo(
    SimplePrintComponent Component,
    Version Version,
    string TagName,
    string ReleaseUrl,
    string InstallerUrl,
    string InstallerName,
    long InstallerSize,
    string? Sha256,
    string? Notes);

public sealed record UpdateApplyRequest(
    ReleaseUpdateInfo Release,
    string TargetExecutablePath,
    string UpdaterDirectory);

public sealed record PreparedUpdateHost(
    string ExecutablePath,
    string RequestPath);

public static class GitHubUpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/tojollinor/SimplePrint/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("SimplePrint", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    public static async Task<ReleaseUpdateInfo?> CheckAsync(
        Version currentVersion,
        SimplePrintComponent component,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        using var response = await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;

        if (!root.TryGetProperty("tag_name", out var tagElement))
            return null;

        var tagName = tagElement.GetString();
        if (!TryParseVersion(tagName, out var releaseVersion))
            return null;

        if (Normalize(releaseVersion) <= Normalize(currentVersion))
            return null;

        var releaseUrl = root.TryGetProperty("html_url", out var releaseUrlElement)
            ? releaseUrlElement.GetString() ?? "https://github.com/tojollinor/SimplePrint/releases"
            : "https://github.com/tojollinor/SimplePrint/releases";

        var notes = root.TryGetProperty("body", out var notesElement) &&
                    notesElement.ValueKind != JsonValueKind.Null
            ? notesElement.GetString()
            : null;

        if (!root.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
            return null;

        var expectedPrefix = component == SimplePrintComponent.Server
            ? "SimplePrint-Server-Setup-"
            : "SimplePrint-Client-Setup-";

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name) ||
                !name.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement)
                ? urlElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(downloadUrl))
                continue;

            var size = asset.TryGetProperty("size", out var sizeElement) &&
                       sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize
                : 0;

            string? sha256 = null;
            if (asset.TryGetProperty("digest", out var digestElement) &&
                digestElement.ValueKind == JsonValueKind.String)
            {
                var digest = digestElement.GetString();
                if (!string.IsNullOrWhiteSpace(digest) &&
                    digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha256 = digest["sha256:".Length..];
            }

            return new ReleaseUpdateInfo(
                component,
                releaseVersion,
                tagName ?? releaseVersion.ToString(),
                releaseUrl,
                downloadUrl,
                name,
                size,
                sha256,
                notes);
        }

        return null;
    }

    public static async Task<string> DownloadInstallerAsync(
        ReleaseUpdateInfo release,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var safeName = Path.GetFileName(release.InstallerName);
        var expectedPrefix = release.Component == SimplePrintComponent.Server
            ? "SimplePrint-Server-Setup-"
            : "SimplePrint-Client-Setup-";

        if (string.IsNullOrWhiteSpace(safeName) ||
            !safeName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !safeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Der Release enthält keinen gültigen SimplePrint-Installer.");

        var targetDirectory = Path.Combine(
            Path.GetTempPath(),
            "SimplePrint",
            "Updates",
            release.TagName);

        Directory.CreateDirectory(targetDirectory);
        var targetPath = Path.Combine(targetDirectory, safeName);
        var temporaryPath = targetPath + ".download";

        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);

        using var response = await Http.GetAsync(
            release.InstallerUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         1024 * 128,
                         useAsync: true))
        {
            var buffer = new byte[1024 * 128];
            long written = 0;

            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;

                if (total is > 0)
                    progress?.Report((int)Math.Clamp(written * 100 / total.Value, 0, 100));
            }
        }

        if (release.InstallerSize > 0)
        {
            var actualSize = new FileInfo(temporaryPath).Length;
            if (actualSize != release.InstallerSize)
            {
                File.Delete(temporaryPath);
                throw new InvalidDataException(
                    $"Der heruntergeladene Installer ist unvollständig ({actualSize:N0} statt {release.InstallerSize:N0} Bytes).");
            }
        }

        if (!string.IsNullOrWhiteSpace(release.Sha256))
        {
            await using var verifyStream = File.OpenRead(temporaryPath);
            var hash = await SHA256.HashDataAsync(verifyStream, cancellationToken);
            var actualHash = Convert.ToHexString(hash);

            if (!actualHash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporaryPath);
                throw new InvalidDataException("Die SHA-256-Prüfsumme des Updates stimmt nicht mit GitHub überein.");
            }
        }

        File.Move(temporaryPath, targetPath, true);
        progress?.Report(100);
        return targetPath;
    }

    public static void LaunchElevatedBootstrap(string executablePath)
    {
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("Die SimplePrint-Anwendung wurde nicht gefunden.", executablePath);

        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            Verb = "runas"
        };
        psi.ArgumentList.Add("--bootstrap-update");

        try
        {
            _ = Process.Start(psi)
                ?? throw new InvalidOperationException("Der administrative Update-Prozess konnte nicht gestartet werden.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Die Administratorfreigabe wurde abgebrochen.", ex);
        }
    }

    public static async Task<PreparedUpdateHost> PrepareDetachedUpdateHostAsync(
        Version currentVersion,
        SimplePrintComponent component,
        string installedExecutablePath,
        CancellationToken cancellationToken = default)
    {
        var release = await CheckAsync(currentVersion, component, cancellationToken)
            ?? throw new InvalidOperationException("Das angebotene Update ist nicht mehr verfügbar.");

        var executableDirectory = Path.GetDirectoryName(installedExecutablePath)
            ?? throw new InvalidOperationException("Das Installationsverzeichnis konnte nicht ermittelt werden.");

        var componentDirectory = Directory.GetParent(executableDirectory)?.FullName
            ?? throw new InvalidOperationException("Das SimplePrint-Komponentenverzeichnis konnte nicht ermittelt werden.");

        var appRoot = Directory.GetParent(componentDirectory)?.FullName
            ?? throw new InvalidOperationException("Das SimplePrint-Installationsverzeichnis konnte nicht ermittelt werden.");

        var updaterDirectory = Path.Combine(
            appRoot,
            "UpdaterTemp",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(updaterDirectory);

        var updaterExecutablePath = Path.Combine(
            updaterDirectory,
            Path.GetFileName(installedExecutablePath));

        File.Copy(installedExecutablePath, updaterExecutablePath, overwrite: true);

        var request = new UpdateApplyRequest(
            release,
            installedExecutablePath,
            updaterDirectory);

        var requestPath = Path.Combine(updaterDirectory, "update-request.json");
        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            }),
            new UTF8Encoding(false),
            cancellationToken);

        return new PreparedUpdateHost(updaterExecutablePath, requestPath);
    }

    public static void LaunchDetachedUpdateHost(PreparedUpdateHost host)
    {
        if (!File.Exists(host.ExecutablePath) || !File.Exists(host.RequestPath))
            throw new InvalidOperationException("Der vorbereitete Update-Prozess ist unvollständig.");

        var psi = new ProcessStartInfo
        {
            FileName = host.ExecutablePath,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("--apply-update");
        psi.ArgumentList.Add(host.RequestPath);

        _ = Process.Start(psi)
            ?? throw new InvalidOperationException("Der Update-Prozess konnte nicht gestartet werden.");
    }

    public static UpdateApplyRequest LoadUpdateRequest(string requestPath)
    {
        if (!File.Exists(requestPath))
            throw new FileNotFoundException("Die Update-Anforderung wurde nicht gefunden.", requestPath);

        var request = JsonSerializer.Deserialize<UpdateApplyRequest>(
            File.ReadAllText(requestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        return request
            ?? throw new InvalidDataException("Die Update-Anforderung ist ungültig.");
    }

    public static async Task<int> InstallSilentlyAsync(
        string installerPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(installerPath))
            throw new FileNotFoundException("Der Update-Installer wurde nicht gefunden.", installerPath);

        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in new[]
        {
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/CLOSEAPPLICATIONS",
            "/NORESTARTAPPLICATIONS",
            "/SP-"
        })
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Der stille Update-Installer konnte nicht gestartet werden.");

        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    public static void RelaunchAfterSuccessfulUpdate(UpdateApplyRequest request)
    {
        if (!File.Exists(request.TargetExecutablePath))
            throw new FileNotFoundException(
                "Die aktualisierte SimplePrint-Anwendung wurde nicht gefunden.",
                request.TargetExecutablePath);

        var version = request.Release.TagName;

        var psi = new ProcessStartInfo
        {
            FileName = request.TargetExecutablePath,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("--update-success");
        psi.ArgumentList.Add(version);

        _ = Process.Start(psi)
            ?? throw new InvalidOperationException("SimplePrint konnte nach dem Update nicht neu gestartet werden.");
    }

    public static void ScheduleUpdaterCleanup(UpdateApplyRequest request)
    {
        try
        {
            var command =
                $"ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{request.UpdaterDirectory}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c " + command,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch
        {
        }
    }

    public static void LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
            throw new FileNotFoundException("Der Update-Installer wurde nicht gefunden.", installerPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true
        });
    }

    private static bool TryParseVersion(string? tagName, out Version version)
    {
        version = new Version(0, 0, 0, 0);

        if (string.IsNullOrWhiteSpace(tagName))
            return false;

        var value = tagName.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
            value = value[1..];

        var suffix = value.IndexOfAny(['-', '+']);
        if (suffix >= 0)
            value = value[..suffix];

        if (!Version.TryParse(value, out var parsed))
            return false;

        version = Normalize(parsed);
        return true;
    }

    private static Version Normalize(Version value) =>
        new(
            Math.Max(0, value.Major),
            Math.Max(0, value.Minor),
            Math.Max(0, value.Build),
            Math.Max(0, value.Revision));
}