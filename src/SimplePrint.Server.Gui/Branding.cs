using System.Reflection;

namespace SimplePrint.Server.Gui;

internal static class Branding
{
    private const string EmbeddedLogoResource = "SimplePrint.DefaultLogo.png";
    private const string FixedIconName = "app-0.1.6.ico";

    public static void ApplyApplicationIcon(Form form)
    {
        try
        {
            using var icon = LoadFixedIcon();
            if (icon is not null) form.Icon = (Icon)icon.Clone();
        }
        catch { }
    }

    public static Icon? LoadFixedIcon()
    {
        var path = FindAsset(FixedIconName) ?? FindAsset("app.ico");
        if (path is not null)
        {
            try
            {
                using var icon = new Icon(path);
                return (Icon)icon.Clone();
            }
            catch { }
        }

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            return icon is null ? null : (Icon)icon.Clone();
        }
        catch { return null; }
    }

    public static PictureBox CreateGuiLogoBox() => new()
    {
        Dock = DockStyle.Fill,
        Margin = new Padding(0),
        Padding = new Padding(4),
        SizeMode = PictureBoxSizeMode.Zoom,
        Image = LoadGuiLogo()
    };

    public static Image? LoadGuiLogo()
    {
        var customPath = FindAsset("logo.png");
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
            using var icon = LoadFixedIcon();
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

    private static string? FindAsset(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "assets", file);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
