using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SimplePrint.Server.Service;

internal static class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DOC_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName = "SimplePrint";
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDatatype = "RAW";
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

    public static bool CanOpen(string queueName)
    {
        if (!OpenPrinter(queueName, out var h, IntPtr.Zero)) return false;
        ClosePrinter(h);
        return true;
    }

    public static async Task<long> SendStreamAsync(string queueName, Stream source, string documentName, CancellationToken ct)
    {
        if (!OpenPrinter(queueName, out var printer, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Drucker '{queueName}' konnte nicht geöffnet werden.");

        var docStarted = false;
        var pageStarted = false;
        try
        {
            var job = StartDocPrinter(printer, 1, new DOC_INFO_1 { pDocName = documentName, pDatatype = "RAW" });
            if (job == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "StartDocPrinter fehlgeschlagen.");
            docStarted = true;
            if (!StartPagePrinter(printer)) throw new Win32Exception(Marshal.GetLastWin32Error(), "StartPagePrinter fehlgeschlagen.");
            pageStarted = true;

            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, ct);
                if (read == 0) break;
                var ptr = Marshal.AllocHGlobal(read);
                try
                {
                    Marshal.Copy(buffer, 0, ptr, read);
                    if (!WritePrinter(printer, ptr, read, out var written) || written != read)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), $"WritePrinter fehlgeschlagen ({written}/{read} Byte).");
                    total += written;
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            return total;
        }
        finally
        {
            if (pageStarted) EndPagePrinter(printer);
            if (docStarted) EndDocPrinter(printer);
            ClosePrinter(printer);
        }
    }
}
