using SimplePrint.Common;

namespace SimplePrint.Server.Gui;

internal sealed class PrinterHealthForm : Form
{
    public PrinterHealthForm(PrinterHealthStatus status)
    {
        Text = "SimplePrint Druckbereitschaft";
        ClientSize = new Size(720, 560);
        MinimumSize = new Size(640, 500);
        StartPosition = FormStartPosition.CenterParent;
        Branding.ApplyApplicationIcon(this);

        var header = new Panel { Dock = DockStyle.Top, Height = 90, Padding = new Padding(16) };

        var level = new Label
        {
            AutoSize = true,
            Text = status.Level switch
            {
                "Green" => "● BEREIT",
                "Yellow" => "● WARNUNG",
                "Red" => "● NICHT DRUCKBEREIT",
                _ => "● UNBEKANNT"
            },
            ForeColor = status.Level switch
            {
                "Green" => Color.DarkGreen,
                "Yellow" => Color.DarkGoldenrod,
                "Red" => Color.DarkRed,
                _ => SystemColors.ControlText
            },
            Font = new Font(Font.FontFamily, 14, FontStyle.Bold),
            Location = new Point(16, 12)
        };

        var summary = new Label
        {
            AutoSize = false,
            Text = status.Summary,
            Location = new Point(16, 46),
            Size = new Size(660, 34)
        };

        header.Controls.Add(level);
        header.Controls.Add(summary);

        var tabs = new TabControl { Dock = DockStyle.Fill };

        var overview = new TabPage("Prüfung");
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        list.Columns.Add("Prüfung", 210);
        list.Columns.Add("Ergebnis", 440);

        void Add(string name, string value) =>
            list.Items.Add(new ListViewItem([name, string.IsNullOrWhiteSpace(value) ? "Nicht verfügbar" : value]));

        Add("Drucker", status.PrinterName);
        Add("Windows-Warteschlange", status.QueueExists ? "Vorhanden" : "Fehlt");
        Add("Queue-Status", status.QueueStatus);
        Add("Treiber", status.DriverName);
        Add("Port", status.PortName);
        Add("Geräteadresse", status.DeviceAddress);
        Add("Geräteport", status.DevicePort?.ToString() ?? "");
        Add("Ping", status.PingReachable is null ? "Nicht geprüft" : status.PingReachable.Value ? "Erreichbar" : "Keine Antwort");
        Add("TCP", status.TcpReachable is null ? "Nicht geprüft" : status.TcpReachable.Value ? "Erreichbar" : "Nicht erreichbar");
        Add("SNMP", status.SnmpAvailable ? "Verfügbar" : "Nicht verfügbar");
        Add("Gerätestatus", status.DeviceStatus);
        Add("Papier", status.PaperStatus);
        Add("Geprüft", status.CheckedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"));

        overview.Controls.Add(list);

        var suppliesTab = new TabPage("Verbrauchsmaterial");
        var supplies = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        supplies.Columns.Add("name", "Material");
        supplies.Columns.Add("percent", "Stand");
        supplies.Columns.Add("state", "Status");

        foreach (var item in status.Supplies)
            supplies.Rows.Add(
                item.Name,
                item.Percent is null ? "" : item.Percent + " %",
                item.State);

        if (status.Supplies.Count == 0)
            supplies.Rows.Add("Keine Daten", "", "Drucker liefert keine standardisierten Verbrauchsmaterialdaten.");

        suppliesTab.Controls.Add(supplies);

        var warningsTab = new TabPage("Hinweise");
        var warnings = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = status.Warnings.Count == 0
                ? "Keine Warnungen gemeldet."
                : string.Join(Environment.NewLine, status.Warnings.Select(x => "• " + x))
        };
        warningsTab.Controls.Add(warnings);

        tabs.TabPages.Add(overview);
        tabs.TabPages.Add(suppliesTab);
        tabs.TabPages.Add(warningsTab);

        var closePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };

        var close = new Button
        {
            Text = "Schließen",
            Width = 105,
            Height = 32,
            DialogResult = DialogResult.OK
        };

        closePanel.Controls.Add(close);

        Controls.Add(tabs);
        Controls.Add(header);
        Controls.Add(closePanel);

        AcceptButton = close;
        CancelButton = close;
    }
}
