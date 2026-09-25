using SimplePrint.Common;

namespace SimplePrint.Server.Gui;

internal sealed class UpdateProgressForm : Form
{
    private readonly string _requestPath;
    private readonly Label _status = new() { AutoSize = false, Height = 46 };
    private readonly Label _downloadLabel = new() { AutoSize = true, Text = "Download" };
    private readonly ProgressBar _download = new() { Minimum = 0, Maximum = 100, Height = 20 };
    private readonly Label _installLabel = new() { AutoSize = true, Text = "Installation" };
    private readonly ProgressBar _install = new() { Minimum = 0, Maximum = 100, Height = 20 };
    private readonly Button _close = new() { Text = "Schließen", Width = 110, Height = 34, Visible = false };

    public UpdateProgressForm(string requestPath)
    {
        _requestPath = requestPath;

        Text = "SimplePrint Server Update";
        ClientSize = new Size(610, 270);
        MinimumSize = new Size(610, 270);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        ShowInTaskbar = true;

        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
        }

        var title = new Label
        {
            Left = 24,
            Top = 22,
            Width = 560,
            Height = 36,
            Text = "SimplePrint wird aktualisiert",
            Font = new Font(Font.FontFamily, 17, FontStyle.Bold)
        };

        _status.SetBounds(26, 66, 555, 46);
        _status.Text = "Update wird vorbereitet …";

        _downloadLabel.SetBounds(26, 120, 555, 22);
        _download.SetBounds(26, 144, 555, 20);

        _installLabel.SetBounds(26, 177, 555, 22);
        _install.SetBounds(26, 201, 555, 20);
        _install.Value = 0;

        _close.SetBounds(471, 230, 110, 34);
        _close.Click += (_, _) => Close();

        Controls.AddRange([
            title,
            _status,
            _downloadLabel,
            _download,
            _installLabel,
            _install,
            _close
        ]);

        Shown += async (_, _) => await ApplyUpdateAsync();
    }

    private async Task ApplyUpdateAsync()
    {
        try
        {
            var request = GitHubUpdateService.LoadUpdateRequest(_requestPath);

            _status.Text = $"SimplePrint {request.Release.TagName} wird heruntergeladen …";
            var progress = new Progress<int>(value =>
            {
                var percent = Math.Clamp(value, 0, 100);
                _download.Value = percent;
                _downloadLabel.Text = $"Download · {percent}%";
            });

            var installer = await GitHubUpdateService.DownloadInstallerAsync(
                request.Release,
                progress);

            _download.Value = 100;
            _downloadLabel.Text = "Download · 100%";
            _status.Text = "Download geprüft. Installation läuft automatisch …";

            _install.Style = ProgressBarStyle.Marquee;
            _install.MarqueeAnimationSpeed = 25;
            _installLabel.Text = "Installation · läuft";

            var exitCode = await GitHubUpdateService.InstallSilentlyAsync(installer);
            if (exitCode != 0)
                throw new InvalidOperationException($"Der Installer wurde mit Fehlercode {exitCode} beendet.");

            _install.Style = ProgressBarStyle.Blocks;
            _install.Value = 100;
            _installLabel.Text = "Installation · 100%";
            _status.Text = "Update erfolgreich. SimplePrint wird neu gestartet …";

            await Task.Delay(700);
            GitHubUpdateService.RelaunchAfterSuccessfulUpdate(request);
            GitHubUpdateService.ScheduleUpdaterCleanup(request);

            await Task.Delay(400);
            Close();
        }
        catch (Exception ex)
        {
            ControlBox = true;
            _close.Visible = true;
            _install.Style = ProgressBarStyle.Blocks;
            _status.Text = "Update fehlgeschlagen: " + ex.Message;
        }
    }
}
