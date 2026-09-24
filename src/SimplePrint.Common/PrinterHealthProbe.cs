using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace SimplePrint.Common;

public static class PrinterHealthProbe
{
    private sealed class QueueProbe
    {
        public bool Exists { get; set; }
        public string DriverName { get; set; } = "";
        public string PrinterStatus { get; set; } = "";
        public string PortName { get; set; } = "";
        public bool WorkOffline { get; set; }
        public bool Paused { get; set; }
        public string DeviceAddress { get; set; } = "";
        public int? DevicePort { get; set; }
        public bool SnmpEnabled { get; set; }
        public string SnmpCommunity { get; set; } = "";
    }

    private const string HrPrinterStatus = ".1.3.6.1.2.1.25.3.5.1.1";
    private const string HrPrinterErrorState = ".1.3.6.1.2.1.25.3.5.1.2";
    private const string SuppliesDescription = ".1.3.6.1.2.1.43.11.1.1.6";
    private const string SuppliesMaxCapacity = ".1.3.6.1.2.1.43.11.1.1.8";
    private const string SuppliesLevel = ".1.3.6.1.2.1.43.11.1.1.9";

    public static async Task<PrinterHealthStatus> ProbeAsync(
        string queueName,
        Guid printerId = default,
        CancellationToken ct = default)
    {
        var result = new PrinterHealthStatus
        {
            PrinterId = printerId,
            PrinterName = queueName,
            QueueName = queueName,
            CheckedAt = DateTimeOffset.Now
        };

        var queue = await ReadQueueAsync(queueName);
        result.QueueExists = queue.Exists;
        result.DriverName = queue.DriverName;
        result.PortName = queue.PortName;
        result.QueueStatus = queue.PrinterStatus;
        result.QueueOffline = queue.WorkOffline ||
            ContainsAny(queue.PrinterStatus, "offline", "error", "fehler", "not available", "nicht verfügbar");
        result.QueuePaused = queue.Paused ||
            ContainsAny(queue.PrinterStatus, "paused", "angehalten", "pause");
        result.DeviceAddress = queue.DeviceAddress;
        result.DevicePort = queue.DevicePort;

        if (!queue.Exists)
        {
            result.Level = "Red";
            result.Summary = "Windows-Druckerwarteschlange nicht gefunden.";
            result.Warnings.Add("Die konfigurierte Server-Warteschlange existiert nicht.");
            return result;
        }

        if (PrinterTransport.IsSimplePrintPort(queue.PortName) ||
            PrinterTransport.IsLoopbackProxy(queue.DeviceAddress, queue.DevicePort))
        {
            result.Level = "Red";
            result.Summary = "Nicht druckbereit: Druckschleife erkannt.";
            result.Warnings.Add(
                "Die Server-Warteschlange zeigt auf einen lokalen SimplePrint-Proxy. Diese Konfiguration wird blockiert.");
            return result;
        }

        var validMicrosoftDirect =
            PrinterTransport.IsMicrosoftIppClassDriver(queue.DriverName) &&
            !PrinterTransport.IsSimplePrintPort(queue.PortName);

        if (IsGenericClassDriver(queue.DriverName) && !validMicrosoftDirect)
        {
            result.Warnings.Add(
                $"Class-Treiber erkannt: {queue.DriverName}. Im SimplePrint-Tunnel muss auf dem Client exakt derselbe Treiber verwendet werden.");
        }

        if (!string.IsNullOrWhiteSpace(queue.DeviceAddress))
        {
            result.PingReachable = await TryPingAsync(queue.DeviceAddress, ct);

            if (queue.DevicePort is > 0)
                result.TcpReachable = await TryTcpAsync(queue.DeviceAddress, queue.DevicePort.Value, ct);

            var community = string.IsNullOrWhiteSpace(queue.SnmpCommunity)
                ? "public"
                : queue.SnmpCommunity;

            await TrySnmpAsync(result, queue.DeviceAddress, community, ct);
        }
        else
        {
            result.DeviceStatus = "Lokaler/USB/WSD-Drucker: Gerätezustand nur über Windows verfügbar.";
        }

        Evaluate(result);
        return result;
    }

    private static async Task<QueueProbe> ReadQueueAsync(string queueName)
    {
        var script = $@"
$ErrorActionPreference='Stop'
$q={PowerShellRunner.Quote(queueName)}
$p = Get-Printer -Name $q -ErrorAction SilentlyContinue
if(-not $p) {{
  [pscustomobject]@{{ Exists=$false }} | ConvertTo-Json -Compress
  exit 0
}}

$port = Get-PrinterPort -Name $p.PortName -ErrorAction SilentlyContinue
$cim = Get-CimInstance Win32_Printer -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -eq $q }} | Select-Object -First 1

$deviceAddress = ''
$devicePort = $null
$snmpEnabled = $false
$snmpCommunity = ''

if($port) {{
  if($port.PSObject.Properties['PrinterHostAddress']) {{ $deviceAddress = [string]$port.PrinterHostAddress }}
  if($port.PSObject.Properties['PortNumber'] -and $port.PortNumber) {{ $devicePort = [int]$port.PortNumber }}
  if($port.PSObject.Properties['SNMPEnabled']) {{ $snmpEnabled = [bool]$port.SNMPEnabled }}
  if($port.PSObject.Properties['SNMPCommunity']) {{ $snmpCommunity = [string]$port.SNMPCommunity }}
}}

[pscustomobject]@{{
  Exists = $true
  DriverName = [string]$p.DriverName
  PrinterStatus = [string]$p.PrinterStatus
  PortName = [string]$p.PortName
  WorkOffline = if($cim) {{ [bool]$cim.WorkOffline }} else {{ $false }}
  Paused = if($cim) {{ [bool]$cim.Paused }} else {{ $false }}
  DeviceAddress = $deviceAddress
  DevicePort = $devicePort
  SnmpEnabled = $snmpEnabled
  SnmpCommunity = $snmpCommunity
}} | ConvertTo-Json -Compress
";

        var response = await PowerShellRunner.RunAsync(script);
        if (response.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(response.StdErr)
                    ? "Windows-Druckerstatus konnte nicht gelesen werden."
                    : response.StdErr.Trim());

        return JsonSerializer.Deserialize<QueueProbe>(response.StdOut, JsonStore.Options)
            ?? new QueueProbe();
    }

    private static async Task<bool> TryPingAsync(string host, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(900), Array.Empty<byte>(), null, ct);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryTcpAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1200));
            await tcp.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task TrySnmpAsync(
        PrinterHealthStatus result,
        string host,
        string community,
        CancellationToken ct)
    {
        try
        {
            var statusRows = await SimpleSnmpClient.WalkAsync(
                host,
                community,
                HrPrinterStatus,
                maxItems: 8,
                timeoutMs: 700,
                ct);

            if (statusRows.Count == 0)
            {
                var sysName = await SimpleSnmpClient.GetAsync(
                    host,
                    community,
                    ".1.3.6.1.2.1.1.5.0",
                    700,
                    ct);

                if (sysName is null)
                    return;
            }

            result.SnmpAvailable = true;

            var printerStatus = statusRows
                .Select(x => x.AsInteger())
                .FirstOrDefault(x => x.HasValue);

            if (printerStatus.HasValue)
                result.DeviceStatus = DescribePrinterStatus(printerStatus.Value);

            var errorRows = await SimpleSnmpClient.WalkAsync(
                host,
                community,
                HrPrinterErrorState,
                maxItems: 8,
                timeoutMs: 700,
                ct);

            var errorBits = errorRows.FirstOrDefault(x => x.Raw.Length > 0)?.Raw;
            if (errorBits is { Length: > 0 })
                ApplyPrinterErrorBits(result, errorBits);

            var descriptions = await SimpleSnmpClient.WalkAsync(
                host,
                community,
                SuppliesDescription,
                maxItems: 48,
                timeoutMs: 700,
                ct);

            if (descriptions.Count == 0)
                return;

            var maximums = await SimpleSnmpClient.WalkAsync(
                host,
                community,
                SuppliesMaxCapacity,
                maxItems: 48,
                timeoutMs: 700,
                ct);

            var levels = await SimpleSnmpClient.WalkAsync(
                host,
                community,
                SuppliesLevel,
                maxItems: 48,
                timeoutMs: 700,
                ct);

            var maxBySuffix = maximums.ToDictionary(
                x => Suffix(x.Oid, SuppliesMaxCapacity),
                x => x.AsInteger());

            var levelBySuffix = levels.ToDictionary(
                x => Suffix(x.Oid, SuppliesLevel),
                x => x.AsInteger());

            foreach (var item in descriptions)
            {
                var suffix = Suffix(item.Oid, SuppliesDescription);
                var name = item.AsText();

                if (string.IsNullOrWhiteSpace(name))
                    name = "Verbrauchsmaterial " + suffix;

                maxBySuffix.TryGetValue(suffix, out var max);
                levelBySuffix.TryGetValue(suffix, out var level);

                int? percent = null;
                var state = "Unbekannt";

                if (level.HasValue && level.Value >= 0 &&
                    max.HasValue && max.Value > 0)
                {
                    percent = (int)Math.Clamp(
                        Math.Round(level.Value * 100d / max.Value),
                        0,
                        100);

                    state = percent == 0
                        ? "Leer"
                        : percent <= 10
                            ? "Niedrig"
                            : "OK";
                }
                else if (level == -3)
                {
                    state = "Vorhanden";
                }

                result.Supplies.Add(new PrinterSupplyStatus
                {
                    Name = name,
                    Percent = percent,
                    State = state
                });
            }
        }
        catch
        {
            result.SnmpAvailable = false;
        }
    }

    private static string Suffix(string oid, string subtree)
    {
        var prefix = subtree.TrimEnd('.') + ".";
        return oid.StartsWith(prefix, StringComparison.Ordinal)
            ? oid[prefix.Length..]
            : oid;
    }

    private static string DescribePrinterStatus(long value) =>
        value switch
        {
            3 => "Leerlauf",
            4 => "Druckt",
            5 => "Aufwärmphase",
            2 => "Unbekannt",
            _ => "Sonstiger Gerätezustand"
        };

    private static void ApplyPrinterErrorBits(PrinterHealthStatus result, byte[] bytes)
    {
        var states = new (int Bit, string Text, bool Critical, bool Paper)[]
        {
            (0, "Papier niedrig", false, true),
            (1, "Kein Papier", true, true),
            (2, "Toner niedrig", false, false),
            (3, "Toner leer", true, false),
            (4, "Klappe offen", true, false),
            (5, "Papierstau", true, true),
            (6, "Drucker offline", true, false),
            (7, "Service erforderlich", true, false),
            (8, "Papierfach fehlt", true, true),
            (9, "Ausgabefach fehlt", true, true),
            (10, "Verbrauchsmaterial fehlt", true, false),
            (11, "Ausgabefach fast voll", false, false),
            (12, "Ausgabefach voll", true, false),
            (13, "Papierfach leer", true, true),
            (14, "Wartung überfällig", false, false)
        };

        var paper = new List<string>();

        foreach (var item in states)
        {
            var byteIndex = item.Bit / 8;
            var bitIndex = 7 - (item.Bit % 8);

            if (byteIndex >= bytes.Length ||
                (bytes[byteIndex] & (1 << bitIndex)) == 0)
                continue;

            result.Warnings.Add(item.Text);
            if (item.Paper) paper.Add(item.Text);
        }

        if (paper.Count > 0)
            result.PaperStatus = string.Join(", ", paper);
        else
            result.PaperStatus = "Keine Papierwarnung gemeldet";
    }

    private static void Evaluate(PrinterHealthStatus result)
    {
        if (!result.QueueExists)
        {
            result.Level = "Red";
            result.Summary = "Nicht druckbereit: Windows-Warteschlange fehlt.";
            return;
        }

        if (result.QueuePaused)
        {
            result.Level = "Red";
            result.Summary = "Nicht druckbereit: Windows-Warteschlange ist pausiert.";
            return;
        }

        if (result.QueueOffline)
        {
            result.Level = "Red";
            result.Summary = "Nicht druckbereit: Drucker bzw. Windows-Warteschlange ist offline.";
            return;
        }

        var emptySupply = result.Supplies.FirstOrDefault(x =>
            x.State.Equals("Leer", StringComparison.OrdinalIgnoreCase));

        var tonerEmpty =
            result.Warnings.Any(x => ContainsAny(x, "toner leer")) ||
            (emptySupply is not null && ContainsAny(emptySupply.Name, "toner", "cartridge", "kartusche"));

        if (tonerEmpty)
        {
            result.Level = "Red";
            result.Summary = "Verbindung zum Drucker vorhanden, aber Toner leer.";
            return;
        }

        if (emptySupply is not null)
        {
            result.Level = "Red";
            result.Summary =
                $"Verbindung zum Drucker vorhanden, aber Verbrauchsmaterial leer: {emptySupply.Name}.";
            return;
        }

        var criticalReason = GetCriticalReason(result.Warnings);
        if (!string.IsNullOrWhiteSpace(criticalReason))
        {
            result.Level = "Red";
            result.Summary = $"Verbindung zum Drucker vorhanden, aber {criticalReason}.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.DeviceAddress) &&
            result.TcpReachable == false &&
            result.PingReachable == false &&
            !result.SnmpAvailable)
        {
            result.Level = "Red";
            result.Summary = "Nicht druckbereit: Netzwerkverbindung zum Drucker nicht möglich.";
            return;
        }

        var lowSupply = result.Supplies.FirstOrDefault(x =>
            x.State.Equals("Niedrig", StringComparison.OrdinalIgnoreCase));

        if (lowSupply is not null)
        {
            result.Level = "Yellow";
            var level = lowSupply.Percent is null ? "" : $" ({lowSupply.Percent} %)";
            result.Summary =
                $"Verbindung zum Drucker vorhanden, Verbrauchsmaterial niedrig: {lowSupply.Name}{level}.";
            return;
        }

        var firstWarning = result.Warnings.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstWarning))
        {
            result.Level = "Yellow";
            result.Summary = $"Druckverbindung vorhanden, Hinweis vom Drucker: {firstWarning}.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.DeviceAddress) &&
            !result.SnmpAvailable &&
            result.TcpReachable != true &&
            result.PingReachable != true)
        {
            result.Level = "Yellow";
            result.Summary = "Windows-Warteschlange bereit, Gerätezustand konnte nicht vollständig geprüft werden.";
            return;
        }

        result.Level = "Green";
        result.Summary = "Bereit: Keine bekannten Hindernisse gefunden.";
    }

    private static string? GetCriticalReason(IEnumerable<string> warnings)
    {
        var ordered = new (string Needle, string Text)[]
        {
            ("kein papier", "kein Papier vorhanden"),
            ("papierfach leer", "Papierfach leer"),
            ("papierstau", "Papierstau gemeldet"),
            ("klappe offen", "Klappe offen"),
            ("drucker offline", "Drucker offline"),
            ("service erforderlich", "Service erforderlich"),
            ("papierfach fehlt", "Papierfach fehlt"),
            ("ausgabefach fehlt", "Ausgabefach fehlt"),
            ("verbrauchsmaterial fehlt", "Verbrauchsmaterial fehlt"),
            ("ausgabefach voll", "Ausgabefach voll")
        };

        foreach (var candidate in ordered)
        {
            if (warnings.Any(x => ContainsAny(x, candidate.Needle)))
                return candidate.Text;
        }

        return null;
    }

    private static bool IsGenericClassDriver(string driver) =>
        ContainsAny(
            driver,
            "class driver",
            "type1 class",
            "type 1 class",
            "microsoft ipp",
            "microsoft enhanced point");

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        return needles.Any(x =>
            value.Contains(x, StringComparison.OrdinalIgnoreCase));
    }
}