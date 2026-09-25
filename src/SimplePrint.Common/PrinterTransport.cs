namespace SimplePrint.Common;

public static class PrinterTransport
{
    public const string Tunnel = "Tunnel";
    public const string Ipp = "Ipp";
    public const string Wsd = "Wsd";
    public const string WindowsShare = "WindowsShare";

    public static bool IsDirect(string? mode) =>
        string.Equals(mode, Ipp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, Wsd, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, WindowsShare, StringComparison.OrdinalIgnoreCase);

    public static bool IsDeviceDirect(string? mode) =>
        string.Equals(mode, Ipp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, Wsd, StringComparison.OrdinalIgnoreCase);

    public static string GetWindowsShareName(Guid printerId) =>
        $"SimplePrint_{printerId.ToString("N")[..8]}";

    public static string GetWindowsSharePath(string serverAddress, Guid printerId) =>
        $@"\\{serverAddress}\{GetWindowsShareName(printerId)}";

    public static bool IsMicrosoftIppClassDriver(string? driverName) =>
        !string.IsNullOrWhiteSpace(driverName) &&
        driverName.Contains("Microsoft IPP Class Driver", StringComparison.OrdinalIgnoreCase);

    public static bool IsClassDriver(string? driverName) =>
        !string.IsNullOrWhiteSpace(driverName) &&
        (driverName.Contains("Class Driver", StringComparison.OrdinalIgnoreCase) ||
         driverName.Contains("Type1 Class", StringComparison.OrdinalIgnoreCase) ||
         driverName.Contains("Type 1 Class", StringComparison.OrdinalIgnoreCase) ||
         driverName.Contains("Microsoft IPP", StringComparison.OrdinalIgnoreCase));

    public static bool IsSimplePrintPort(string? portName) =>
        !string.IsNullOrWhiteSpace(portName) &&
        (portName.StartsWith("SimplePrint_", StringComparison.OrdinalIgnoreCase) ||
         portName.StartsWith("SimplePrintDirect_", StringComparison.OrdinalIgnoreCase));

    public static bool IsLoopbackProxy(string? host, int? port) =>
        (string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) &&
        port is >= 19100 and <= 19999;
}
