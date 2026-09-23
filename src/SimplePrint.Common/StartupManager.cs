using Microsoft.Win32;

namespace SimplePrint.Common;

public static class StartupManager
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsSystemWideEnabled(string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static Task SetSystemWideAsync(string valueName, string executablePath, bool enabled)
    {
        var key = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        string script;
        if (enabled)
        {
            var command = $"\"{executablePath}\" --tray";
            script = $@"
$ErrorActionPreference='Stop'
New-Item -Path {PowerShellRunner.Quote(key)} -Force | Out-Null
New-ItemProperty -Path {PowerShellRunner.Quote(key)} -Name {PowerShellRunner.Quote(valueName)} -Value {PowerShellRunner.Quote(command)} -PropertyType String -Force | Out-Null
";
        }
        else
        {
            script = $@"
$ErrorActionPreference='Stop'
Remove-ItemProperty -Path {PowerShellRunner.Quote(key)} -Name {PowerShellRunner.Quote(valueName)} -ErrorAction SilentlyContinue
";
        }

        return PrivilegeHelper.RunPowerShellElevatedAsync(script);
    }
}
