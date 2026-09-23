using System.IO.Compression;
using SimplePrint.Common;

namespace SimplePrint.Server.Gui;

public sealed class MainForm : Form
{
    private readonly Label _service = new() { AutoSize = true };
    private readonly Label _network = new() { AutoSize = true };
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private ServerConfig _config = new();

    public MainForm()
    {
        Text = "SimplePrint Server";
        Width = 900; Height = 600; StartPosition = FormStartPosition.CenterScreen;
        Branding.ApplyApplicationIcon(this);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 96, Padding = new Padding(10), ColumnCount = 2, RowCount = 1 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.Controls.Add(Branding.CreateGuiLogoBox(), 0, 0);
        var headerText = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10, 2, 0, 0) };
        headerText.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        headerText.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        headerText.Controls.Add(new Label { Text = "SimplePrint Server", AutoSize = true, Font = new Font(Font.FontFamily, 17, FontStyle.Bold), Margin = new Padding(0, 4, 0, 0) }, 0, 0);
        var status = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, WrapContents = false, Margin = new Padding(0) };
        status.Controls.AddRange([new Label { Text = "Dienst:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, _service,
            new Label { Text = "   Netzwerk:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, _network]);
        headerText.Controls.Add(status, 0, 1);
        top.Controls.Add(headerText, 1, 0);

        _grid.Columns.Add("name", "Freigegebener Drucker");
        _grid.Columns.Add("queue", "Windows-Warteschlange");
        _grid.Columns.Add("driver", "Treiber");
        _grid.Columns.Add("status", "Status");

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(8) };
        foreach (var b in MakeButtons()) buttons.Controls.Add(b);
        Controls.Add(_grid); Controls.Add(top); Controls.Add(buttons);
        Shown += async (_, _) => await RefreshAllAsync();
    }

    private IEnumerable<Button> MakeButtons()
    {
        Button B(string text, EventHandler click) { var b = new Button { Text = text, AutoSize = true, Height = 32 }; b.Click += click; return b; }
        return [
            B("Drucker hinzufügen", async (_,_) => await AddPrinterAsync()),
            B("Entfernen", (_,_) => RemovePrinter()),
            B("Testseite", (_,_) => TestPage()),
            B("Aktualisieren", async (_,_) => await RefreshAllAsync()),
            B("Firewall reparieren", async (_,_) => await RepairFirewallAsync()),
            B("Diagnosepaket", async (_,_) => await CreateDiagnosticsAsync()),
            B("Über", (_,_) => ShowAbout())
        ];
    }

    private async Task RefreshAllAsync()
    {
        _config = JsonStore.LoadOrCreate(AppPaths.ServerConfig, () => new ServerConfig());
        List<LocalPrinterInfo> local;
        try { local = await WinPrinterHelper.GetPrintersAsync(); }
        catch { local = []; }
        _grid.Rows.Clear();
        foreach (var p in _config.Printers)
        {
            var lp = local.FirstOrDefault(x => x.Name.Equals(p.QueueName, StringComparison.OrdinalIgnoreCase));
            var i = _grid.Rows.Add(p.DisplayName, p.QueueName, p.DriverName, lp?.PrinterStatus ?? "Nicht gefunden");
            _grid.Rows[i].Tag = p.Id;
        }
        var svc = await PowerShellRunner.RunAsync("(Get-Service -Name SimplePrintServer -ErrorAction SilentlyContinue).Status");
        _service.Text = string.IsNullOrWhiteSpace(svc.StdOut) ? "nicht installiert" : svc.StdOut.Trim();
        var net = await PowerShellRunner.RunAsync("if(Get-NetTCPConnection -LocalPort 45881 -State Listen -ErrorAction SilentlyContinue){'bereit'}else{'nicht bereit'}");
        _network.Text = net.StdOut.Trim();
    }

    private async Task AddPrinterAsync()
    {
        try
        {
            var printers = await WinPrinterHelper.GetPrintersAsync();
            if (printers.Count == 0) { MessageBox.Show("Keine lokalen Windows-Drucker gefunden."); return; }
            using var dlg = new Form { Text = "Drucker hinzufügen", Width = 650, Height = 180, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
            var combo = new ComboBox { Left = 20, Top = 25, Width = 590, DropDownStyle = ComboBoxStyle.DropDownList };
            combo.Items.AddRange(printers.Select(p => $"{p.Name}  [{p.DriverName}]").Cast<object>().ToArray()); combo.SelectedIndex = 0;
            var ok = new Button { Text = "Hinzufügen", Left = 420, Top = 75, Width = 90, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Abbrechen", Left = 520, Top = 75, Width = 90, DialogResult = DialogResult.Cancel };
            dlg.Controls.AddRange([combo, ok, cancel]); dlg.AcceptButton = ok; dlg.CancelButton = cancel;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var selected = printers[combo.SelectedIndex];
            if (_config.Printers.Any(p => p.QueueName.Equals(selected.Name, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show("Dieser Drucker ist bereits freigegeben."); return; }
            _config.Printers.Add(new SharedPrinterConfig { QueueName = selected.Name, DisplayName = selected.Name, DriverName = selected.DriverName });
            JsonStore.Save(AppPaths.ServerConfig, _config);
            await RefreshAllAsync();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void RemovePrinter()
    {
        if (_grid.SelectedRows.Count == 0) return;
        var id = (Guid)_grid.SelectedRows[0].Tag;
        var p = _config.Printers.First(x => x.Id == id);
        if (MessageBox.Show($"'{p.DisplayName}' aus SimplePrint entfernen? Der lokale Windows-Drucker bleibt erhalten.", "Entfernen", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _config.Printers.RemoveAll(x => x.Id == id); JsonStore.Save(AppPaths.ServerConfig, _config); _ = RefreshAllAsync();
    }

    private void TestPage()
    {
        if (_grid.SelectedRows.Count == 0) return;
        var id = (Guid)_grid.SelectedRows[0].Tag;
        WinPrinterHelper.PrintTestPage(_config.Printers.First(x => x.Id == id).QueueName);
    }

    private async Task RepairFirewallAsync()
    {
        try { await WinPrinterHelper.RepairFirewallAsync(); MessageBox.Show("Firewallregeln wurden neu erstellt."); await RefreshAllAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Firewall", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task CreateDiagnosticsAsync()
    {
        using var save = new SaveFileDialog { Filter = "ZIP-Datei|*.zip", FileName = $"SimplePrint-Server-Diagnose-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var zip = ZipFile.Open(save.FileName, ZipArchiveMode.Create);
            AddFile(zip, AppPaths.ServerConfig, "config.json");
            AddFile(zip, AppPaths.ServerLog, "server.log");
            var e = zip.CreateEntry("diagnostics.txt"); using var w = new StreamWriter(e.Open()); await w.WriteAsync(await WinPrinterHelper.GetDiagnosticsAsync());
            MessageBox.Show("Diagnosepaket wurde erstellt.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Diagnose", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ShowAbout()
    {
        using var dlg = new AboutForm();
        dlg.ShowDialog(this);
    }

    private static void AddFile(ZipArchive zip, string path, string name)
    {
        if (File.Exists(path)) zip.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
    }
}
