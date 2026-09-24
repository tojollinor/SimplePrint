using SimplePrint.Common;

namespace SimplePrint.Client.Gui;

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
            Size = new Size(675, 36)
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

        static bool IsNeutralText(string value) =>
            string.IsNullOrWhiteSpace(value) ||
            value.Equals("Nicht verfügbar", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Nicht geprüft", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Unbekannt", StringComparison.OrdinalIgnoreCase);

        static bool ContainsAny(string? value, params string[] needles)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return needles.Any(x =>
                value.Contains(x, StringComparison.OrdinalIgnoreCase));
        }

        void Add(string name, string? value, bool? ok = null)
        {
            var display = string.IsNullOrWhiteSpace(value) ? "Nicht verfügbar" : value.Trim();

            if (IsNeutralText(display))
                ok = null;

            var text = ok switch
            {
                true => "✓ " + display,
                false => "✗ " + display,
                _ => display
            };

            var row = new ListViewItem(name) { UseItemStyleForSubItems = false };
            var resultCell = row.SubItems.Add(text);

            resultCell.ForeColor = ok switch
            {
                true => Color.DarkGreen,
                false => Color.DarkRed,
                _ => SystemColors.WindowText
            };

            list.Items.Add(row);
        }

        var queueStatusCritical = ContainsAny(
            status.QueueStatus,
            "notoner", "no toner", "toner leer",
            "paperout", "no paper", "kein papier",
            "paperjam", "papierstau",
            "dooropen", "klappe offen",
            "offline", "error", "fehler");

        var criticalDeviceWarning = status.Warnings.Any(x =>
            ContainsAny(
                x,
                "toner leer",
                "kein papier",
                "papierfach leer",
                "papierstau",
                "klappe offen",
                "drucker offline",
                "service erforderlich",
                "papierfach fehlt",
                "ausgabefach fehlt",
                "verbrauchsmaterial fehlt",
                "ausgabefach voll"));

        bool? paperOk = null;
        if (!string.IsNullOrWhiteSpace(status.PaperStatus))
        {
            if (ContainsAny(status.PaperStatus, "keine papierwarnung"))
                paperOk = true;
            else if (ContainsAny(
                         status.PaperStatus,
                         "kein papier", "papierfach leer", "papierstau",
                         "papierfach fehlt"))
                paperOk = false;
        }

        bool? deviceOk = null;
        if (!string.IsNullOrWhiteSpace(status.DeviceStatus) &&
            !ContainsAny(status.DeviceStatus, "nur über windows verfügbar", "nicht verfügbar"))
        {
            deviceOk = !criticalDeviceWarning;
        }

        Add("Drucker", status.PrinterName,
            string.IsNullOrWhiteSpace(status.PrinterName) ? null : true);

        if (!string.IsNullOrWhiteSpace(status.ClientTransportStatus))
            Add("Client-Pfad", status.ClientTransportStatus, status.ClientTransportReady);

        if (!string.IsNullOrWhiteSpace(status.ServerTransportStatus))
            Add("Server-Pfad", status.ServerTransportStatus, status.ServerTransportReady);

        Add("Windows-Warteschlange",
            status.QueueExists ? "Vorhanden" : "Fehlt",
            status.QueueExists);

        Add("Queue-Status",
            status.QueueStatus,
            string.IsNullOrWhiteSpace(status.QueueStatus)
                ? null
                : !(status.QueueOffline || status.QueuePaused || queueStatusCritical));

        Add("Treiber", status.DriverName,
            string.IsNullOrWhiteSpace(status.DriverName) ? null : true);

        Add("Port", status.PortName,
            string.IsNullOrWhiteSpace(status.PortName) ? null : true);

        Add("Geräteadresse", status.DeviceAddress,
            string.IsNullOrWhiteSpace(status.DeviceAddress) ? null : true);

        Add("Geräteport",
            status.DevicePort?.ToString(),
            status.DevicePort is null ? null : true);

        Add("Ping",
            status.PingReachable is null
                ? "Nicht geprüft"
                : status.PingReachable.Value ? "Erreichbar" : "Keine Antwort",
            status.PingReachable);

        Add("TCP",
            status.TcpReachable is null
                ? "Nicht geprüft"
                : status.TcpReachable.Value ? "Erreichbar" : "Nicht erreichbar",
            status.TcpReachable);

        Add("SNMP",
            status.SnmpAvailable ? "Verfügbar" : "Nicht verfügbar",
            status.SnmpAvailable ? true : null);

        Add("Gerätestatus", status.DeviceStatus, deviceOk);
        Add("Papier", status.PaperStatus, paperOk);
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
        {
            var stateText = item.State;
            Color? stateColor = null;

            if (item.State.Equals("Leer", StringComparison.OrdinalIgnoreCase))
            {
                stateText = "✗ Leer";
                stateColor = Color.DarkRed;
            }
            else if (item.State.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
                     item.State.Equals("Vorhanden", StringComparison.OrdinalIgnoreCase))
            {
                stateText = "✓ " + item.State;
                stateColor = Color.DarkGreen;
            }
            else if (item.State.Equals("Niedrig", StringComparison.OrdinalIgnoreCase))
            {
                stateColor = Color.DarkGoldenrod;
            }

            var row = supplies.Rows.Add(
                item.Name,
                item.Percent is null ? "" : item.Percent + " %",
                stateText);

            if (stateColor.HasValue)
                supplies.Rows[row].Cells["state"].Style.ForeColor = stateColor.Value;
        }

        if (status.Supplies.Count == 0)
            supplies.Rows.Add(
                "Keine Daten",
                "",
                "Drucker liefert keine standardisierten Verbrauchsmaterialdaten.");

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
