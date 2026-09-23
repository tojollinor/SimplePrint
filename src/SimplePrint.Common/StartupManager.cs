using Microsoft.Win32;

namespace SimplePrint.Common;

public static class StartupManager
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsSystemWideEnabled(string valueName)
        => GetSystemWideCommand(valueName) is not null;

    public static string? GetSystemWideCommand(string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsTrayModeEnabled(string valueName, bool defaultValue = true)
    {
        var command = GetSystemWideCommand(valueName);
        if (string.IsNullOrWhiteSpace(command)) return defaultValue;
        return command.Contains("--tray", StringComparison.OrdinalIgnoreCase);
    }

    public static Task SetSystemWideAsync(string valueName, string executablePath, bool enabled, bool startInTray = true)
    {
        var key = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        string script;

        if (enabled)
        {
            var command = startInTray
                ? $"\"{executablePath}\" --tray"
                : $"\"{executablePath}\"";

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
