using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace SimplePrint.Common;

public static class WsdAddressResolver
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static readonly FileLog Log = new(AppPaths.DeviceLog);

    /// <summary>
    /// Liefert eine geprüfte, vollständige IPP-URL (z. B. http://192.168.1.20:631/ipp/print)
    /// für einen WSD-Drucker. Kann keine funktionierende IPP-Adresse gefunden werden,
    /// wird "" zurückgegeben; der Drucker bleibt dann im WSD-Modus.
    /// Eine bloße IP-Adresse wird bewusst nie zurückgegeben: Add-Printer -IppURL
    /// braucht eine vollständige URL, und Aufrufer schalten bei jedem nicht leeren
    /// Ergebnis auf IPP um. Jede Entscheidung wird ins Gerätelog geschrieben.
    /// </summary>
    public static async Task<string> ResolveAsync(
        string? deviceUuid,
        CancellationToken cancellationToken = default)
    {
        var host = await ResolveHostAsync(deviceUuid);
        if (string.IsNullOrWhiteSpace(host))
        {
            Log.Info($"WSD-Auflösung {deviceUuid}: keine Geräteadresse gefunden (Registry und ARP-Cache ohne Treffer).");
            return "";
        }

        try
        {
            var url = await FindIppUrlAsync(host, cancellationToken);

            Log.Info(
                url.Length == 0
                    ? $"WSD-Auflösung {deviceUuid}: Adresse {host} gefunden, aber keine IPP-Schnittstelle erreichbar. Der Drucker bleibt im WSD-Modus."
                    : $"WSD-Auflösung {deviceUuid}: IPP-URL {url} bestätigt.");

            return url;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"WSD-Auflösung {deviceUuid}: IPP-Prüfung für {host} fehlgeschlagen", ex);
            return "";
        }
    }

    private static async Task<string> FindIppUrlAsync(string host, CancellationToken ct)
    {
        var uriHost = FormatUriHost(host);
        if (uriHost.Length == 0)
            return "";

        var candidates = new (string HttpUrl, string PrinterUri)[]
        {
            ($"http://{uriHost}:631/ipp/print", $"ipp://{uriHost}:631/ipp/print"),
            ($"http://{uriHost}/ipp/print", $"ipp://{uriHost}:80/ipp/print"),
            ($"http://{uriHost}:631/ipp", $"ipp://{uriHost}:631/ipp")
        };

        var probes = await Task.WhenAll(
            candidates.Select(c => ProbeIppAsync(c.HttpUrl, c.PrinterUri, ct)));

        for (var i = 0; i < candidates.Length; i++)
        {
            if (!probes[i].Ok)
                Log.Info($"IPP-Prüfung {candidates[i].HttpUrl}: {probes[i].Detail}");
        }

        for (var i = 0; i < candidates.Length; i++)
        {
            if (probes[i].Ok)
                return candidates[i].HttpUrl;
        }

        return "";
    }

    private static string FormatUriHost(string host)
    {
        var value = host.Trim();

        if (IPAddress.TryParse(value, out var ip))
        {
            return ip.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{ip}]"
                : ip.ToString();
        }

        return Uri.CheckHostName(value) == UriHostNameType.Dns
            ? value
            : "";
    }

    // Sendet eine IPP-Get-Printer-Attributes-Anfrage und prüft, ob der Drucker
    // unter dieser URL tatsächlich IPP spricht.
    private static async Task<(bool Ok, string Detail)> ProbeIppAsync(
        string url,
        string printerUri,
        CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

            using var content = new ByteArrayContent(BuildGetPrinterAttributesRequest(printerUri));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");

            using var response = await Http.PostAsync(url, content, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return (false, $"HTTP {(int)response.StatusCode}");

            var body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            if (body.Length < 4)
                return (false, "Antwort zu kurz (kein IPP)");

            // Bytes 2-3 enthalten den IPP-Statuscode; alles unter 0x0100 ist "successful".
            var status = (body[2] << 8) | body[3];
            return status < 0x0100
                ? (true, "OK")
                : (false, $"IPP-Status 0x{status:X4}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            return (false, $"{ex.GetType().Name}: {message}");
        }
    }

    private static byte[] BuildGetPrinterAttributesRequest(string printerUri)
    {
        using var ms = new MemoryStream();

        void Write16(int value)
        {
            ms.WriteByte((byte)(value >> 8));
            ms.WriteByte((byte)value);
        }

        void WriteText(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Write16(bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }

        void WriteAttribute(byte tag, string name, string value)
        {
            ms.WriteByte(tag);
            WriteText(name);
            WriteText(value);
        }

        ms.WriteByte(0x01);                 // IPP-Version 1.1
        ms.WriteByte(0x01);
        Write16(0x000B);                    // Operation: Get-Printer-Attributes
        Write16(0);                         // request-id (32 Bit) = 1
        Write16(1);
        ms.WriteByte(0x01);                 // operation-attributes-tag
        WriteAttribute(0x47, "attributes-charset", "utf-8");
        WriteAttribute(0x48, "attributes-natural-language", "en");
        WriteAttribute(0x45, "printer-uri", printerUri);
        WriteAttribute(0x44, "requested-attributes", "printer-name");
        ms.WriteByte(0x03);                 // end-of-attributes-tag

        return ms.ToArray();
    }

    private static async Task<string> ResolveHostAsync(string? deviceUuid)
    {
        if (string.IsNullOrWhiteSpace(deviceUuid))
            return "";

        // Hinweis: $host ist in PowerShell eine schreibgeschützte Systemvariable.
        // Deshalb heißt die Variable hier $resolvedHost.
        var script = $@"
$ErrorActionPreference='SilentlyContinue'
$raw={PowerShellRunner.Quote(deviceUuid)}

function Normalize-WsdUuid([string]$value) {{
  if([string]::IsNullOrWhiteSpace($value)) {{ return '' }}
  $v = $value.Trim()
  if($v.StartsWith('urn:uuid:', [System.StringComparison]::OrdinalIgnoreCase)) {{
    $v = $v.Substring(9)
  }}
  $g = [Guid]::Empty
  if([Guid]::TryParse($v, [ref]$g)) {{ return $g.ToString('D') }}
  return $v.ToLowerInvariant()
}}

function Get-HostFromLocation([string]$location) {{
  if([string]::IsNullOrWhiteSpace($location)) {{ return '' }}

  try {{
    $uri = [Uri]$location
    if(-not [string]::IsNullOrWhiteSpace($uri.Host)) {{
      return $uri.Host
    }}
  }} catch {{}}

  if($location -match '(?i)(?:https?|ipps?)://\[?([^\]/:]+(?::[^\]]+)?)\]?(?::\d+)?/') {{
    return $Matches[1]
  }}

  return ''
}}

$normalized = Normalize-WsdUuid $raw
if([string]::IsNullOrWhiteSpace($normalized)) {{ exit 0 }}

$root = 'HKLM:\SYSTEM\CurrentControlSet\Enum\SWD\DAFWSDProvider'
$candidates = @(
  $raw.Trim(),
  $normalized,
  ('urn:uuid:' + $normalized)
) | Where-Object {{ -not [string]::IsNullOrWhiteSpace($_) }} | Select-Object -Unique

foreach($candidate in $candidates) {{
  $key = Join-Path $root $candidate
  if(Test-Path -LiteralPath $key) {{
    $item = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
    $resolvedHost = Get-HostFromLocation ([string]$item.LocationInformation)
    if(-not [string]::IsNullOrWhiteSpace($resolvedHost)) {{
      $resolvedHost
      exit 0
    }}
  }}
}}

if(Test-Path -LiteralPath $root) {{
  foreach($key in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {{
    $name = [string]$key.PSChildName
    if((Normalize-WsdUuid $name) -ne $normalized) {{ continue }}

    $item = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
    $resolvedHost = Get-HostFromLocation ([string]$item.LocationInformation)
    if(-not [string]::IsNullOrWhiteSpace($resolvedHost)) {{
      $resolvedHost
      exit 0
    }}
  }}
}}

$hex = ($normalized -replace '[^0-9a-fA-F]','').ToUpperInvariant()
if($hex.Length -ge 12) {{
  $macHex = $hex.Substring($hex.Length - 12)
  $mac = ((0..5 | ForEach-Object {{ $macHex.Substring($_ * 2, 2) }}) -join '-').ToUpperInvariant()

  $neighbors = @(
    Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue |
      Where-Object {{
        -not [string]::IsNullOrWhiteSpace([string]$_.LinkLayerAddress) -and
        (([string]$_.LinkLayerAddress).Replace(':','-').ToUpperInvariant() -eq $mac) -and
        ([string]$_.State -notin @('Unreachable','Incomplete'))
      }} |
      Sort-Object @{{ Expression = {{
        switch([string]$_.State) {{
          'Reachable' {{ 0 }}
          'Permanent' {{ 1 }}
          'Stale' {{ 2 }}
          default {{ 3 }}
        }}
      }} }}
  )

  foreach($neighbor in $neighbors) {{
    $ip = [string]$neighbor.IPAddress
    if(-not [string]::IsNullOrWhiteSpace($ip)) {{
      $ip
      exit 0
    }}
  }}
}}
";

        var result = await PowerShellRunner.RunAsync(
            script,
            timeout: TimeSpan.FromSeconds(30));

        if (result.ExitCode != 0)
            return "";

        var value = result.StdOut
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(x => IPAddress.TryParse(x, out _) || Uri.CheckHostName(x) != UriHostNameType.Unknown);

        return value ?? "";
    }
}
