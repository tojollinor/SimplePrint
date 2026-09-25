using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SimplePrint.Common;

public static class ClientDiagnosticsBuilder
{
    private sealed class LocalReadiness
    {
        public bool Ready { get; set; }
        public string Detail { get; set; } = "";
        public List<string> Problems { get; set; } = [];
        public List<string> Warnings { get; set; } = [];
    }

    public static async Task<byte[]> CreateArchiveAsync(
        ClientConfig config,
        IReadOnlyList<DiscoveredServer> servers,
        CancellationToken ct = default)
    {
        await using var memory = new MemoryStream();

        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddFile(zip, AppPaths.ClientConfig, "config.json");
            AddFile(zip, AppPaths.ClientLog, "client.log");
            AddFile(zip, AppPaths.ClientJobs, "jobs.json");

            var diagnostics = zip.CreateEntry("diagnostics.txt", CompressionLevel.Optimal);
            await using (var stream = diagnostics.Open())
            await using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                await writer.WriteAsync(await GetLocalDiagnosticsAsync());
            }

            var discovery = zip.CreateEntry("discovery.txt", CompressionLevel.Optimal);
            await using (var stream = discovery.Open())
            await using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                foreach (var server in servers.OrderBy(x => x.Announcement.ServerName))
                {
                    await writer.WriteLineAsync(
                        $"{server.Announcement.ServerName} {server.Address}:{server.Announcement.GatewayPort} " +
                        $"app={server.Announcement.AppVersion} protocol={server.Announcement.Version} " +
                        $"compatible={server.Announcement.Version == Protocol.Version} " +
                        $"id={server.Announcement.ServerId} printers={server.Announcement.Printers.Count}");
                }
            }

            var healthResults = new List<PrinterHealthStatus>();

            foreach (var mapping in config.Mappings)
            {
                try
                {
                    var local = await GetLocalReadinessAsync(mapping);
                    var health = await QueryServerHealthAsync(mapping, servers, ct);

                    health.ClientTransportStatus = local.Detail;
                    health.ClientTransportReady = local.Ready;
                    health.ServerTransportStatus =
                        $"Server '{mapping.ServerName}' · Protokoll {Protocol.Version}";
                    health.ServerTransportReady = true;

                    foreach (var warning in local.Warnings)
                    {
                        if (!health.Warnings.Contains(warning, StringComparer.OrdinalIgnoreCase))
                            health.Warnings.Add(warning);
                    }

                    if (!local.Ready)
                    {
                        health.Level = "Red";
                        var cause = local.Problems.FirstOrDefault();
                        health.Summary = string.IsNullOrWhiteSpace(cause)
                            ? "Nicht druckbereit: Lokaler Client-Druckpfad fehlerhaft."
                            : $"Nicht druckbereit: {cause}";
                    }

                    healthResults.Add(health);
                }
                catch (Exception ex)
                {
                    healthResults.Add(new PrinterHealthStatus
                    {
                        PrinterId = mapping.PrinterId,
                        PrinterName = mapping.LocalPrinterName,
                        Level = "Red",
                        Summary = "End-to-End-Prüfung fehlgeschlagen.",
                        Warnings = [ex.Message],
                        CheckedAt = DateTimeOffset.Now
                    });
                }
            }

            var healthEntry = zip.CreateEntry("printer-health.json", CompressionLevel.Optimal);
            await using (var stream = healthEntry.Open())
            await using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                await writer.WriteAsync(
                    JsonSerializer.Serialize(healthResults, JsonStore.Options));
            }
        }

        return memory.ToArray();
    }

    private static async Task<LocalReadiness> GetLocalReadinessAsync(
        ClientPrinterMapping mapping)
    {
        if (PrinterTransport.IsDirect(mapping.TransportMode))
        {
            var script = $@"
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$address={PowerShellRunner.Quote(mapping.DirectAddress)}
$deviceUuid={PowerShellRunner.Quote(mapping.DeviceUuid)}
$p = Get-Printer -Name $printer -ErrorAction SilentlyContinue
$problems = @()
if(-not $p) {{ $problems += 'Direkte Windows-Druckerqueue fehlt.' }}

$target = if(-not [string]::IsNullOrWhiteSpace($address)) {{
  $address
}} elseif(-not [string]::IsNullOrWhiteSpace($deviceUuid)) {{
  'UUID ' + $deviceUuid
}} else {{
  'nicht verfügbar'
}}

[pscustomobject]@{{
  Ready = ($problems.Count -eq 0)
  Detail = 'Modus={mapping.TransportMode}; Queue=' + $(if($p){{'vorhanden'}}else{{'fehlt'}}) +
           '; Ziel=' + $target
  Problems = @($problems)
  Warnings = @($problems)
}} | ConvertTo-Json -Compress
";

            return await ReadLocalReadinessAsync(script);
        }

        var tunnelScript = $@"
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$port={PowerShellRunner.Quote(mapping.PortName)}
$proxyPort={mapping.LocalProxyPort}

$svc = Get-Service -Name SimplePrintClient -ErrorAction SilentlyContinue
$p = Get-Printer -Name $printer -ErrorAction SilentlyContinue
$pp = Get-PrinterPort -Name $port -ErrorAction SilentlyContinue
$listen = Get-NetTCPConnection -State Listen -LocalPort $proxyPort -ErrorAction SilentlyContinue
$queuePortMatches = ($p -and $p.PortName -eq $port)

$problems = @()
$warnings = @()

if(-not $svc -or [string]$svc.Status -ne 'Running') {{ $problems += 'SimplePrint Client-Agent läuft nicht.' }}
if(-not $p) {{ $problems += 'Windows-Druckerqueue fehlt.' }}
elseif($p.PortName -ne $port) {{ $problems += 'Windows-Drucker verwendet nicht den erwarteten SimplePrint-Port.' }}

if(-not $pp) {{ $problems += 'SimplePrint-Druckerport fehlt.' }}
if(-not $listen) {{ $problems += 'Lokaler SimplePrint-Proxy lauscht nicht auf Port ' + $proxyPort + '.' }}

if($p -and ([string]$p.DriverName -match 'Class Driver|Type1 Class|Type 1 Class|Microsoft IPP')) {{
  $warnings += 'Class-Treiber erkannt. Für den Tunnel muss exakt derselbe Treiber wie am Server verwendet werden.'
}}

[pscustomobject]@{{
  Ready = ($problems.Count -eq 0)
  Detail = 'Dienst=' + $(if($svc){{[string]$svc.Status}}else{{'fehlt'}}) +
           '; Queue=' + $(if($p){{'vorhanden'}}else{{'fehlt'}}) +
           '; Queue-Zuordnung=' + $(if($queuePortMatches){{'korrekt'}}elseif($p){{'falsch'}}else{{'nicht prüfbar'}}) +
           '; Port=' + $(if($pp){{'vorhanden'}}else{{'fehlt'}}) +
           '; Proxy=' + $(if($listen){{'lauscht'}}else{{'nicht aktiv'}})
  Problems = @($problems)
  Warnings = @($problems + $warnings)
}} | ConvertTo-Json -Compress
";

        return await ReadLocalReadinessAsync(tunnelScript);
    }

    private static async Task<LocalReadiness> ReadLocalReadinessAsync(string script)
    {
        var result = await PowerShellRunner.RunAsync(script);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.StdErr)
                    ? "Lokale Druckbereitschaft konnte nicht geprüft werden."
                    : result.StdErr.Trim());

        return JsonSerializer.Deserialize<LocalReadiness>(
                   result.StdOut,
                   JsonStore.Options)
               ?? new LocalReadiness
               {
                   Ready = false,
                   Detail = "Keine lokalen Statusdaten erhalten."
               };
    }

    private static async Task<PrinterHealthStatus> QueryServerHealthAsync(
        ClientPrinterMapping mapping,
        IReadOnlyList<DiscoveredServer> servers,
        CancellationToken ct)
    {
        var server = servers.FirstOrDefault(
            x => x.Announcement.ServerId == mapping.ServerId);

        if (server is null)
            throw new InvalidOperationException(
                $"Server '{mapping.ServerName}' wurde aktuell nicht gefunden.");

        if (server.Announcement.Version != Protocol.Version)
            throw new InvalidOperationException(
                $"Server '{server.Announcement.ServerName}' verwendet Protokoll " +
                $"{server.Announcement.Version}; benötigt wird {Protocol.Version}.");

        using var tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        await tcp.ConnectAsync(
            server.Address,
            server.Announcement.GatewayPort,
            timeout.Token);

        using var stream = tcp.GetStream();
        await stream.WriteAsync(
            Protocol.CreateGatewayHeader(mapping.PrinterId, Guid.Empty),
            timeout.Token);

        var health = await Protocol.ReadPrinterHealthAsync(stream, timeout.Token);

        return health ?? throw new InvalidOperationException(
            "Der Server hat keine gültige Druckerstatus-Antwort geliefert.");
    }

    private static async Task<string> GetLocalDiagnosticsAsync()
    {
        const string script = @"
$ErrorActionPreference='Continue'
'=== SYSTEM ==='
Get-ComputerInfo | Select-Object WindowsProductName,WindowsVersion,OsBuildNumber,CsName | Format-List | Out-String
'=== NETWORK PROFILE ==='
Get-NetConnectionProfile | Format-Table Name,InterfaceAlias,NetworkCategory,IPv4Connectivity,IPv6Connectivity -AutoSize | Out-String
'=== CLIENT AGENT ==='
Get-Service -Name SimplePrintClient -ErrorAction SilentlyContinue | Format-List * | Out-String
'=== SIMPLEPRINT PRINTERS ==='
Get-Printer | Where-Object { $_.PortName -Like 'SimplePrint_*' -or $_.Name -Like '* (SimplePrint)*' -or $_.Comment -Like 'SimplePrint:*' } | Format-Table Name,DriverName,PortName,PrinterStatus,Comment -AutoSize | Out-String
'=== PORTS ==='
Get-PrinterPort | Where-Object { $_.Name -Like 'SimplePrint_*' -or $_.Name -Like 'WSD-*' } | Select-Object Name,PrinterHostAddress,PortNumber,DeviceURL,DeviceUUID,SNMPEnabled | Format-Table -AutoSize | Out-String
'=== CLIENT DIAGNOSTICS LISTENER ==='
Get-NetTCPConnection -State Listen -LocalPort 45882 -ErrorAction SilentlyContinue | Format-Table -AutoSize | Out-String
'=== CLIENT DIAGNOSTICS FIREWALL ==='
Get-NetFirewallRule -Name 'SimplePrint-ClientDiagnostics' -ErrorAction SilentlyContinue | Select-Object Name,DisplayName,Enabled,Profile,Direction,Action | Format-List | Out-String
";

        var result = await PowerShellRunner.RunAsync(script);
        return result.StdOut + Environment.NewLine + result.StdErr;
    }

    private static void AddFile(
        ZipArchive zip,
        string path,
        string entryName)
    {
        if (File.Exists(path))
            zip.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
    }
}