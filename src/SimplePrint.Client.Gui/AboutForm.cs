using System.Diagnostics;

namespace SimplePrint.Client.Gui;

internal sealed class AboutForm : Form
{
    private const string RepositoryUrl = "https://github.com/tojollinor/SimplePrint";

    public AboutForm()
    {
        Text = "Über SimplePrint";
        ClientSize = new Size(560, 320);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        Branding.ApplyApplicationIcon(this);

        var assemblyVersion = typeof(AboutForm).Assembly.GetName().Version;
        var versionText = assemblyVersion is null
            ? "unbekannt"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";

        var logo = new PictureBox
        {
            Left = 24,
            Top = 24,
            Width = 180,
            Height = 180,
            Padding = new Padding(6),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = Branding.LoadFixedAboutLogo()
        };

        var title = new Label
        {
            Left = 225,
            Top = 36,
            Width = 305,
            Height = 42,
            Text = "SimplePrint Client",
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold)
        };

        var version = new Label
        {
            Left = 228,
            Top = 86,
            Width = 300,
            Height = 24,
            Text = $"Version {versionText} · Protokoll {SimplePrint.Common.Protocol.Version}"
        };

        var description = new Label
        {
            Left = 228,
            Top = 126,
            Width = 300,
            Height = 70,
            Text = "Lokaler RAW-Drucktransport\nmit automatischer Servererkennung und End-to-End-Diagnose."
        };

        var copyright = new Label
        {
            Left = 28,
            Top = 224,
            Width = 500,
            Height = 22,
            Text = "© 2026 tojollinor"
        };

        var link = new LinkLabel
        {
            Left = 28,
            Top = 248,
            Width = 500,
            Height = 24,
            Text = "github.com/tojollinor/SimplePrint",
            AutoSize = false
        };

        link.LinkClicked += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = RepositoryUrl,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        };

        var close = new Button
        {
            Text = "Schließen",
            Left = 425,
            Top = 272,
            Width = 105,
            Height = 32,
            DialogResult = DialogResult.OK
        };

        Controls.AddRange([logo, title, version, description, copyright, link, close]);
        AcceptButton = close;
        CancelButton = close;
    }
}