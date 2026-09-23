namespace SimplePrint.Client.Gui;

internal sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text = "Über SimplePrint";
        ClientSize = new Size(500, 270);
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
            Width = 150,
            Height = 150,
            Padding = new Padding(8),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = Branding.LoadFixedAboutLogo()
        };

        var title = new Label
        {
            Left = 195,
            Top = 36,
            Width = 270,
            Height = 40,
            Text = "SimplePrint Client",
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold)
        };

        var version = new Label
        {
            Left = 198,
            Top = 84,
            Width = 260,
            Height = 24,
            Text = $"Version {versionText}"
        };

        var description = new Label
        {
            Left = 198,
            Top = 120,
            Width = 270,
            Height = 64,
            Text = "Lokaler RAW-Drucktransport\r\nmit automatischer Servererkennung."
        };

        var close = new Button
        {
            Text = "Schließen",
            Left = 365,
            Top = 218,
            Width = 105,
            Height = 32,
            DialogResult = DialogResult.OK
        };

        Controls.AddRange([logo, title, version, description, close]);
        AcceptButton = close;
        CancelButton = close;
    }
}
