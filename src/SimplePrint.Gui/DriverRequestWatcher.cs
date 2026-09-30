using SimplePrint.Common;

namespace SimplePrint.Gui;

/// <summary>
/// Läuft in der SimplePrint-Oberfläche (auch im Infobereich) und fragt den Benutzer am
/// Server, ob ein Client einen Druckertreiber erhalten darf. Der Dienst hat keine
/// Oberfläche; er legt Anfragen als Dateien ab, die hier beantwortet werden.
/// </summary>
internal sealed class DriverRequestWatcher : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1500 };
    private readonly HashSet<Guid> _handled = [];
    private bool _showing;
    private int _ticks;

    public void Start()
    {
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private void Poll()
    {
        if (_showing)
            return;

        // Gelegentlich alte Reste entfernen (z. B. Entscheidungen zu abgebrochenen Anfragen).
        if (++_ticks % 400 == 0)
            DriverRequestStore.CleanupAll(onlyStale: true);

        DriverRequestRecord? next;

        try
        {
            next = DriverRequestStore.LoadPending()
                .Where(x => !_handled.Contains(x.Id))
                .OrderBy(x => x.CreatedAt)
                .FirstOrDefault();
        }
        catch
        {
            return;
        }

        if (next is null)
            return;

        _handled.Add(next.Id);
        _showing = true;

        try
        {
            Ask(next);
        }
        catch
        {
        }
        finally
        {
            _showing = false;
        }
    }

    private static void Ask(DriverRequestRecord request)
    {
        // Unsichtbares Fenster im Vordergrund als Besitzer, damit die Abfrage auch
        // erscheint, wenn SimplePrint nur im Infobereich läuft.
        using var owner = new Form
        {
            TopMost = true,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1)
        };

        owner.Show();
        owner.Activate();

        var printer = string.IsNullOrWhiteSpace(request.PrinterName)
            ? ""
            : $" für den Drucker '{request.PrinterName}'";

        var text =
            $"Der PC '{request.RequesterName}' ({request.RequesterAddress}) möchte den " +
            $"Druckertreiber '{request.DriverName}' ({DriverArchitecture.Describe(request.Architecture)})" +
            $"{printer} von diesem PC erhalten.\r\n\r\n" +
            "Das Treiberpaket wird einmalig an diesen PC gesendet. Bestätige nur, wenn du " +
            "diese Anfrage selbst gestartet hast.\r\n\r\n" +
            "Treiber bereitstellen?";

        var result = MessageBox.Show(
            owner,
            text,
            "SimplePrint – Treiberanfrage",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        // Hat sich die Anfrage inzwischen erledigt (Client abgebrochen, Frist abgelaufen),
        // gibt es nichts mehr zu entscheiden.
        if (!File.Exists(DriverRequestStore.RequestPath(request.Id)))
            return;

        DriverRequestStore.WriteDecision(request.Id, result == DialogResult.Yes);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
