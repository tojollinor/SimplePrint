using System.ComponentModel;
using System.Runtime.InteropServices;
using SimplePrint.Common;

namespace SimplePrint.Service;

internal sealed record RawPrintResult(long Bytes, uint SpoolerJobId);
internal sealed record RawJobSnapshot(bool Exists, uint Status, uint PagesPrinted, uint TotalPages, string StatusText);

internal static class RawPrinter
{
    private const uint JOB_STATUS_PAUSED = 0x00000001;
    private const uint JOB_STATUS_ERROR = 0x00000002;
    private const uint JOB_STATUS_DELETING = 0x00000004;
    private const uint JOB_STATUS_SPOOLING = 0x00000008;
    private const uint JOB_STATUS_PRINTING = 0x00000010;
    private const uint JOB_STATUS_OFFLINE = 0x00000020;
    private const uint JOB_STATUS_PAPEROUT = 0x00000040;
    private const uint JOB_STATUS_PRINTED = 0x00000080;
    private const uint JOB_STATUS_DELETED = 0x00000100;
    private const uint JOB_STATUS_BLOCKED_DEVQ = 0x00000200;
    private const uint JOB_STATUS_USER_INTERVENTION = 0x00000400;
    private const uint JOB_STATUS_RESTART = 0x00000800;
    private const uint JOB_STATUS_COMPLETE = 0x00001000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DOC_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName = "SimplePrint";
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDatatype = "RAW";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JOB_INFO_1
    {
        public uint JobId;
        public IntPtr pPrinterName;
        public IntPtr pMachineName;
        public IntPtr pUserName;
        public IntPtr pDocument;
        public IntPtr pDatatype;
        public IntPtr pStatus;
        public uint Status;
        public uint Priority;
        public uint Position;
        public uint TotalPages;
        public uint PagesPrinted;
        public SYSTEMTIME Submitted;
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint StartDocPrinter(IntPtr hPrinter, int level, [In] DOC_INFO_1 di);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool AbortPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetJob(
        IntPtr hPrinter,
        uint jobId,
        uint level,
        IntPtr pJob,
        uint cbBuf,
        out uint pcbNeeded);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumJobs(
        IntPtr hPrinter,
        uint firstJob,
        uint noJobs,
        uint level,
        IntPtr pJob,
        uint cbBuf,
        out uint pcbNeeded,
        out uint pcReturned);

    // Platzhalter-ID, wenn ein PDF-Auftrag so schnell gedruckt wurde, dass er
    // beim Nachschlagen schon nicht mehr in der Warteschlange stand.
    private const uint JobAlreadyFinished = uint.MaxValue;

    public static bool CanOpen(string queueName)
    {
        if (!OpenPrinter(queueName, out var h, IntPtr.Zero)) return false;
        ClosePrinter(h);
        return true;
    }

    public static async Task<RawPrintResult> SendStreamAsync(
        string queueName,
        Stream source,
        string documentName,
        CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var firstRead = await source.ReadAsync(buffer, ct);

        // Niemals für reine TCP-/Portmonitor-Prüfverbindungen einen
        // Windows-Spoolerauftrag anlegen.
        if (firstRead == 0)
            return new RawPrintResult(0, 0);

        // Die ersten Bytes entscheiden den Druckweg: Eine PDF (z. B. vom
        // "Microsoft Print to PDF"-Treiber des Clients) wird gespeichert und über
        // den Treiber dieses Servers gedruckt, alles andere geht unverändert als RAW.
        while (firstRead < PdfMagic.Length)
        {
            var more = await source.ReadAsync(buffer.AsMemory(firstRead), ct);
            if (more == 0) break;
            firstRead += more;
        }

        if (IsPdf(buffer, firstRead))
            return await SendPdfAsync(queueName, buffer, firstRead, source, documentName, ct);

        if (!OpenPrinter(queueName, out var printer, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Drucker '{queueName}' konnte nicht geöffnet werden.");

        var docStarted = false;
        var pageStarted = false;
        var completed = false;
        uint spoolerJobId = 0;

        try
        {
            spoolerJobId = StartDocPrinter(
                printer,
                1,
                new DOC_INFO_1 { pDocName = documentName, pDatatype = "RAW" });

            if (spoolerJobId == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "StartDocPrinter fehlgeschlagen.");

            docStarted = true;

            if (!StartPagePrinter(printer))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "StartPagePrinter fehlgeschlagen.");

            pageStarted = true;

            long total = 0;

            void WriteChunk(int count)
            {
                if (!WritePrinter(printer, buffer, count, out var written) || written != count)
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"WritePrinter fehlgeschlagen ({written}/{count} Byte).");

                total += written;
            }

            WriteChunk(firstRead);

            while (true)
            {
                var read = await source.ReadAsync(buffer, ct);
                if (read == 0) break;

                WriteChunk(read);
            }

            completed = true;
            return new RawPrintResult(total, spoolerJobId);
        }
        finally
        {
            if (completed)
            {
                if (pageStarted) EndPagePrinter(printer);
                if (docStarted) EndDocPrinter(printer);
            }
            else if (docStarted)
            {
                // Abgebrochene Übertragung: Auftrag verwerfen statt halbe Daten
                // (Zeichensalat) auszudrucken.
                AbortPrinter(printer);
            }

            ClosePrinter(printer);
        }
    }

    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

    private static bool IsPdf(byte[] buffer, int length) =>
        length >= PdfMagic.Length &&
        buffer.AsSpan(0, PdfMagic.Length).SequenceEqual(PdfMagic);

    private static async Task<RawPrintResult> SendPdfAsync(
        string queueName,
        byte[] first,
        int firstLength,
        Stream source,
        string documentName,
        CancellationToken ct)
    {
        var retentionDays = PdfStore.GetRetentionDays();
        var folder = PdfStore.EnsureFolder();
        var path = Path.Combine(folder, PdfStore.FileNameFor(documentName));

        PdfStore.CleanupIfDue(retentionDays);

        long total = 0;

        try
        {
            await using (var file = new FileStream(
                             path,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.Read,
                             64 * 1024,
                             useAsync: true))
            {
                await file.WriteAsync(first.AsMemory(0, firstLength), ct);
                total = firstLength;

                var chunk = new byte[64 * 1024];
                while (true)
                {
                    var read = await source.ReadAsync(chunk, ct);
                    if (read == 0) break;

                    total += read;
                    if (total > PdfStore.MaxPdfBytes)
                        throw new InvalidDataException(
                            "Die PDF-Datei ist größer als 100 MB und wird nicht gedruckt.");

                    await file.WriteAsync(chunk.AsMemory(0, read), ct);
                }
            }

            await PdfPrinter.PrintAsync(queueName, path, documentName, ct);
        }
        catch
        {
            // Unvollständige oder nicht druckbare Dateien nicht aufbewahren.
            PdfStore.TryDelete(path);
            throw;
        }

        var spoolerJobId = await FindJobIdAsync(queueName, documentName, ct);

        if (retentionDays == 0)
            PdfStore.TryDelete(path);

        return new RawPrintResult(total, spoolerJobId);
    }

    private static async Task<uint> FindJobIdAsync(
        string queueName,
        string documentName,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var id = FindJobIdByDocument(queueName, documentName);
            if (id != 0) return id;

            await Task.Delay(300, ct);
        }

        return JobAlreadyFinished;
    }

    private static uint FindJobIdByDocument(string queueName, string documentName)
    {
        if (!OpenPrinter(queueName, out var printer, IntPtr.Zero))
            return 0;

        try
        {
            _ = EnumJobs(printer, 0, 256, 1, IntPtr.Zero, 0, out var needed, out _);
            if (needed == 0) return 0;

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!EnumJobs(printer, 0, 256, 1, buffer, needed, out _, out var returned))
                    return 0;

                var itemSize = Marshal.SizeOf<JOB_INFO_1>();
                uint found = 0;

                for (var i = 0; i < returned; i++)
                {
                    var info = Marshal.PtrToStructure<JOB_INFO_1>(
                        IntPtr.Add(buffer, i * itemSize));

                    var document = info.pDocument == IntPtr.Zero
                        ? ""
                        : Marshal.PtrToStringUni(info.pDocument) ?? "";

                    if (string.Equals(document, documentName, StringComparison.Ordinal))
                        found = Math.Max(found, info.JobId);
                }

                return found;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ClosePrinter(printer);
        }
    }

    public static RawJobSnapshot GetJobSnapshot(string queueName, uint jobId)
    {
        if (!OpenPrinter(queueName, out var printer, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Drucker '{queueName}' konnte nicht geöffnet werden.");

        try
        {
            _ = GetJob(printer, jobId, 1, IntPtr.Zero, 0, out var needed);
            var firstError = Marshal.GetLastWin32Error();

            if (needed == 0)
            {
                if (firstError == 87)
                    return new RawJobSnapshot(false, 0, 0, 0, "Auftrag nicht mehr in der Warteschlange");

                throw new Win32Exception(firstError, "GetJob konnte die benötigte Puffergröße nicht ermitteln.");
            }

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetJob(printer, jobId, 1, buffer, needed, out _))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 87)
                        return new RawJobSnapshot(false, 0, 0, 0, "Auftrag nicht mehr in der Warteschlange");

                    throw new Win32Exception(error, "GetJob fehlgeschlagen.");
                }

                var info = Marshal.PtrToStructure<JOB_INFO_1>(buffer);
                var nativeText = info.pStatus == IntPtr.Zero
                    ? ""
                    : Marshal.PtrToStringUni(info.pStatus) ?? "";

                return new RawJobSnapshot(
                    true,
                    info.Status,
                    info.PagesPrinted,
                    info.TotalPages,
                    DescribeStatus(info.Status, nativeText));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ClosePrinter(printer);
        }
    }

    public static bool IsConfirmedPrinted(uint status) =>
        (status & (JOB_STATUS_PRINTED | JOB_STATUS_COMPLETE)) != 0;

    public static bool IsFailure(uint status) =>
        (status & (
            JOB_STATUS_ERROR |
            JOB_STATUS_OFFLINE |
            JOB_STATUS_PAPEROUT |
            JOB_STATUS_BLOCKED_DEVQ |
            JOB_STATUS_USER_INTERVENTION |
            JOB_STATUS_DELETED)) != 0;

    public static bool IsPrinting(uint status) =>
        (status & JOB_STATUS_PRINTING) != 0;

    public static bool IsSpooling(uint status) =>
        (status & JOB_STATUS_SPOOLING) != 0;

    private static string DescribeStatus(uint status, string nativeText)
    {
        if (!string.IsNullOrWhiteSpace(nativeText)) return nativeText;
        if ((status & JOB_STATUS_PRINTED) != 0) return "Windows-Spooler meldet: gedruckt";
        if ((status & JOB_STATUS_COMPLETE) != 0) return "Windows-Spooler meldet: abgeschlossen";
        if ((status & JOB_STATUS_PRINTING) != 0) return "Druckt";
        if ((status & JOB_STATUS_SPOOLING) != 0) return "Wird gespoolt";
        if ((status & JOB_STATUS_PAUSED) != 0) return "Pausiert";
        if ((status & JOB_STATUS_OFFLINE) != 0) return "Drucker offline";
        if ((status & JOB_STATUS_PAPEROUT) != 0) return "Papier fehlt";
        if ((status & JOB_STATUS_USER_INTERVENTION) != 0) return "Benutzereingriff erforderlich";
        if ((status & JOB_STATUS_BLOCKED_DEVQ) != 0) return "Druckerwarteschlange blockiert";
        if ((status & JOB_STATUS_ERROR) != 0) return "Druckfehler";
        if ((status & JOB_STATUS_DELETING) != 0) return "Wird gelöscht";
        if ((status & JOB_STATUS_RESTART) != 0) return "Wird neu gestartet";
        return "Im Windows-Spooler";
    }
}
