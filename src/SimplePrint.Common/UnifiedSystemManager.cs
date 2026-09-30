using System.Text.Json;

namespace SimplePrint.Common;

public sealed record UnifiedFirewallState(
    FirewallRuleStatus Discovery,
    FirewallRuleStatus Gateway,
    FirewallRuleStatus Diagnostics)
{
    public bool Correct =>
        Discovery.Correct &&
        Gateway.Correct &&
        Diagnostics.Correct;
}

public static class UnifiedSystemManager
{
    public static async Task<UnifiedFirewallState> GetFirewallStateAsync(
        SimplePrintConfig config)
    {
        var script = $$"""
function Get-SimplePrintRuleState($name,$display,$protocol,$port) {
  $r = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue |
    Select-Object -First 1

  if(-not $r) {
    return [pscustomobject]@{
      Name=$display
      Exists=$false
      Enabled=$false
      Correct=$false
      Detail='Regel fehlt'
    }
  }

  $pf = $r | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue
  $af = $r | Get-NetFirewallAddressFilter -ErrorAction SilentlyContinue
  $profile = [string]$r.Profile
  $remote = @($af.RemoteAddress) -join ','

  $enabled = ([string]$r.Enabled -eq 'True')
  $profileOk =
    ($profile -match 'Private') -and
    ($profile -match 'Domain') -and
    ($profile -notmatch 'Public')

  $protocolText = [string]$pf.Protocol
  $protocolOk =
    ($protocolText -eq $protocol) -or
    ($protocol -eq 'UDP' -and $protocolText -eq '17') -or
    ($protocol -eq 'TCP' -and $protocolText -eq '6')

  $portOk =
    $protocolOk -and
    ([string]$pf.LocalPort -eq [string]$port)

  $addressOk = $remote -match 'LocalSubnet'

  $correct =
    $enabled -and
    ([string]$r.Direction -eq 'Inbound') -and
    ([string]$r.Action -eq 'Allow') -and
    $profileOk -and
    $portOk -and
    $addressOk

  [pscustomobject]@{
    Name=$display
    Exists=$true
    Enabled=$enabled
    Correct=$correct
    Detail=('Enabled=' + $enabled +
            '; Profile=' + $profile +
            '; Protocol=' + $protocolText +
            '; Port=' + [string]$pf.LocalPort +
            '; Remote=' + $remote)
  }
}

@(
  Get-SimplePrintRuleState 'SimplePrint-Discovery' 'SimplePrint Discovery' 'UDP' {{config.DiscoveryPort}}
  Get-SimplePrintRuleState 'SimplePrint-Gateway' 'SimplePrint Print Gateway' 'TCP' {{config.GatewayPort}}
  Get-SimplePrintRuleState 'SimplePrint-Diagnostics' 'SimplePrint Diagnostics' 'TCP' {{config.DiagnosticsPort}}
) | ConvertTo-Json -Compress
""";

        var result = await PowerShellRunner.RunAsync(script);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.StdErr)
                    ? "Firewallstatus konnte nicht gelesen werden."
                    : result.StdErr.Trim());
        }

        var states = JsonSerializer.Deserialize<List<FirewallRuleStatus>>(
                         result.StdOut,
                         JsonStore.Options)
                     ?? [];

        FirewallRuleStatus Find(string name) =>
            states.FirstOrDefault(x =>
                x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? new FirewallRuleStatus
            {
                Name = name,
                Detail = "Keine Statusdaten"
            };

        return new UnifiedFirewallState(
            Find("SimplePrint Discovery"),
            Find("SimplePrint Print Gateway"),
            Find("SimplePrint Diagnostics"));
    }

    public static Task ApplyFirewallAsync(SimplePrintConfig config)
    {
        var script = $$"""
$ErrorActionPreference='Stop'

$names = @(
  'SimplePrint-Discovery',
  'SimplePrint-Gateway',
  'SimplePrint-Diagnostics'
)

foreach($name in $names) {
  Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule
}

New-NetFirewallRule -Name 'SimplePrint-Discovery' -DisplayName 'SimplePrint Discovery' -Description 'SimplePrint Geräteerkennung im lokalen Netzwerk' -Direction Inbound -Action Allow -Enabled True -Protocol UDP -LocalPort {{config.DiscoveryPort}} -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null
New-NetFirewallRule -Name 'SimplePrint-Gateway' -DisplayName 'SimplePrint Print Gateway' -Description 'SimplePrint Druckdaten und Status im lokalen Netzwerk' -Direction Inbound -Action Allow -Enabled True -Protocol TCP -LocalPort {{config.GatewayPort}} -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null
New-NetFirewallRule -Name 'SimplePrint-Diagnostics' -DisplayName 'SimplePrint Diagnostics' -Description 'SimplePrint Diagnoseabruf im lokalen Netzwerk' -Direction Inbound -Action Allow -Enabled True -Protocol TCP -LocalPort {{config.DiagnosticsPort}} -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null

function Assert-SimplePrintRule($name,$port) {
  $r = Get-NetFirewallRule -Name $name -ErrorAction Stop
  $pf = $r | Get-NetFirewallPortFilter
  $af = $r | Get-NetFirewallAddressFilter

  if([string]$r.Enabled -ne 'True') {
    throw "$name ist nicht aktiviert."
  }

  if([string]$r.Direction -ne 'Inbound' -or
     [string]$r.Action -ne 'Allow') {
    throw "$name hat eine ungültige Richtung oder Aktion."
  }

  $profile = [string]$r.Profile
  if($profile -notmatch 'Private' -or
     $profile -notmatch 'Domain' -or
     $profile -match 'Public') {
    throw "$name hat ein falsches Firewallprofil: $profile"
  }

  if([string]$pf.LocalPort -ne [string]$port) {
    throw "$name hat einen falschen Port: $($pf.LocalPort)"
  }

  $remote = @($af.RemoteAddress) -join ','
  if($remote -notmatch 'LocalSubnet') {
    throw "$name ist nicht auf LocalSubnet beschränkt."
  }
}

Assert-SimplePrintRule 'SimplePrint-Discovery' {{config.DiscoveryPort}}
Assert-SimplePrintRule 'SimplePrint-Gateway' {{config.GatewayPort}}
Assert-SimplePrintRule 'SimplePrint-Diagnostics' {{config.DiagnosticsPort}}
""";

        return PrivilegeHelper.RunPowerShellElevatedAsync(script);
    }

    public static Task RemoveFirewallAsync()
    {
        const string script = """
$ErrorActionPreference='Stop'
@(
  'SimplePrint-Discovery',
  'SimplePrint-Gateway',
  'SimplePrint-Diagnostics'
) | ForEach-Object {
  Get-NetFirewallRule -Name $_ -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule
}
""";

        return PrivilegeHelper.RunPowerShellElevatedAsync(script);
    }

    public static async Task RestartServiceAsync()
    {
        const string script = """
$ErrorActionPreference='Stop'
$svc = Get-Service -Name SimplePrint -ErrorAction Stop

if($svc.Status -eq 'Stopped') {
  Start-Service -Name SimplePrint -ErrorAction Stop
} else {
  Restart-Service -Name SimplePrint -Force -ErrorAction Stop
}

(Get-Service -Name SimplePrint -ErrorAction Stop).WaitForStatus(
  'Running',
  [TimeSpan]::FromSeconds(15))

if((Get-Service -Name SimplePrint -ErrorAction Stop).Status -ne 'Running') {
  throw 'Der SimplePrint-Dienst konnte nicht gestartet werden.'
}
""";

        await PrivilegeHelper.RunPowerShellElevatedAsync(script);
    }
}
