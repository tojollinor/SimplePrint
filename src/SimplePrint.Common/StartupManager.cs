using Microsoft.Win32;

namespace SimplePrint.Common;

public static class StartupManager
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string PreferenceKey = @"SOFTWARE\SimplePrint\Preferences";

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
        var runKey = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        var preferenceKey = @"HKLM:\SOFTWARE\SimplePrint\Preferences";
        var enabledPreference = valueName + "Enabled";
        var trayPreference = valueName + "Tray";

        var command = startInTray
            ? $"\"{executablePath}\" --tray"
            : $"\"{executablePath}\"";

        var script = $@"
$ErrorActionPreference='Stop'
New-Item -Path {PowerShellRunner.Quote(preferenceKey)} -Force | Out-Null
New-ItemProperty -Path {PowerShellRunner.Quote(preferenceKey)} -Name {PowerShellRunner.Quote(enabledPreference)} -Value {Convert.ToInt32(enabled)} -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path {PowerShellRunner.Quote(preferenceKey)} -Name {PowerShellRunner.Quote(trayPreference)} -Value {Convert.ToInt32(startInTray)} -PropertyType DWord -Force | Out-Null
";

        if (enabled)
        {
            script += $@"
New-Item -Path {PowerShellRunner.Quote(runKey)} -Force | Out-Null
New-ItemProperty -Path {PowerShellRunner.Quote(runKey)} -Name {PowerShellRunner.Quote(valueName)} -Value {PowerShellRunner.Quote(command)} -PropertyType String -Force | Out-Null
";
        }
        else
        {
            script += $@"
Remove-ItemProperty -Path {PowerShellRunner.Quote(runKey)} -Name {PowerShellRunner.Quote(valueName)} -ErrorAction SilentlyContinue
";
        }

        return PrivilegeHelper.RunPowerShellElevatedAsync(script);
    }
}
