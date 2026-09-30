using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using SimplePrint.Common;

namespace SimplePrint.Gui;

internal sealed class DriverSignatureResult
{
    public bool Valid { get; set; }
    public string Signer { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>
/// Client-Seite der einmaligen Treiberübertragung: fehlenden Treiber beim SimplePrint-Server
/// anfragen, Paket prüfen (Prüfsumme, Architektur, digitale Signatur), Benutzer bestätigen
/// lassen und mit Administratorrechten installieren.
/// </summary>
internal static class DriverPackageInstaller
{
    private static readonly TimeSpan PeerMaxAge = TimeSpan.FromSeconds(40);

    /// <summary>
    /// Liefert true, wenn der Treiber danach installiert ist. Liefert false, wenn kein Server
    /// ihn anbietet, der Benutzer abbricht oder der Server ablehnt (Meldung wurde dann angezeigt).
    /// Wird die Administratorabfrage abgebrochen, wird OperationCanceledException ausgelöst.
    /// </summary>
    public static async Task<bool> TryInstallFromServerAsync(string driverName)
    {
        var server = FindServerForDriver(driverName);
        if (server is null)
            return false;

        var ask = MessageBox.Show(
            $"Der Druckertreiber '{driverName}' ist auf diesem PC nicht vorhanden.\r\n\r\n" +
            $"Der SimplePrint-Server '{server.DeviceName}' ({server.Address}) verwendet diesen Treiber " +
            "und kann ihn einmalig bereitstellen. Dazu muss der Server die Anfrage bestätigen.\r\n\r\n" +
            "Treiber jetzt beim Server anfragen?",
            "SimplePrint – Treiber fehlt",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (ask != DialogResult.Yes)
            return false;

        var config = UnifiedConfigStore.LoadOrMigrate();

        var request = new DriverRequestMessage
        {
            DriverName = driverName,
            ClientName = config.DeviceName,
            ClientArchitecture = DriverArchitecture.Local
        };

        DriverDownload download;

        using (var progressForm = new DriverTransferProgressForm(server.DeviceName))
        {
            progressForm.Show();
            var progress = new Progress<string>(progressForm.SetText);

            try
            {
                download = await DriverTransferClient.DownloadAsync(
                    server.Address,
                    config.DeviceId,
                    request,
                    progress,
                    progressForm.CancellationToken);
            }
            catch (OperationCanceledException) when (progressForm.CancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                progressForm.Close();
                MessageBox.Show(
                    "Die Treiberübertragung hat zu lange gedauert und wurde abgebrochen.",
                    "SimplePrint – Treiberübertragung",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }
            catch (DriverTransferException ex)
            {
                progressForm.Close();
                MessageBox.Show(
                    ex.Message,
                    TitleFor(ex.State),
                    MessageBoxButtons.OK,
                    ex.State is DriverStates.Error
                        ? MessageBoxIcon.Error
                        : MessageBoxIcon.Warning);
                return false;
            }
            catch (Exception ex)
            {
                progressForm.Close();
                MessageBox.Show(
                    ex.Message,
                    "SimplePrint – Treiberübertragung",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }
        }

        return await InstallDownloadedAsync(download, server, driverName);
    }

    private static string TitleFor(string state) =>
        state switch
        {
            DriverStates.ArchitectureMismatch => "SimplePrint – Architektur passt nicht",
            DriverStates.Denied => "SimplePrint – Anfrage abgelehnt",
            DriverStates.Timeout => "SimplePrint – Keine Bestätigung am Server",
            DriverStates.Unavailable => "SimplePrint – Treiber nicht verfügbar",
            DriverStates.NotAuthorized => "SimplePrint – Server kennt dieses Gerät nicht",
            DriverStates.Busy => "SimplePrint – Server ausgelastet",
            _ => "SimplePrint – Treiberübertragung"
        };

    private static DevicePresence? FindServerForDriver(string driverName)
    {
        try
        {
            var config = UnifiedConfigStore.LoadOrMigrate();
            var peers = JsonStore.LoadOrCreate(
                AppPaths.DevicePeers,
                () => new List<DevicePresence>());

            return peers
                .Where(p =>
                    p.DeviceId != config.DeviceId &&
                    !string.IsNullOrWhiteSpace(p.Address) &&
                    DateTimeOffset.Now - p.LastSeen <= PeerMaxAge &&
                    p.Printers.Any(x =>
                        string.Equals(
                            x.DriverName,
                            driverName,
                            StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p.DeviceName, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> InstallDownloadedAsync(
        DriverDownload download,
        DevicePresence server,
        string driverName)
    {
        var info = download.Info;

        // Entpackt wird unter ProgramData: Dort kann auch das mit Administratorrechten
        // gestartete PowerShell (evtl. anderes Konto) lesen, im Tempordner des Benutzers nicht.
        var importDir = Path.Combine(
            AppPaths.DeviceRoot,
            "DriverImport",
            Guid.NewGuid().ToString("N"));

        try
        {
            if (!string.Equals(
                    info.Architecture,
                    DriverArchitecture.Local,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Der Treiber ist für {DriverArchitecture.Describe(info.Architecture)}, " +
                    $"dieser PC ist {DriverArchitecture.Describe(DriverArchitecture.Local)}.");
            }

            var infName = Path.GetFileName(info.InfFileName);
            if (string.IsNullOrWhiteSpace(infName) ||
                !string.Equals(infName, info.InfFileName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Der Server hat einen ungültigen INF-Dateinamen gemeldet.");
            }

            Directory.CreateDirectory(importDir);
            ZipFile.ExtractToDirectory(download.ZipPath, importDir, overwriteFiles: false);

            var infPath = Path.Combine(importDir, infName);
            if (!File.Exists(infPath))
            {
                throw new InvalidOperationException(
                    "Die INF-Datei des Treibers fehlt im übertragenen Paket.");
            }

            var signature = await CheckSignatureAsync(importDir);
            if (!signature.Valid)
            {
                throw new InvalidOperationException(
                    "Das Treiberpaket ist nicht gültig digital signiert und wird deshalb nicht installiert." +
                    (string.IsNullOrWhiteSpace(signature.Detail)
                        ? ""
                        : "\r\n\r\n" + signature.Detail));
            }

            var confirm = MessageBox.Show(
                $"Der Server '{server.DeviceName}' hat den Treiber bereitgestellt:\r\n\r\n" +
                $"Treiber: {info.DriverName}\r\n" +
                $"Hersteller: {(string.IsNullOrWhiteSpace(info.Manufacturer) ? "unbekannt" : info.Manufacturer)}\r\n" +
                $"Version: {(string.IsNullOrWhiteSpace(info.DriverVersion) ? "unbekannt" : info.DriverVersion)}\r\n" +
                $"Architektur: {DriverArchitecture.Describe(info.Architecture)}\r\n" +
                $"Digitale Signatur: gültig, signiert von {ShortSigner(signature.Signer)}\r\n\r\n" +
                "Treiber jetzt installieren? Danach folgt eine Administratorabfrage.",
                "SimplePrint – Treiber installieren",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes)
                return false;

            var script = $@"
$inf={PowerShellRunner.Quote(infPath)}
$driver={PowerShellRunner.Quote(driverName)}
$pnputil = Join-Path $env:SystemRoot 'System32\pnputil.exe'
$output = (& $pnputil /add-driver $inf /install 2>&1 | Out-String)
$code = $LASTEXITCODE
if($code -ne 0 -and $code -ne 3010) {{
  throw ('pnputil meldete Fehlercode ' + $code + ': ' + $output.Trim())
}}
Add-PrinterDriver -Name $driver -ErrorAction Stop
";

            await PrivilegeHelper.RunPowerShellElevatedAsync(script);

            var installed = await PrinterInstaller.GetDriverNamesAsync();
            return installed.Any(x =>
                x.Equals(driverName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try
            {
                File.Delete(download.ZipPath);
            }
            catch
            {
            }

            try
            {
                if (Directory.Exists(importDir))
                    Directory.Delete(importDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task<DriverSignatureResult> CheckSignatureAsync(string directory)
    {
        var script = $@"
$ErrorActionPreference='Stop'
$dir={PowerShellRunner.Quote(directory)}
$cats = @(Get-ChildItem -LiteralPath $dir -Filter *.cat -File -ErrorAction SilentlyContinue)
if($cats.Count -eq 0) {{
  [pscustomobject]@{{ Valid=$false; Signer=''; Detail='Das Treiberpaket enthält keine Katalogdatei (.cat).' }} | ConvertTo-Json -Compress
  return
}}

$bad = @()
$signer = ''
foreach($cat in $cats) {{
  $s = Get-AuthenticodeSignature -LiteralPath $cat.FullName
  if([string]$s.Status -ne 'Valid') {{
    $bad += ($cat.Name + ': ' + [string]$s.Status)
  }}
  elseif(-not $signer -and $s.SignerCertificate) {{
    $signer = [string]$s.SignerCertificate.Subject
  }}
}}

[pscustomobject]@{{ Valid=($bad.Count -eq 0); Signer=$signer; Detail=($bad -join '; ') }} | ConvertTo-Json -Compress
";

        var result = await PowerShellRunner.RunAsync(script);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return new DriverSignatureResult
            {
                Valid = false,
                Detail = string.IsNullOrWhiteSpace(result.StdErr)
                    ? "Die Signatur konnte nicht geprüft werden."
                    : result.StdErr.Trim()
            };
        }

        return JsonSerializer.Deserialize<DriverSignatureResult>(
                   result.StdOut.Trim(),
                   JsonStore.Options)
               ?? new DriverSignatureResult
               {
                   Valid = false,
                   Detail = "Die Signatur konnte nicht geprüft werden."
               };
    }

    private static string ShortSigner(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return "unbekanntem Herausgeber";

        var match = Regex.Match(subject, "CN=(?:\"([^\"]+)\"|([^,]+))");
        if (!match.Success)
            return subject;

        return (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim();
    }
}

/// <summary>Kleines Fortschrittsfenster mit Abbrechen, während der Server bestätigt und sendet.</summary>
internal sealed class DriverTransferProgressForm : Form
{
    private readonly Label _label;
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken CancellationToken => _cts.Token;

    public DriverTransferProgressForm(string serverName)
    {
        Text = "SimplePrint – Treiber vom Server";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        TopMost = true;
        ClientSize = new Size(480, 150);
        Branding.ApplyApplicationIcon(this);

        _label = new Label
        {
            Dock = DockStyle.Top,
            Height = 74,
            Padding = new Padding(12, 12, 12, 0),
            Text = $"Treiber wird bei '{serverName}' angefragt …"
        };

        var bar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Style = ProgressBarStyle.Marquee,
            Height = 18
        };

        var cancel = new Button
        {
            Text = "Abbrechen",
            Width = 110,
            Height = 30
        };

        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 46
        };

        cancel.Location = new Point(ClientSize.Width - cancel.Width - 12, 8);
        cancel.Click += (_, _) =>
        {
            cancel.Enabled = false;
            _label.Text = "Wird abgebrochen …";
            _cts.Cancel();
        };

        panel.Controls.Add(cancel);

        // Zuletzt hinzugefügte angedockte Steuerelemente liegen ganz außen (oben).
        Controls.Add(panel);
        Controls.Add(bar);
        Controls.Add(_label);
    }

    public void SetText(string text) => _label.Text = text;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _cts.Dispose();

        base.Dispose(disposing);
    }
}
