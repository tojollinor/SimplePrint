using System.Diagnostics;
using SimplePrint.Common;

namespace SimplePrint.Gui;

/// <summary>
/// PDF-Druckweg in der Oberfläche: Haken pro Drucker (Rechtsklick), Aufbewahrungsdauer
/// in den Einstellungen und das Öffnen gespeicherter PDFs aus der Auftragsliste.
/// Die Menüs werden beim ersten Anzeigen des Fensters an die bestehenden Reiter angehängt.
/// </summary>
public sealed partial class MainForm
{
    private const string PdfDriverName = "Microsoft Print To PDF";
    private const string PdfNodeSuffix = "   [PDF-Modus]";

    private bool _pdfUiAttached;
    private ToolStripMenuItem? _pdfModeItem;
    private NumericUpDown? _pdfRetentionBox;
    private bool _pdfRetentionLoading;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        AttachPdfUi();
    }

    private void AttachPdfUi()
    {
        if (_pdfUiAttached)
            return;

        _pdfUiAttached = true;

        try
        {
            AttachPdfModeMenu();
            AttachPdfRetentionSetting();
            _liveTimer.Tick += (_, _) => DecoratePdfNodes();
            DecoratePdfNodes();
        }
        catch (Exception ex)
        {
            SetStatus("PDF-Funktionen konnten nicht eingebunden werden: " + ex.Message);
        }
    }

    // ---------- Haken "Als PDF-Drucker nutzen" ----------

    private void AttachPdfModeMenu()
    {
        var menu = _printerTree.ContextMenuStrip;
        if (menu is null)
            return;

        _pdfModeItem = new ToolStripMenuItem("Als PDF-Drucker nutzen")
        {
            Visible = false
        };

        _pdfModeItem.Click += async (_, _) => await TogglePdfModeAsync();

        menu.Items.Add(_pdfModeItem);

        menu.Opening += (_, _) =>
        {
            var mapping = GetPdfCandidateMapping();
            _pdfModeItem.Visible = mapping is not null;
            _pdfModeItem.Checked = mapping?.UsePdf ?? false;
        };
    }

    // Nur Tunnel-Drucker, die SimplePrint selbst angelegt hat, können auf PDF umgestellt
    // werden. Direkte und über die Windows-Freigabe verbundene Drucker laufen nicht über
    // den Tunnel.
    private NetworkPrinterMapping? GetPdfCandidateMapping()
    {
        if (_printerTree.SelectedNode?.Tag is not NetworkPrinterTag tag)
            return null;

        return _config.NetworkPrinters.FirstOrDefault(x =>
            x.Enabled &&
            !x.UseExistingQueue &&
            x.SourceDeviceId == tag.Device.DeviceId &&
            x.PrinterId == tag.Printer.Id &&
            string.Equals(
                x.TransportMode,
                PrinterTransport.Tunnel,
                StringComparison.OrdinalIgnoreCase));
    }

    private async Task TogglePdfModeAsync()
    {
        var mapping = GetPdfCandidateMapping();
        if (mapping is null)
            return;

        var enable = !mapping.UsePdf;

        if (enable)
        {
            var answer = MessageBox.Show(
                $"'{mapping.PrinterDisplayName}' wird auf den Windows-Treiber \"{PdfDriverName}\" " +
                "umgestellt. Dokumente werden dann als PDF an den Server geschickt und dort mit " +
                "dem Treiber und den Druckeinstellungen des Servers gedruckt.\r\n\r\n" +
                "Der Server speichert die PDF für die in den Einstellungen festgelegte Zeit.\r\n\r\n" +
                "Dafür ist eine Administratorfreigabe nötig. Fortfahren?",
                "Als PDF-Drucker nutzen",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes)
                return;
        }

        var driver = enable ? PdfDriverName : mapping.DriverName;

        var script = $@"
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$driver={PowerShellRunner.Quote(driver)}

if(-not (Get-Printer -Name $printer -ErrorAction SilentlyContinue)) {{
  throw ('Die Windows-Druckerqueue ' + $printer + ' wurde nicht gefunden.')
}}

if(-not (Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue)) {{
  Add-PrinterDriver -Name $driver -ErrorAction SilentlyContinue
}}

if(-not (Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue)) {{
  throw ('Der Druckertreiber ' + $driver + ' ist auf diesem PC nicht installiert. ' +
         'Unter Windows-Features muss Microsoft Print to PDF aktiviert sein.')
}}

Set-Printer -Name $printer -DriverName $driver -ErrorAction Stop
";

        try
        {
            SetBusy(enable
                ? $"'{mapping.PrinterDisplayName}' wird auf PDF umgestellt …"
                : $"'{mapping.PrinterDisplayName}' wird zurückgestellt …");

            await PrivilegeHelper.RunPowerShellElevatedAsync(script);

            mapping.UsePdf = enable;
            SaveConfig();
            DecoratePdfNodes();

            SetStatus(enable
                ? $"✓ '{mapping.PrinterDisplayName}' druckt jetzt als PDF"
                : $"✓ '{mapping.PrinterDisplayName}' ist wieder im normalen Modus");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Abgebrochen");
        }
        catch (Exception ex)
        {
            SetStatus("Umstellung fehlgeschlagen");
            MessageBox.Show(
                ex.Message,
                "Als PDF-Drucker nutzen",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    // Der Baum wird bei Änderungen komplett neu aufgebaut. Der Hinweis "[PDF-Modus]"
    // wird deshalb regelmäßig wieder angehängt (mehrfaches Anhängen wird verhindert).
    private void DecoratePdfNodes()
    {
        foreach (TreeNode device in _otherRoot.Nodes)
        {
            foreach (TreeNode child in device.Nodes)
            {
                if (child.Tag is not NetworkPrinterTag tag)
                    continue;

                var isPdf = _config.NetworkPrinters.Any(x =>
                    x.Enabled &&
                    x.UsePdf &&
                    x.SourceDeviceId == tag.Device.DeviceId &&
                    x.PrinterId == tag.Printer.Id);

                var marked = child.Text.EndsWith(PdfNodeSuffix, StringComparison.Ordinal);

                if (isPdf && !marked)
                    child.Text += PdfNodeSuffix;
                else if (!isPdf && marked)
                    child.Text = child.Text[..^PdfNodeSuffix.Length];
            }
        }
    }

    // ---------- Aufbewahrungsdauer ----------

    private void AttachPdfRetentionSetting()
    {
        var settingsTab = _tabs?.TabPages
            .Cast<TabPage>()
            .FirstOrDefault(x => x.Text == "Einstellungen");

        var panel = settingsTab?.Controls
            .OfType<FlowLayoutPanel>()
            .FirstOrDefault();

        if (panel is null)
            return;

        _pdfRetentionBox = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 3650,
            Width = 80,
            Margin = new Padding(3, 3, 3, 3)
        };

        _pdfRetentionLoading = true;
        _pdfRetentionBox.Value = Math.Clamp(_config.PdfRetentionDays, 0, 3650);
        _pdfRetentionLoading = false;

        _pdfRetentionBox.ValueChanged += (_, _) =>
        {
            if (_pdfRetentionLoading || _pdfRetentionBox is null)
                return;

            _config.PdfRetentionDays = (int)_pdfRetentionBox.Value;
            SaveConfig();
            SetStatus(_config.PdfRetentionDays == 0
                ? "✓ PDFs werden nach dem Druck sofort gelöscht"
                : $"✓ PDFs werden {_config.PdfRetentionDays} Tage aufbewahrt");
        };

        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 6)
        };

        row.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "PDFs aufbewahren für",
            Margin = new Padding(3, 6, 3, 3)
        });

        row.Controls.Add(_pdfRetentionBox);

        row.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Tage   (0 = nach dem Druck sofort löschen)",
            Margin = new Padding(3, 6, 3, 3)
        });

        var heading = SectionHeading("PDF-Druckaufträge");

        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            ForeColor = UiColors.Muted,
            Margin = new Padding(0, 0, 0, 6),
            Text =
                "Gilt für Aufträge, die dieses Gerät als Server per PDF erhält. Die Dateien liegen in " +
                AppPaths.DevicePdfs + ". Abgelaufene PDFs werden automatisch gelöscht."
        };

        var insertAt = panel.Controls.Count;
        for (var i = 0; i < panel.Controls.Count; i++)
        {
            if (panel.Controls[i] is Label label && label.Text == "Konfiguration")
            {
                insertAt = i;
                break;
            }
        }

        panel.Controls.Add(heading);
        panel.Controls.Add(hint);
        panel.Controls.Add(row);

        panel.Controls.SetChildIndex(heading, insertAt);
        panel.Controls.SetChildIndex(hint, insertAt + 1);
        panel.Controls.SetChildIndex(row, insertAt + 2);
    }

    // ---------- PDF öffnen ----------

    private bool JobHasPdf(PrintJobRecord job)
    {
        if (job.ServerId != _config.DeviceId)
            return false;

        try
        {
            return File.Exists(PdfStore.PathForJob(job.JobId));
        }
        catch
        {
            return false;
        }
    }

    // Die PDFs liegen in einem Ordner, dessen Dateien nur SYSTEM und Administratoren lesen
    // dürfen. Zum Öffnen wird deshalb nach einer Administratorfreigabe eine Kopie im
    // Temp-Ordner des Benutzers angelegt und diese angezeigt.
    private async Task OpenJobPdfAsync(PrintJobRecord job)
    {
        var source = PdfStore.PathForJob(job.JobId);
        var target = Path.Combine(
            Path.GetTempPath(),
            $"SimplePrint-{job.JobId:N}.pdf");

        var script = $@"
$source={PowerShellRunner.Quote(source)}
$target={PowerShellRunner.Quote(target)}

if(-not (Test-Path -LiteralPath $source)) {{
  throw 'Die PDF-Datei existiert nicht mehr. Sie wurde vermutlich nach Ablauf der Aufbewahrungszeit gelöscht.'
}}

Copy-Item -LiteralPath $source -Destination $target -Force
";

        try
        {
            SetBusy("PDF wird geöffnet …");
            await PrivilegeHelper.RunPowerShellElevatedAsync(script);

            if (!File.Exists(target))
                throw new FileNotFoundException("Die PDF konnte nicht bereitgestellt werden.");

            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            SetStatus("✓ PDF geöffnet");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Abgebrochen");
        }
        catch (Exception ex)
        {
            SetStatus("PDF konnte nicht geöffnet werden");
            MessageBox.Show(
                ex.Message,
                "PDF öffnen",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
