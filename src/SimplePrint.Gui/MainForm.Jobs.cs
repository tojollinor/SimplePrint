using SimplePrint.Common;

namespace SimplePrint.Gui;

public sealed partial class MainForm
{
    private readonly DataGridView _jobs = Grid();
    private DateTime _jobsWriteUtc = DateTime.MinValue;

    private TabPage CreateJobsTab()
    {
        var tab = new TabPage("Druckaufträge");

        _jobs.Columns.Add("time", "Zeit");
        _jobs.Columns.Add("direction", "Richtung");
        _jobs.Columns.Add("peer", "Gegenstelle");
        _jobs.Columns.Add("printer", "Drucker");
        _jobs.Columns.Add("status", "Status");
        _jobs.Columns.Add("bytes", "Bytes");
        _jobs.Columns.Add("pdf", "PDF");
        _jobs.Columns.Add("message", "Meldung");
        _jobs.ContextMenuStrip = BuildJobsMenu();
        _jobs.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right)
                return;

            var hit = _jobs.HitTest(e.X, e.Y);
            if (hit.RowIndex >= 0)
            {
                _jobs.ClearSelection();
                _jobs.Rows[hit.RowIndex].Selected = true;
            }
        };

        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(10),
            ForeColor = UiColors.Muted,
            Text =
                "Ein- und ausgehende SimplePrint-Druckaufträge werden gemeinsam dargestellt."
        };

        var buttons = BottomButtons();
        buttons.Controls.Add(MakeButton("Aktualisieren", (_, _) => RefreshJobsGrid()));
        buttons.Controls.Add(MakeButton(
            "Abgeschlossene löschen",
            (_, _) => ClearCompletedJobs()));
        buttons.Controls.Add(MakeButton(
            "Alle löschen",
            (_, _) => ClearAllJobs()));

        tab.Controls.Add(_jobs);
        tab.Controls.Add(info);
        tab.Controls.Add(buttons);
        return tab;
    }

    private ContextMenuStrip BuildJobsMenu()
    {
        var menu = new ContextMenuStrip();

        var openPdf = new ToolStripMenuItem(
            "PDF öffnen",
            null,
            async (_, _) =>
            {
                if (_jobs.SelectedRows.Count > 0 &&
                    _jobs.SelectedRows[0].Tag is PrintJobRecord job &&
                    JobHasPdf(job))
                {
                    await OpenJobPdfAsync(job);
                }
            });

        menu.Items.Add(openPdf);

        menu.Opening += (_, e) =>
        {
            var hasPdf =
                _jobs.SelectedRows.Count > 0 &&
                _jobs.SelectedRows[0].Tag is PrintJobRecord job &&
                JobHasPdf(job);

            if (!hasPdf)
                e.Cancel = true;
        };

        return menu;
    }

    private void ClearCompletedJobs()
    {
        var jobs = JsonStore.LoadOrCreate(
            AppPaths.DeviceJobs,
            () => new List<PrintJobRecord>());

        var completed = new HashSet<string>(
            ["Gedruckt", "Abgeschlossen", "Ignoriert", "Fehler"],
            StringComparer.OrdinalIgnoreCase);

        var count = jobs.RemoveAll(x => completed.Contains(x.Status));
        JsonStore.Save(AppPaths.DeviceJobs, jobs);
        RefreshJobsGrid();
        SetStatus($"✓ {count} abgeschlossene Druckaufträge gelöscht");
    }

    private void ClearAllJobs()
    {
        if (MessageBox.Show(
                "Die gesamte gespeicherte Druckauftragshistorie dieses Geräts löschen?",
                "Druckaufträge löschen",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        JsonStore.Save(AppPaths.DeviceJobs, new List<PrintJobRecord>());
        RefreshJobsGrid();
        SetStatus("✓ Druckauftragshistorie gelöscht");
    }

    private void RefreshJobsGrid(bool force = true)
    {
        var write = GetWriteUtc(AppPaths.DeviceJobs);

        // Die Tabelle wird nur neu aufgebaut, wenn sich jobs.json geändert hat.
        if (!force && write == _jobsWriteUtc)
            return;

        List<PrintJobRecord> jobs;
        try
        {
            jobs = JsonStore.LoadOrCreate(
                AppPaths.DeviceJobs,
                () => new List<PrintJobRecord>());
        }
        catch
        {
            jobs = [];
        }

        _jobsWriteUtc = write;

        var selectedIndex = _jobs.SelectedRows.Count > 0
            ? _jobs.SelectedRows[0].Index
            : -1;

        var firstVisible = -1;
        try
        {
            firstVisible = _jobs.FirstDisplayedScrollingRowIndex;
        }
        catch
        {
        }

        _jobs.SuspendLayout();
        try
        {
            _jobs.Rows.Clear();

            foreach (var job in jobs
                         .OrderByDescending(x => x.UpdatedAt)
                         .Take(200))
            {
                var incoming = job.ServerId == _config.DeviceId;
                var peer = incoming ? job.ClientName : job.ServerName;

                var index = _jobs.Rows.Add(
                    job.UpdatedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss"),
                    incoming ? "Eingehend" : "Ausgehend",
                    peer,
                    job.PrinterName,
                    job.Status,
                    job.Bytes.ToString("N0"),
                    JobHasPdf(job) ? "PDF" : "",
                    job.Message);

                _jobs.Rows[index].Tag = job;

                var statusCell = _jobs.Rows[index].Cells["status"];

                if (job.Status is "Gedruckt" or "Abgeschlossen")
                    statusCell.Style.ForeColor = UiColors.Ok;
                else if (job.Status == "Fehler")
                    statusCell.Style.ForeColor = UiColors.Error;
            }

            if (selectedIndex >= 0 && selectedIndex < _jobs.Rows.Count)
                _jobs.Rows[selectedIndex].Selected = true;

            if (firstVisible >= 0 && firstVisible < _jobs.Rows.Count)
            {
                try
                {
                    _jobs.FirstDisplayedScrollingRowIndex = firstVisible;
                }
                catch
                {
                }
            }
        }
        finally
        {
            _jobs.ResumeLayout();
        }
    }
}
