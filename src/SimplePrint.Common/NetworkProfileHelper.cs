namespace SimplePrint.Common;

public sealed record NetworkProfileState(bool HasPublicProfile, string Description);

public static class NetworkProfileHelper
{
    public static async Task<NetworkProfileState> GetStateAsync()
    {
        const string script = @"
$profiles = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object {
  $_.IPv4Connectivity -ne 'Disconnected' -or $_.IPv6Connectivity -ne 'Disconnected'
})
if(-not $profiles -or $profiles.Count -eq 0) {
  'NONE'
  exit 0
}
foreach($p in $profiles) {
  $p.InterfaceAlias + '|' + $p.Name + '|' + $p.NetworkCategory
}
";
        var r = await PowerShellRunner.RunAsync(script);
        var lines = r.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0 || lines.Any(x => x.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase)))
            return new NetworkProfileState(false, "Kein aktives Windows-Netzwerkprofil gefunden.");

        var isPublic = lines.Any(x => x.EndsWith("|Public", StringComparison.OrdinalIgnoreCase));
        return new NetworkProfileState(isPublic, string.Join(Environment.NewLine, lines));
    }
}
