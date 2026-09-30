namespace SimplePrint.Common;

public static class UnifiedConfigStore
{
    public static SimplePrintConfig LoadOrMigrate()
    {
        AppPaths.Ensure();

        if (File.Exists(AppPaths.DeviceConfig))
            return Normalize(JsonStore.LoadOrCreate(AppPaths.DeviceConfig, () => new SimplePrintConfig()));

        var server = File.Exists(AppPaths.ServerConfig)
            ? JsonStore.LoadOrCreate(AppPaths.ServerConfig, () => new ServerConfig())
            : null;

        var client = File.Exists(AppPaths.ClientConfig)
            ? JsonStore.LoadOrCreate(AppPaths.ClientConfig, () => new ClientConfig())
            : null;

        var config = new SimplePrintConfig
        {
            DeviceId =
                server?.ServerId is Guid serverId && serverId != Guid.Empty
                    ? serverId
                    : client?.ClientId is Guid clientId && clientId != Guid.Empty
                        ? clientId
                        : Guid.NewGuid(),
            DeviceName =
                !string.IsNullOrWhiteSpace(server?.ServerName)
                    ? server!.ServerName
                    : Environment.MachineName,
            DiscoveryPort =
                server?.DiscoveryPort
                ?? client?.DiscoveryPort
                ?? Protocol.DefaultDiscoveryPort,
            GatewayPort = server?.GatewayPort ?? Protocol.DefaultGatewayPort,
            DiagnosticsPort = Protocol.DefaultDiagnosticsPort,
            LocalPortStart = client?.LocalPortStart ?? 19100,
            LocalPortEnd = client?.LocalPortEnd ?? 19999,
            SharedPrinters = server?.Printers
                .Select(CloneSharedPrinter)
                .ToList() ?? [],
            NetworkPrinters = client?.Mappings
                .Select(ConvertMapping)
                .ToList() ?? []
        };

        if (!string.IsNullOrWhiteSpace(client?.ManualServer))
            config.ManualPeers.Add(client.ManualServer.Trim());

        config = Normalize(config);
        JsonStore.Save(AppPaths.DeviceConfig, config);
        return config;
    }

    public static void Save(SimplePrintConfig config)
    {
        AppPaths.Ensure();
        JsonStore.Save(AppPaths.DeviceConfig, Normalize(config));
    }

    private static SimplePrintConfig Normalize(SimplePrintConfig config)
    {
        if (config.DeviceId == Guid.Empty)
            config.DeviceId = Guid.NewGuid();

        if (string.IsNullOrWhiteSpace(config.DeviceName))
            config.DeviceName = Environment.MachineName;

        config.DiscoveryPort = config.DiscoveryPort <= 0
            ? Protocol.DefaultDiscoveryPort
            : config.DiscoveryPort;

        config.GatewayPort = config.GatewayPort <= 0
            ? Protocol.DefaultGatewayPort
            : config.GatewayPort;

        config.DiagnosticsPort = config.DiagnosticsPort <= 0
            ? Protocol.DefaultDiagnosticsPort
            : config.DiagnosticsPort;

        if (config.LocalPortStart <= 0)
            config.LocalPortStart = 19100;

        if (config.LocalPortEnd < config.LocalPortStart)
            config.LocalPortEnd = Math.Max(config.LocalPortStart, 19999);

        config.ManualPeers = config.ManualPeers
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        config.SharedPrinters ??= [];
        config.NetworkPrinters ??= [];

        return config;
    }

    private static SharedPrinterConfig CloneSharedPrinter(SharedPrinterConfig source) =>
        new()
        {
            Id = source.Id == Guid.Empty ? Guid.NewGuid() : source.Id,
            QueueName = source.QueueName,
            DisplayName = source.DisplayName,
            DriverName = source.DriverName,
            PortName = source.PortName,
            TransportMode = source.TransportMode,
            DirectAddress = source.DirectAddress,
            DeviceUuid = source.DeviceUuid,
            Enabled = source.Enabled
        };

    private static NetworkPrinterMapping ConvertMapping(ClientPrinterMapping source) =>
        new()
        {
            SourceDeviceId = source.ServerId,
            PrinterId = source.PrinterId,
            SourceDeviceName = source.ServerName,
            PrinterDisplayName = source.PrinterDisplayName,
            LocalPrinterName = source.LocalPrinterName,
            DriverName = source.DriverName,
            PortName = source.PortName,
            LocalProxyPort = source.LocalProxyPort,
            TransportMode = source.TransportMode,
            DirectAddress = source.DirectAddress,
            DeviceUuid = source.DeviceUuid,
            UseExistingQueue = source.UseExistingQueue,
            Enabled = source.Enabled
        };
}
