using System.Diagnostics;
using SimplePrint.Common;

namespace SimplePrint.Client.Gui;

internal sealed class UpdateForm : Form
{
    private readonly ReleaseUpdateInfo _release;
    private readonly Label _status = new() { AutoSize = false, Height = 42 };
    private readonly ProgressBar _progress = new() { Height = 18, Minimum = 0, Maximum = 100 };
    private readonly Button _install = new() { Text = "Jetzt aktualisieren", Width = 145, Height = 34 };
    private readonly Button _later = new() { Text = "Später", Width = 95, Height = 34 };
    private readonly Button _releasePage = new() { Text = "Release auf GitHub", Width = 145, Height = 34 };
    private readonly CancellationTokenSource _downloadCancellation = new();

    public bool InstallerStarted { get; private set; }

    public UpdateForm(ReleaseUpdateInfo release)
    {
        _release = release;

        Text = "SimplePrint Update";
        ClientSize = new Size(610, 430);
        MinimumSize = new Size(610, 430);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        Branding.ApplyApplicationIcon(this);

        var title = new Label
        {
            Left = 24,
            Top = 22,
            Width = 560,
            Height = 36,
            Text = $"SimplePrint {release.Version.Major}.{release.Version.Minor}.{release.Version.Build} ist verfügbar",
            Font = new Font(Font.FontFamily, 17, FontStyle.Bold)
        };

        var current = typeof(MainForm).Assembly.GetName().Version;
        var currentText = current is null
            ? "unbekannt"
            : $"{current.Major}.{current.Minor}.{Math.Max(0, current.Build)}";

        var version = new Label
        {
            Left = 26,
            Top = 68,
            Width = 555,
            Height = 24,
            Text = $"Installiert: {currentText}    Neu: {release.TagName}"
        };

        var explanation = new Label
        {
            Left = 26,
            Top = 100,
            Width = 555,
            Height = 44,
            Text = "Der Installer wird direkt aus dem offiziellen GitHub-Release von tojollinor/SimplePrint geladen. Anschließend übernimmt das normale SimplePrint-Setup die Aktualisierung."
        };

        var notesLabel = new Label
        {
            Left = 26,
            Top = 154,
            Width = 555,
            Height = 22,
            Text = "Release-Hinweise:"
        };

        var notes = new TextBox
        {
            Left = 26,
            Top = 178,
            Width = 555,
            Height = 120,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = string.IsNullOrWhiteSpace(release.Notes)
                ? "Für diesen Release wurden keine zusätzlichen Hinweise hinterlegt."
                : release.Notes
        };

        _status.SetBounds(26, 310, 555, 42);
        _status.Text = "Bereit zum Aktualisieren.";

        _progress.SetBounds(26, 350, 555, 18);
        _progress.Visible = false;

        _releasePage.SetBounds(26, 383, 145, 34);
        _later.SetBounds(330, 383, 95, 34);
        _install.SetBounds(436, 383, 145, 34);
        _install.Click += async (_, _) => await InstallAsync();
        _later.Click += (_, _) => Close();
        _releasePage.Click += (_, _) => OpenReleasePage();

        Controls.AddRange([
            title, version, explanation, notesLabel, notes,
            _status, _progress, _releasePage, _later, _install
        ]);

        FormClosed += (_, _) => _downloadCancellation.Dispose();
    }

    private async Task InstallAsync()
    {
        try
        {
            _install.Enabled = false;
            _later.Enabled = false;
            _releasePage.Enabled = false;
            _progress.Visible = true;
            _progress.Style = ProgressBarStyle.Continuous;
            _status.Text = "Installer wird von GitHub heruntergeladen …";

            var progress = new Progress<int>(value =>
            {
                _progress.Value = Math.Clamp(value, 0, 100);
                _status.Text = $"Installer wird heruntergeladen … {value}%";
            });

            var path = await GitHubUpdateService.DownloadInstallerAsync(
                _release,
                progress,
                _downloadCancellation.Token);

            _status.Text = "Download geprüft. Installer wird gestartet …";
            GitHubUpdateService.LaunchInstaller(path);
            InstallerStarted = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Update-Download abgebrochen.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Update konnte nicht gestartet werden: {ex.Message}";
            _install.Enabled = true;
            _later.Enabled = true;
            _releasePage.Enabled = true;
            _progress.Visible = false;
        }
    }

    private void OpenReleasePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _release.ReleaseUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _status.Text = $"GitHub konnte nicht geöffnet werden: {ex.Message}";
        }
    }
}