using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SimplePrint.Server.Service;

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
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetJob(
        IntPtr hPrinter,
        uint jobId,
        uint level,
        IntPtr pJob,
        uint cbBuf,
        out uint pcbNeeded);

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

        if (!OpenPrinter(queueName, out var printer, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Drucker '{queueName}' konnte nicht geöffnet werden.");

        var docStarted = false;
        var pageStarted = false;
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

            void WriteChunk(byte[] data, int count)
            {
                var ptr = Marshal.AllocHGlobal(count);
                try
                {
                    Marshal.Copy(data, 0, ptr, count);

                    if (!WritePrinter(printer, ptr, count, out var written) || written != count)
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            $"WritePrinter fehlgeschlagen ({written}/{count} Byte).");

                    total += written;
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }

            WriteChunk(buffer, firstRead);

            while (true)
            {
                var read = await source.ReadAsync(buffer, ct);
                if (read == 0) break;

                WriteChunk(buffer, read);
            }

            return new RawPrintResult(total, spoolerJobId);
        }
        finally
        {
            if (pageStarted) EndPagePrinter(printer);
            if (docStarted) EndDocPrinter(printer);
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
