namespace SimplePrint.Gui;

internal enum UiHealth
{
    Unknown,
    Ok,
    Warning,
    Error
}

internal static class UiColors
{
    public static readonly Color Ok = Color.FromArgb(28, 128, 52);
    public static readonly Color Warning = Color.FromArgb(196, 122, 0);
    public static readonly Color Error = Color.FromArgb(190, 30, 45);
    public static readonly Color Muted = Color.FromArgb(110, 110, 110);

    public static Color For(UiHealth health) => health switch
    {
        UiHealth.Ok => Ok,
        UiHealth.Warning => Warning,
        UiHealth.Error => Error,
        _ => Muted
    };

    public static string Glyph(UiHealth health) => health switch
    {
        UiHealth.Ok => "✔",
        UiHealth.Warning => "⚠",
        UiHealth.Error => "✖",
        _ => "…"
    };
}

/// <summary>
/// Eine Statuszeile mit farbigem Symbol (grüner Haken, gelbe Warnung, rotes Kreuz),
/// Titel, Beschreibung und optionalen Buttons rechts.
/// </summary>
internal sealed class StatusRow : TableLayoutPanel
{
    private readonly Label _icon = new()
    {
        AutoSize = false,
        Width = 30,
        Height = 30,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI Symbol", 14f, FontStyle.Bold),
        Anchor = AnchorStyles.Left,
        Text = "…",
        ForeColor = UiColors.Muted
    };

    private readonly Label _detail = new()
    {
        AutoSize = true,
        MaximumSize = new Size(420, 0),
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 6, 3, 6)
    };

    public UiHealth Health { get; private set; } = UiHealth.Unknown;

    public StatusRow(string title, params Control[] actions)
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 4;
        RowCount = 1;
        Width = 840;
        Margin = new Padding(0, 0, 0, 6);
        Padding = new Padding(8, 4, 8, 4);
        BackColor = Color.FromArgb(246, 247, 249);

        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Controls.Add(_icon, 0, 0);

        Controls.Add(
            new Label
            {
                Text = title,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold)
            },
            1,
            0);

        Controls.Add(_detail, 2, 0);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0)
        };

        foreach (var action in actions)
            buttons.Controls.Add(action);

        Controls.Add(buttons, 3, 0);
    }

    public void SetState(UiHealth health, string detail)
    {
        Health = health;
        _icon.Text = UiColors.Glyph(health);
        _icon.ForeColor = UiColors.For(health);
        _detail.Text = detail;
    }
}

/// <summary>Farbiges Gesamtbanner oben in der Übersicht.</summary>
internal sealed class StatusBanner : Panel
{
    private readonly Label _label = new()
    {
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 13f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(22, 0, 0, 0)
    };

    public StatusBanner()
    {
        Dock = DockStyle.Top;
        Height = 58;
        Controls.Add(_label);
        SetState(UiHealth.Unknown, "Status wird geprüft …");
    }

    public void SetState(UiHealth health, string text)
    {
        BackColor = health switch
        {
            UiHealth.Ok => Color.FromArgb(226, 244, 232),
            UiHealth.Warning => Color.FromArgb(255, 243, 214),
            UiHealth.Error => Color.FromArgb(252, 228, 230),
            _ => Color.FromArgb(238, 238, 238)
        };

        _label.ForeColor = UiColors.For(health);
        _label.Text = $"{UiColors.Glyph(health)}  {text}";
    }
}
