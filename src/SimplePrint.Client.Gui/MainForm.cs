using System.IO.Compression;
using System.Net.Sockets;
using SimplePrint.Common;

namespace SimplePrint.Client.Gui;

public sealed class MainForm : Form
{
    private readonly Label _agent = new() { AutoSize = true };
    private readonly Label _scan = new() { AutoSize = true };
    private readonly DataGridView _available = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private readonly DataGridView _installed = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private ClientConfig _config = new();
    private List<DiscoveredServer> _servers = [];

    private sealed record AvailableTag(DiscoveredServer Server, DiscoveredPrinter Printer);

    public MainForm()
    {
        Text = "SimplePrint Client";
        Width = 980; Height = 700; StartPosition = FormStartPosition.CenterScreen;
        Branding.ApplyApplicationIcon(this);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 96, Padding = new Padding(10), ColumnCount = 2, RowCount = 1 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.Controls.Add(Branding.CreateGuiLogoBox(), 0, 0);
        var headerText = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10, 2, 0, 0) };
        headerText.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        headerText.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        headerText.Controls.Add(new Label { Text = "SimplePrint Client", AutoSize = true, Font = new Font(Font.FontFamily, 17, FontStyle.Bold), Margin = new Padding(0, 4, 0, 0) }, 0, 0);
        var status = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, WrapContents = false, Margin = new Padding(0) };
        status.Controls.AddRange([new Label { Text = "Client-Agent:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, _agent,
            new Label { Text = "   Suche:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, _scan]);
        headerText.Controls.Add(status, 0, 1);
        top.Controls.Add(headerText, 1, 0);

        _available.Columns.Add("server", "Server"); _available.Columns.Add("printer", "Verfügbarer Drucker"); _available.Columns.Add("driver", "Treiberhinweis"); _available.Columns.Add("status", "Status");
        _installed.Columns.Add("printer", "Installierter Drucker"); _installed.Columns.Add("server", "Server"); _installed.Columns.Add("driver", "Treiber"); _installed.Columns.Add("port", "Lokaler Proxy-Port");

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var t1 = new TabPage("Verfügbare Drucker"); var t2 = new TabPage("Installierte Drucker");
        t1.Controls.Add(_available); t2.Controls.Add(_installed); tabs.TabPages.AddRange([t1, t2]);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(8) };
        foreach (var b in MakeButtons()) buttons.Controls.Add(b);
        Controls.Add(tabs); Controls.Add(top); Controls.Add(buttons);
        Shown += async (_, _) => await RefreshAllAsync();
    }

    private IEnumerable<Button> MakeButtons()
    {
        Button B(string text, EventHandler click) { var b = new Button { Text = text, AutoSize = true, Height = 32 }; b.Click += click; return b; }
        return [
            B("Server suchen", async (_,_) => await RefreshAllAsync()),
            B("Drucker installieren", async (_,_) => await InstallSelectedAsync()),
            B("Drucker entfernen", async (_,_) => await RemoveSelectedAsync()),
            B("Verbindung testen", async (_,_) => await TestConnectionsAsync()),
            B("Testseite", (_,_) => TestPage()),
            B("Diagnosepaket", async (_,_) => await CreateDiagnosticsAsync()),
            B("Über", (_,_) => ShowAbout())
        ];
    }

    private async Task RefreshAllAsync()
    {
        _config = JsonStore.LoadOrCreate(AppPaths.ClientConfig, () => new ClientConfig());
        var svc = await PowerShellRunner.RunAsync("(Get-Service -Name SimplePrintClient -ErrorAction SilentlyContinue).Status");
        _agent.Text = string.IsNullOrWhiteSpace(svc.StdOut) ? "nicht installiert" : svc.StdOut.Trim();
        _scan.Text = "suche ...";
        try { _servers = (await Discovery.DiscoverAsync(_config.DiscoveryPort, 1000)).ToList(); }
        catch { _servers = []; }
        _scan.Text = _servers.Count == 0 ? "kein Server gefunden" : $"{_servers.Count} Server gefunden";

        _available.Rows.Clear();
        foreach (var s in _servers)
        foreach (var p in s.Announcement.Printers)
        {
            var installed = _config.Mappings.Any(m => m.ServerId == s.Announcement.ServerId && m.PrinterId == p.Id);
            var i = _available.Rows.Add(s.Announcement.ServerName, p.DisplayName + (installed ? "  ✓" : ""), p.DriverName, p.Status);
            _available.Rows[i].Tag = new AvailableTag(s, p);
        }

        _installed.Rows.Clear();
        foreach (var m in _config.Mappings)
        {
            var i = _installed.Rows.Add(m.LocalPrinterName, m.ServerName, m.DriverName, m.LocalProxyPort);
            _installed.Rows[i].Tag = m.PortName;
        }
    }

    private async Task InstallSelectedAsync()
    {
        if (_available.SelectedRows.Count == 0) { MessageBox.Show("Bitte einen verfügbaren Drucker auswählen."); return; }
        var tag = (AvailableTag)_available.SelectedRows[0].Tag;
        if (_config.Mappings.Any(m => m.ServerId == tag.Server.Announcement.ServerId && m.PrinterId == tag.Printer.Id)) { MessageBox.Show("Dieser Drucker ist bereits installiert."); return; }
        try
        {
            var drivers = await PrinterInstaller.GetDriverNamesAsync();
            if (drivers.Count == 0) { MessageBox.Show("Auf diesem PC wurden keine Druckertreiber gefunden."); return; }
            var driver = drivers.FirstOrDefault(d => d.Equals(tag.Printer.DriverName, StringComparison.OrdinalIgnoreCase));
            if (driver is null) driver = ChooseDriver(drivers, tag.Printer.DriverName);
            if (driver is null) return;

            var localPort = AllocatePort();
            var shortServer = tag.Server.Announcement.ServerId.ToString("N")[..8];
            var shortPrinter = tag.Printer.Id.ToString("N")[..8];
            var portName = $"SimplePrint_{shortServer}_{shortPrinter}";
            var localName = UniqueLocalName(tag.Printer.DisplayName);
            var mapping = new ClientPrinterMapping
            {
                ServerId = tag.Server.Announcement.ServerId,
                PrinterId = tag.Printer.Id,
                ServerName = tag.Server.Announcement.ServerName,
                PrinterDisplayName = tag.Printer.DisplayName,
                LocalPrinterName = localName,
                DriverName = driver,
                PortName = portName,
                LocalProxyPort = localPort
            };

            _config.Mappings.Add(mapping);
            JsonStore.Save(AppPaths.ClientConfig, _config);
            await Task.Delay(1600); // Agent liest die neue Zuordnung ein und öffnet localhost:Port.
            try { await PrinterInstaller.InstallAsync(mapping); }
            catch
            {
                _config.Mappings.Remove(mapping); JsonStore.Save(AppPaths.ClientConfig, _config); throw;
            }
            await RefreshAllAsync();
            MessageBox.Show($"'{localName}' wurde installiert.\n\nDer Drucker verwendet den lokalen Originaltreiber und SimplePrint transportiert nur die RAW-Daten.", "Fertig");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Installation fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private string? ChooseDriver(List<string> drivers, string suggested)
    {
        using var dlg = new Form { Text = "Druckertreiber auswählen", Width = 700, Height = 210, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var info = new Label { Left = 20, Top = 15, Width = 640, Height = 40, Text = $"Der Server verwendet '{suggested}'. Wähle den passenden lokal installierten Treiber:" };
        var combo = new ComboBox { Left = 20, Top = 65, Width = 640, DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(drivers.Cast<object>().ToArray()); combo.SelectedIndex = 0;
        var ok = new Button { Text = "Verwenden", Left = 470, Top = 110, Width = 90, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Abbrechen", Left = 570, Top = 110, Width = 90, DialogResult = DialogResult.Cancel };
        dlg.Controls.AddRange([info, combo, ok, cancel]); dlg.AcceptButton = ok; dlg.CancelButton = cancel;
        return dlg.ShowDialog(this) == DialogResult.OK ? combo.SelectedItem?.ToString() : null;
    }

    private int AllocatePort()
    {
        var used = _config.Mappings.Select(m => m.LocalProxyPort).ToHashSet();
        for (var p = _config.LocalPortStart; p <= _config.LocalPortEnd; p++)
        {
            if (used.Contains(p)) continue;
            try { var l = new TcpListener(System.Net.IPAddress.Loopback, p); l.Start(); l.Stop(); return p; }
            catch { }
        }
        throw new InvalidOperationException("Kein freier lokaler SimplePrint-Port verfügbar.");
    }

    private string UniqueLocalName(string requested)
    {
        var names = _config.Mappings.Select(m => m.LocalPrinterName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(requested)) return requested;
        for (var i = 2; i < 100; i++) { var n = $"{requested} ({i})"; if (!names.Contains(n)) return n; }
        return requested + " (SimplePrint)";
    }

    private async Task RemoveSelectedAsync()
    {
        if (_installed.SelectedRows.Count == 0) { MessageBox.Show("Bitte einen installierten Drucker auswählen."); return; }
        var portName = (string)_installed.SelectedRows[0].Tag;
        var mapping = _config.Mappings.First(m => m.PortName == portName);
        if (MessageBox.Show($"'{mapping.LocalPrinterName}' entfernen?", "Entfernen", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            await PrinterInstaller.RemoveAsync(mapping);
            _config.Mappings.Remove(mapping); JsonStore.Save(AppPaths.ClientConfig, _config);
            await RefreshAllAsync();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Entfernen fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task TestConnectionsAsync()
    {
        if (_servers.Count == 0)
        {
            await RefreshAllAsync();
            if (_servers.Count == 0) { MessageBox.Show("Kein SimplePrint-Server gefunden.", "Verbindung"); return; }
        }
        var lines = new List<string>();
        foreach (var s in _servers)
        {
            try
            {
                using var tcp = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await tcp.ConnectAsync(s.Address, s.Announcement.GatewayPort, cts.Token);
                using var stream = tcp.GetStream();
                await stream.WriteAsync(Protocol.CreateGatewayHeader(Guid.Empty), cts.Token);
                var reply = new byte[6];
                var ok = await Protocol.ReadExactAsync(stream, reply, cts.Token) && System.Text.Encoding.ASCII.GetString(reply) == "SPROK1";
                lines.Add($"{s.Announcement.ServerName} ({s.Address}): {(ok ? "OK" : "keine gültige Antwort")}");
            }
            catch (Exception ex) { lines.Add($"{s.Announcement.ServerName} ({s.Address}): FEHLER – {ex.Message}"); }
        }
        MessageBox.Show(string.Join(Environment.NewLine, lines), "SimplePrint Verbindungstest");
    }

    private void TestPage()
    {
        if (_installed.SelectedRows.Count == 0) return;
        var portName = (string)_installed.SelectedRows[0].Tag;
        PrinterInstaller.PrintTestPage(_config.Mappings.First(m => m.PortName == portName).LocalPrinterName);
    }

    private void ShowAbout()
    {
        using var dlg = new AboutForm();
        dlg.ShowDialog(this);
    }

    private async Task CreateDiagnosticsAsync()
    {
        using var save = new SaveFileDialog { Filter = "ZIP-Datei|*.zip", FileName = $"SimplePrint-Client-Diagnose-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var zip = ZipFile.Open(save.FileName, ZipArchiveMode.Create);
            if (File.Exists(AppPaths.ClientConfig)) zip.CreateEntryFromFile(AppPaths.ClientConfig, "config.json", CompressionLevel.Optimal);
            if (File.Exists(AppPaths.ClientLog)) zip.CreateEntryFromFile(AppPaths.ClientLog, "client.log", CompressionLevel.Optimal);
            var e = zip.CreateEntry("diagnostics.txt"); using var w = new StreamWriter(e.Open()); await w.WriteAsync(await PrinterInstaller.GetDiagnosticsAsync());
            var discovery = zip.CreateEntry("discovery.txt"); using var dw = new StreamWriter(discovery.Open());
            foreach (var s in _servers) await dw.WriteLineAsync($"{s.Announcement.ServerName} {s.Address}:{s.Announcement.GatewayPort} id={s.Announcement.ServerId} printers={s.Announcement.Printers.Count}");
            MessageBox.Show("Diagnosepaket wurde erstellt.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Diagnose", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
