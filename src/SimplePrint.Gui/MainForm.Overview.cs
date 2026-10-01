using SimplePrint.Common;

namespace SimplePrint.Gui;

public sealed partial class MainForm
{
    private readonly StatusBanner _banner = new();
    private readonly Label _overviewDevice = ValueLabel();
    private readonly Label _overviewShared = ValueLabel();
    private readonly Label _overviewNetwork = ValueLabel();
    private readonly Label _overviewServers = ValueLabel();
    private readonly Label _overviewClients = ValueLabel();
    private readonly Label _overviewPeers = ValueLabel();

    private TabPage CreateOverviewTab()
    {
        var tab = new TabPage("Übersicht");

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(22, 18, 22, 8),
            ColumnCount = 2,
            RowCount = 6
        };

        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddOverviewRow(panel, 0, "Dieses Gerät", _overviewDevice);
        AddOverviewRow(panel, 1, "Eigene freigegebene Drucker", _overviewShared);
        AddOverviewRow(panel, 2, "Verwendete Netzwerkdrucker", _overviewNetwork);
        AddOverviewRow(panel, 3, "Server (du nutzt deren Drucker)", _overviewServers);
        AddOverviewRow(panel, 4, "Clients (nutzen deine Drucker)", _overviewClients);
        AddOverviewRow(panel, 5, "Aktive SimplePrint-Geräte im Netzwerk", _overviewPeers);

        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 60,
            Padding = new Padding(22, 8, 22, 8),
            ForeColor = UiColors.Muted,
            Text =
                "Jede SimplePrint-Installation kann gleichzeitig eigene Drucker bereitstellen " +
                "und Drucker beliebig vieler anderer SimplePrint-Geräte verwenden."
        };

        // Das zuletzt hinzugefügte Steuerelement wird ganz oben angedockt.
        tab.Controls.Add(info);
        tab.Controls.Add(panel);
        tab.Controls.Add(_banner);
        return tab;
    }

    private static void AddOverviewRow(
        TableLayoutPanel panel,
        int row,
        string title,
        Label value)
    {
        panel.Controls.Add(BoldLabel(title), 0, row);
        panel.Controls.Add(value, 1, row);
    }

    private void RefreshOverview()
    {
        var serverIds = _config.NetworkPrinters
            .Where(x => x.Enabled)
            .Select(x => x.SourceDeviceId)
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToHashSet();

        var clientIds = _peers
            .Where(x => x.Subscriptions.Any(s => s.SourceDeviceId == _config.DeviceId))
            .Select(x => x.DeviceId)
            .Distinct()
            .ToHashSet();

        _overviewDevice.Text = _deviceName.Text;
        _overviewShared.Text = _config.SharedPrinters.Count(x => x.Enabled).ToString();
        _overviewNetwork.Text = _config.NetworkPrinters.Count(x => x.Enabled).ToString();
        _overviewServers.Text = _peers.Count(x => serverIds.Contains(x.DeviceId)).ToString();
        _overviewClients.Text = clientIds.Count.ToString();
        _overviewPeers.Text = _peers.Count.ToString();

        UpdateBanner();
        UpdateTrayIcon();
    }

    private void UpdateBanner()
    {
        var states = new[] { _healthService, _healthNetwork, _healthFirewall, _healthStartup };

        if (states.All(x => x == UiHealth.Unknown))
        {
            _banner.SetState(UiHealth.Unknown, "Status wird geprüft …");
            return;
        }

        var errors = states.Count(x => x == UiHealth.Error);
        var warnings = states.Count(x => x == UiHealth.Warning);

        if (errors > 0)
        {
            _banner.SetState(
                UiHealth.Error,
                errors == 1
                    ? "1 Problem gefunden – Details unter „Einstellungen“"
                    : $"{errors} Probleme gefunden – Details unter „Einstellungen“");
        }
        else if (warnings > 0)
        {
            _banner.SetState(
                UiHealth.Warning,
                warnings == 1
                    ? "1 Hinweis – Details unter „Einstellungen“"
                    : $"{warnings} Hinweise – Details unter „Einstellungen“");
        }
        else
        {
            _banner.SetState(UiHealth.Ok, "Alles in Ordnung");
        }
    }

    private void UpdateTrayIcon()
    {
        var level = _healthService == UiHealth.Ok
            ? "Green"
            : "Yellow";

        // Das Infobereich-Symbol wird nur bei einem Statuswechsel neu erzeugt.
        if (!string.Equals(level, _trayLevel, StringComparison.Ordinal))
        {
            var next = Branding.CreateStatusIcon(level);
            if (next is not null)
            {
                var previous = _tray.Icon;
                _tray.Icon = next;
                previous?.Dispose();
                _trayLevel = level;
            }
        }

        _tray.Text = level == "Green"
            ? "SimplePrint - bereit"
            : "SimplePrint - Dienst prüfen";
    }
}
