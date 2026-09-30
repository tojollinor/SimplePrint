namespace SimplePrint.Common;

public static class DeviceRelationshipHelper
{
    public static IReadOnlySet<Guid> GetServerDeviceIds(SimplePrintConfig config) =>
        config.NetworkPrinters
            .Where(x => x.Enabled && x.SourceDeviceId != Guid.Empty)
            .Select(x => x.SourceDeviceId)
            .ToHashSet();

    public static IReadOnlyList<DiscoveredDevice> GetServers(
        SimplePrintConfig config,
        IEnumerable<DiscoveredDevice> activeDevices)
    {
        var ids = GetServerDeviceIds(config);
        return activeDevices
            .Where(x => ids.Contains(x.Announcement.DeviceId))
            .OrderBy(x => x.Announcement.DeviceName)
            .ToList();
    }

    public static IReadOnlyList<DiscoveredDevice> GetClients(
        Guid localDeviceId,
        IEnumerable<DiscoveredDevice> activeDevices) =>
        activeDevices
            .Where(x =>
                x.Announcement.Subscriptions.Any(s =>
                    s.SourceDeviceId == localDeviceId))
            .OrderBy(x => x.Announcement.DeviceName)
            .ToList();

    public static IReadOnlyList<PrinterSubscriptionAnnouncement> GetLocalSubscriptions(
        SimplePrintConfig config) =>
        config.NetworkPrinters
            .Where(x =>
                x.Enabled &&
                x.SourceDeviceId != Guid.Empty &&
                x.PrinterId != Guid.Empty)
            .Select(x => new PrinterSubscriptionAnnouncement
            {
                SourceDeviceId = x.SourceDeviceId,
                PrinterId = x.PrinterId
            })
            .DistinctBy(x => (x.SourceDeviceId, x.PrinterId))
            .ToList();
}
