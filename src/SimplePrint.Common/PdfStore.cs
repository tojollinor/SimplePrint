using System.Security.AccessControl;
using System.Security.Principal;

namespace SimplePrint.Common;

/// <summary>
/// Ablage der PDF-Druckaufträge. Der Ordner ist nur für SYSTEM und Administratoren
/// zugänglich, weil die PDFs ungeschützt gespeichert werden.
/// </summary>
public static class PdfStore
{
    public const long MaxPdfBytes = 100L * 1024 * 1024;

    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private static readonly object Sync = new();
    private static DateTime _lastCleanupUtc = DateTime.MinValue;

    public static string EnsureFolder()
    {
        var dir = AppPaths.DevicePdfs;
        Directory.CreateDirectory(dir);

        try
        {
            Restrict(dir);
        }
        catch
        {
            // Fehlende Rechte dürfen den Druck nie verhindern.
        }

        return dir;
    }

    /// <summary>
    /// Dateiname der PDF zu einem Auftrag. Der Dokumentname endet auf die
    /// Job-ID ("SimplePrint Client 0123…"), daraus wird der Dateiname gebildet,
    /// damit die Oberfläche die PDF zu einem Eintrag der Auftragsliste findet.
    /// </summary>
    public static string FileNameFor(string documentName)
    {
        var token = (documentName ?? "").Trim().Split(' ').LastOrDefault() ?? "";
        var isJobId = token.Length == 32 && token.All(Uri.IsHexDigit);
        return (isJobId ? token.ToLowerInvariant() : Guid.NewGuid().ToString("N")) + ".pdf";
    }

    public static string PathForJob(Guid jobId) =>
        Path.Combine(AppPaths.DevicePdfs, jobId.ToString("N") + ".pdf");

    public static int GetRetentionDays()
    {
        try
        {
            var config = UnifiedConfigStore.LoadOrMigrate();
            return Math.Clamp(config.PdfRetentionDays, 0, 3650);
        }
        catch
        {
            return 30;
        }
    }

    /// <summary>
    /// Löscht abgelaufene PDFs, höchstens einmal pro Stunde.
    /// Bei 0 Tagen löscht der Druckweg die Datei selbst direkt nach dem Druck.
    /// </summary>
    public static void CleanupIfDue(int retentionDays)
    {
        if (retentionDays <= 0) return;

        lock (Sync)
        {
            if (DateTime.UtcNow - _lastCleanupUtc < CleanupInterval) return;
            _lastCleanupUtc = DateTime.UtcNow;
        }

        try
        {
            var dir = AppPaths.DevicePdfs;
            if (!Directory.Exists(dir)) return;

            var limit = DateTime.UtcNow - TimeSpan.FromDays(retentionDays);
            foreach (var file in Directory.EnumerateFiles(dir, "*.pdf"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < limit)
                        File.Delete(file);
                }
                catch
                {
                    // Datei ist gerade in Benutzung, beim nächsten Durchlauf erneut.
                }
            }
        }
        catch
        {
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void Restrict(string dir)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        foreach (var sid in new[]
                 {
                     WellKnownSidType.LocalSystemSid,
                     WellKnownSidType.BuiltinAdministratorsSid
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                inherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        new DirectoryInfo(dir).SetAccessControl(security);
    }
}
