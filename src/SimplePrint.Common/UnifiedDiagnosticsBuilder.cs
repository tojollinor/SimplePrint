using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SimplePrint.Common;

public static class UnifiedDiagnosticsBuilder
{
    private sealed class LocalReadiness
    {
        public bool Ready { get; set; }
        public string Detail { get; set; } = "";
        public List<string> Problems { get; set; } = [];
        public List<string> Warnings { get; set; } = [];
    }

    public static async Task<byte[]> CreateArchiveAsync(
        SimplePrintConfig config,
        IReadOnlyList<DevicePresence> activePeers,
        CancellationToken ct = default)
    {
        await using var memory = new MemoryStream();

        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddFile(zip, AppPaths.DeviceConfig, "config.json");
            AddFile(zip, AppPaths.DeviceLog, "simpleprint.log");
            AddFile(zip, AppPaths.DeviceJobs, "jobs.json");
            AddFile(zip, AppPaths.DevicePeers, "peers.json");

            var diagnostics = zip.CreateEntry("diagnostics.txt", CompressionLevel.Optimal);
            await using (var stream = diagnostics.Open())
            await using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                await writer.WriteAsync(await GetLocalDiagnosticsAsync(config));
            }

            var relationships = zip.CreateEntry("relationships.json", CompressionLevel.Optimal);
            await using (var stream = relationships.Open())
            await using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                var serverIds = config.NetworkPrinters
                    .Where(x => x.Enabled && x.SourceDeviceId != Guid.Empty)
                    .Select(x => x.SourceDeviceId)
                    .Distinct()
                    .ToHashSet();

                var payload = new
                {
                    ThisDevice = new
                    {
                        config.DeviceId,
                        config.DeviceName
                    },
                    Servers = activePeers
                        .Where(x => serverIds.Contains(x.DeviceId))
                        .OrderBy(x => x.DeviceName)
                        .ToList(),
                    Clients = activePeers
                        .Where(x => x.Subscriptions.Any(s =>
                            s.SourceDeviceId == config.DeviceId))
                        .OrderBy(x => x.DeviceName)
                        .ToList(),
                    ActivePeers = activePeers
                        .OrderBy(x => x.DeviceName)
                        .ToList()
                };

                await writer.WriteAsync(
                    JsonSerializer.Serialize(payload, JsonStore.Options));
            }

            var healthResults = new List<PrinterHealthStatus>();

            foreach (var printer in config.SharedPrinters.Where(x => x.Enabled))
            {
                try
                {
                    var health = await PrinterHealthProbe.ProbeAsync(
                        printer.QueueName,
                        printer.Id,
                        ct);

                    health.PrinterName = string.IsNullOrWhiteSpace(printer.DisplayName)
                        ? printer.QueueName
                        : printer.DisplayName;
                    healthResults.Add(health);
                }
                catch (Exception ex)
                {
                    healthResults.Add(new PrinterHealthStatus
                    {
                        PrinterId = printer.Id,
                        PrinterName = printer.DisplayName,
                        QueueName = printer.QueueName,
                        Level = "Red",
                        Summary = "Diagnose des eigenen Druckers fehlgeschlagen.",
                        Warnings = [ex.Message],
                        CheckedAt = DateTimeOffset.Now
                    });
                }
            }

            foreach (var mapping in config.NetworkPrinters.Where(x => x.Enabled))
            {
                try
                {
                    var local = await GetLocalReadinessAsync(mapping);
                    var peer = activePeers.FirstOrDefault(x =>
                        x.DeviceId == mapping.SourceDeviceId);

                    PrinterHealthStatus health;

                    if (peer is null)
                    {
                        health = new PrinterHealthStatus
                        {
                            PrinterId = mapping.PrinterId,
                            PrinterName = mapping.PrinterDisplayName,
                            QueueName = mapping.LocalPrinterName,
                            Level = local.Ready ? "Yellow" : "Red",
                            Summary = local.Ready
                                ? "Lokaler Druckpfad vorhanden, Quellgerät aktuell nicht erreichbar."
                                : "Lokaler Druckpfad ist nicht bereit.",
                            Warnings = ["Quellgerät aktuell nicht erreichbar."],
                            CheckedAt = DateTimeOffset.Now
                        };
                    }
                    else
                    {
                        health = await QueryPeerHealthAsync(mapping, peer, ct);
                        health.PrinterName = mapping.PrinterDisplayName;
                    }

                    health.ClientTransportStatus = local.Detail;
                    health.ClientTransportReady = local.Ready;
                    health.ServerTransportStatus = peer is null
                        ? $"Quellgerät '{mapping.SourceDeviceName}' nicht erreichbar."
                        : $"Quellgerät '{peer.DeviceName}' erreichbar · P{peer.ProtocolVersion}";
                    health.ServerTransportReady = peer is not null;

                    foreach (var warning in local.Warnings)
                    {
                        if (!health.Warnings.Contains(warning, StringComparer.OrdinalIgnoreCase))
                            health.Warnings.Insert(0, warning);
                    }

                    if (!local.Ready)
                    {
                        health.Level = "Red";
                        var cause = local.Problems.FirstOrDefault();
                        health.Summary = string.IsNullOrWhiteSpace(cause)
                            ? "Lokaler SimplePrint-Druckpfad ist nicht bereit."
                            : $"Nicht druckbereit: {cause}";
                    }

                    healthResults.Add(health);
                }
                catch (Exception ex)
                {
                    healthResults.Add(new PrinterHealthStatus
                    {
                        PrinterId = mapping.PrinterId,
                        PrinterName = mapping.PrinterDisplayName,
                        QueueName = mapping.LocalPrinterName,
                        Level = "Red",
                        Summary = "End-to-End-Diagnose des Netzwerkdruckers fehlgeschlagen.",
                        Warnings = [ex.Message],
                        CheckedAt = DateTimeOffset.Now
                    });
                }
            }

            var healthEntry = zip.CreateEntry(
                "printer-health.json",
                CompressionLevel.Optimal);

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
        NetworkPrinterMapping mapping)
    {
        var script = PrinterTransport.IsDirect(mapping.TransportMode)
            ? $@"
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$p = Get-Printer -Name $printer -ErrorAction SilentlyContinue
$problems = @()
if(-not $p) {{ $problems += 'Windows-Druckerqueue fehlt.' }}
[pscustomobject]@{{
  Ready = ($problems.Count -eq 0)
  Detail = 'Modus={mapping.TransportMode}; Queue=' + $(if($p){{'vorhanden'}}else{{'fehlt'}}) +
           '; Ziel={mapping.DirectAddress}; UUID={mapping.DeviceUuid}'
  Problems = @($problems)
  Warnings = @($problems)
}} | ConvertTo-Json -Compress
"
            : $@"
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$port={PowerShellRunner.Quote(mapping.PortName)}
$proxyPort={mapping.LocalProxyPort}
$svc = Get-Service -Name SimplePrint -ErrorAction SilentlyContinue
$p = Get-Printer -Name $printer -ErrorAction SilentlyContinue
$pp = Get-PrinterPort -Name $port -ErrorAction SilentlyContinue
$listen = Get-NetTCPConnection -State Listen -LocalPort $proxyPort -ErrorAction SilentlyContinue
$problems = @()
if(-not $svc -or [string]$svc.Status -ne 'Running') {{ $problems += 'SimplePrint-Dienst läuft nicht.' }}
if(-not $p) {{ $problems += 'Windows-Druckerqueue fehlt.' }}
elseif($p.PortName -ne $port) {{ $problems += 'Windows-Drucker verwendet nicht den erwarteten SimplePrint-Port.' }}
if(-not $pp) {{ $problems += 'SimplePrint-Druckerport fehlt.' }}
if(-not $listen) {{ $problems += 'Lokaler SimplePrint-Proxy lauscht nicht auf Port ' + $proxyPort + '.' }}
[pscustomobject]@{{
  Ready = ($problems.Count -eq 0)
  Detail = 'Modus=Tunnel; Dienst=' + $(if($svc){{[string]$svc.Status}}else{{'fehlt'}}) +
           '; Queue=' + $(if($p){{'vorhanden'}}else{{'fehlt'}}) +
           '; Port=' + $(if($pp){{'vorhanden'}}else{{'fehlt'}}) +
           '; Proxy=' + $(if($listen){{'lauscht'}}else{{'nicht aktiv'}})
  Problems = @($problems)
  Warnings = @($problems)
}} | ConvertTo-Json -Compress
";

        var result = await PowerShellRunner.RunAsync(script);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.StdErr)
                    ? "Lokale Druckbereitschaft konnte nicht geprüft werden."
                    : result.StdErr.Trim());
        }

        return JsonSerializer.Deserialize<LocalReadiness>(
                   result.StdOut,
                   JsonStore.Options)
               ?? new LocalReadiness
               {
                   Ready = false,
                   Detail = "Keine lokalen Statusdaten erhalten."
               };
    }

    private static async Task<PrinterHealthStatus> QueryPeerHealthAsync(
        NetworkPrinterMapping mapping,
        DevicePresence peer,
        CancellationToken ct)
    {
        if (peer.ProtocolVersion != Protocol.Version)
        {
            throw new InvalidOperationException(
                $"Quellgerät '{peer.DeviceName}' verwendet P{peer.ProtocolVersion}; benötigt wird P{Protocol.Version}.");
        }

        using var tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        await tcp.ConnectAsync(peer.Address, peer.GatewayPort, timeout.Token);
        using var stream = tcp.GetStream();

        await stream.WriteAsync(
            Protocol.CreateGatewayHeader(mapping.PrinterId, Guid.Empty),
            timeout.Token);

        return await Protocol.ReadPrinterHealthAsync(stream, timeout.Token)
               ?? throw new InvalidOperationException(
                   "Quellgerät hat keine gültige Druckerstatus-Antwort geliefert.");
    }

    private static async Task<string> GetLocalDiagnosticsAsync(SimplePrintConfig config)
    {
        var script = $@"
$ErrorActionPreference='Continue'
'=== SIMPLEPRINT DEVICE ==='
'DeviceId={config.DeviceId}'
'DeviceName={config.DeviceName}'
'Protocol={Protocol.Version}'
'DiscoveryPort={config.DiscoveryPort}'
'GatewayPort={config.GatewayPort}'
'DiagnosticsPort={config.DiagnosticsPort}'
'=== SYSTEM ==='
Get-ComputerInfo | Select-Object WindowsProductName,WindowsVersion,OsBuildNumber,CsName | Format-List | Out-String
'=== SIMPLEPRINT SERVICE ==='
Get-Service -Name SimplePrint -ErrorAction SilentlyContinue | Format-List * | Out-String
'=== SPOOLER ==='
Get-Service -Name Spooler -ErrorAction SilentlyContinue | Format-List * | Out-String
'=== NETWORK PROFILE ==='
Get-NetConnectionProfile | Format-Table Name,InterfaceAlias,NetworkCategory,IPv4Connectivity,IPv6Connectivity -AutoSize | Out-String
'=== SIMPLEPRINT FIREWALL ==='
Get-NetFirewallRule -Name 'SimplePrint*' -ErrorAction SilentlyContinue |
  Select-Object Name,DisplayName,Enabled,Profile,Direction,Action |
  Format-Table -AutoSize | Out-String
'=== SIMPLEPRINT TCP LISTENERS ==='
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
  Where-Object {{ $_.LocalPort -eq {config.GatewayPort} -or
                  $_.LocalPort -eq {config.DiagnosticsPort} -or
                  ($_.LocalPort -ge {config.LocalPortStart} -and $_.LocalPort -le {config.LocalPortEnd}) }} |
  Format-Table LocalAddress,LocalPort,OwningProcess -AutoSize | Out-String
'=== WSD DISCOVERY SERVICES ==='
Get-Service -Name fdPHost,FDResPub -ErrorAction SilentlyContinue |
  Format-Table Name,Status,StartType -AutoSize | Out-String
'=== PRINTERS ==='
Get-Printer -ErrorAction SilentlyContinue |
  Format-Table Name,DriverName,PortName,PrinterStatus,JobCount,Shared,ShareName,Comment -AutoSize |
  Out-String
'=== PRINTER PORTS ==='
Get-PrinterPort -ErrorAction SilentlyContinue |
  Select-Object Name,PrinterHostAddress,PortNumber,DeviceURL,DeviceUUID,SNMPEnabled,SNMPCommunity |
  Format-Table -AutoSize | Out-String
'=== WSD PORT REGISTRY ==='
$wsdRoot = 'HKLM:\SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports'
if(Test-Path -LiteralPath $wsdRoot) {{
  Get-ChildItem -LiteralPath $wsdRoot -ErrorAction SilentlyContinue | ForEach-Object {{
    '--- ' + $_.PSChildName + ' ---'
    Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue |
      Select-Object * -ExcludeProperty PSPath,PSParentPath,PSChildName,PSDrive,PSProvider |
      Format-List | Out-String
  }}
}}
'=== WSD DEVICE LOCATIONS ==='
$dafRoot = 'HKLM:\SYSTEM\CurrentControlSet\Enum\SWD\DAFWSDProvider'
if(Test-Path -LiteralPath $dafRoot) {{
  Get-ChildItem -LiteralPath $dafRoot -ErrorAction SilentlyContinue | ForEach-Object {{
    $item = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
    [pscustomobject]@{{
      Device = $_.PSChildName
      FriendlyName = [string]$item.FriendlyName
      LocationInformation = [string]$item.LocationInformation
      ContainerID = [string]$item.ContainerID
    }}
  }} | Format-Table -AutoSize | Out-String
}}
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
