using System.Net;

namespace SimplePrint.Common;

public static class WsdAddressResolver
{
    public static async Task<string> ResolveAsync(
        string? deviceUuid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceUuid))
            return "";

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
    $host = Get-HostFromLocation ([string]$item.LocationInformation)
    if(-not [string]::IsNullOrWhiteSpace($host)) {{
      $host
      exit 0
    }}
  }}
}}

if(Test-Path -LiteralPath $root) {{
  foreach($key in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {{
    $name = [string]$key.PSChildName
    if((Normalize-WsdUuid $name) -ne $normalized) {{ continue }}

    $item = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
    $host = Get-HostFromLocation ([string]$item.LocationInformation)
    if(-not [string]::IsNullOrWhiteSpace($host)) {{
      $host
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

        var result = await PowerShellRunner.RunAsync(script);

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
