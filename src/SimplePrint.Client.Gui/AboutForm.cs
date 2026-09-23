namespace SimplePrint.Client.Gui;

internal sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text = "Über SimplePrint";
        Width = 430;
        Height = 250;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Branding.ApplyApplicationIcon(this);

        var logo = new PictureBox
        {
            Left = 24,
            Top = 28,
            Width = 112,
            Height = 112,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = Branding.LoadFixedAboutLogo()
        };
        var title = new Label { Left = 160, Top = 34, Width = 220, Height = 34, Text = "SimplePrint", Font = new Font(Font.FontFamily, 18, FontStyle.Bold) };
        var version = new Label { Left = 162, Top = 76, Width = 220, Height = 24, Text = $"Version {Application.ProductVersion}" };
        var description = new Label { Left = 162, Top = 108, Width = 220, Height = 50, Text = "Lokaler RAW-Drucktransport\r\nmit automatischer Servererkennung." };
        var close = new Button { Text = "Schließen", Left = 292, Top = 166, Width = 95, Height = 32, DialogResult = DialogResult.OK };

        Controls.AddRange([logo, title, version, description, close]);
        AcceptButton = close;
        CancelButton = close;
    }
}
