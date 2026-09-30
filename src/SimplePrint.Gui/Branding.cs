using System.Reflection;
using System.Runtime.InteropServices;

namespace SimplePrint.Gui;

internal static class Branding
{
    private const string EmbeddedLogoResource = "SimplePrint.DefaultLogo.png";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static void ApplyApplicationIcon(Form form)
    {
        try
        {
            using var icon = LoadFixedIcon();
            if (icon is not null)
                form.Icon = (Icon)icon.Clone();
        }
        catch
        {
        }
    }

    public static Icon? LoadFixedIcon()
    {
        var path = FindAsset("app.ico");
        if (path is not null)
        {
            try
            {
                using var icon = new Icon(path);
                return (Icon)icon.Clone();
            }
            catch
            {
            }
        }

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            return icon is null ? null : (Icon)icon.Clone();
        }
        catch
        {
            return null;
        }
    }

    public static PictureBox CreateGuiLogoBox() => new()
    {
        Dock = DockStyle.Fill,
        Margin = new Padding(0),
        Padding = new Padding(4),
        SizeMode = PictureBoxSizeMode.Zoom,
        Image = LoadGuiLogo()
    };

    public static Icon? CreateStatusIcon(string level)
    {
        try
        {
            using var baseIcon = LoadFixedIcon();
            if (baseIcon is null)
                return null;

            using var bitmap = new Bitmap(32, 32);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.DrawIcon(baseIcon, new Rectangle(0, 0, 32, 32));

            var dot = level.Equals("Red", StringComparison.OrdinalIgnoreCase)
                ? Color.Red
                : level.Equals("Yellow", StringComparison.OrdinalIgnoreCase)
                    ? Color.Goldenrod
                    : Color.LimeGreen;

            using var brush = new SolidBrush(dot);
            using var border = new Pen(Color.White, 2);
            graphics.FillEllipse(brush, 20, 20, 11, 11);
            graphics.DrawEllipse(border, 20, 20, 11, 11);

            var handle = bitmap.GetHicon();
            try
            {
                using var icon = Icon.FromHandle(handle);
                return (Icon)icon.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        catch
        {
            return LoadFixedIcon();
        }
    }

    private static Image? LoadGuiLogo()
    {
        var custom = FindAsset("logo.png");
        if (custom is not null)
        {
            try
            {
                var bytes = File.ReadAllBytes(custom);
                using var stream = new MemoryStream(bytes);
                using var image = Image.FromStream(stream);
                return new Bitmap(image);
            }
            catch
            {
            }
        }

        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(EmbeddedLogoResource);

            if (stream is null)
                return null;

            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindAsset(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        for (var i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "assets", file);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
