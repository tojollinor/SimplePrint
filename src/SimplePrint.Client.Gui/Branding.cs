using System.Reflection;

namespace SimplePrint.Client.Gui;

internal static class Branding
{
    private const string EmbeddedLogoResource = "SimplePrint.DefaultLogo.png";

    public static void ApplyApplicationIcon(Form form)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon is not null) form.Icon = (Icon)icon.Clone();
        }
        catch
        {
            // Das feste EXE-Icon bleibt auch dann in Taskleiste/Explorer erhalten.
        }
    }

    public static PictureBox CreateGuiLogoBox()
    {
        return new PictureBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = LoadGuiLogo()
        };
    }

    public static Image? LoadGuiLogo()
    {
        var customPath = FindCustomLogoPath();
        if (customPath is not null)
        {
            try { return LoadImageUnlocked(customPath); }
            catch { }
        }

        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedLogoResource);
            if (stream is null) return null;
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch { return null; }
    }

    public static Image? LoadFixedAboutLogo()
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            return icon?.ToBitmap();
        }
        catch { return null; }
    }

    private static Image LoadImageUnlocked(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var ms = new MemoryStream(bytes);
        using var image = Image.FromStream(ms);
        return new Bitmap(image);
    }

    private static string? FindCustomLogoPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "assets", "logo.png");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
