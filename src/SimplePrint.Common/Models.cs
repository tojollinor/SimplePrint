using System.Net;

namespace SimplePrint.Common;

public sealed class ServerConfig
{
    public Guid ServerId { get; set; } = Guid.NewGuid();
    public string ServerName { get; set; } = Environment.MachineName;
    public int DiscoveryPort { get; set; } = Protocol.DefaultDiscoveryPort;
    public int GatewayPort { get; set; } = Protocol.DefaultGatewayPort;
    public List<SharedPrinterConfig> Printers { get; set; } = [];
}

public sealed class SharedPrinterConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string QueueName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string DriverName { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class ClientConfig
{
    public Guid ClientId { get; set; } = Guid.NewGuid();
    public int DiscoveryPort { get; set; } = Protocol.DefaultDiscoveryPort;
    public int LocalPortStart { get; set; } = 19100;
    public int LocalPortEnd { get; set; } = 19999;
    public Guid? PreferredServerId { get; set; }
    public string ManualServer { get; set; } = "";
    public List<ClientPrinterMapping> Mappings { get; set; } = [];
}

public sealed class ClientPrinterMapping
{
    public Guid ServerId { get; set; }
    public Guid PrinterId { get; set; }
    public string ServerName { get; set; } = "";
    public string PrinterDisplayName { get; set; } = "";
    public string LocalPrinterName { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string PortName { get; set; } = "";
    public int LocalProxyPort { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class DiscoveryAnnouncement
{
    public string Magic { get; set; } = Protocol.DiscoveryResponseMagic;
    public int Version { get; set; } = Protocol.Version;
    public Guid ServerId { get; set; }
    public string ServerName { get; set; } = "";
    public int GatewayPort { get; set; }
    public List<DiscoveredPrinter> Printers { get; set; } = [];
}

public sealed class DiscoveredPrinter
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string Status { get; set; } = "Unknown";
}

public sealed record DiscoveredServer(DiscoveryAnnouncement Announcement, IPAddress Address, DateTimeOffset SeenAt);

public sealed class ClientHeartbeat
{
    public string Magic { get; set; } = Protocol.ClientHeartbeatMagic;
    public int Version { get; set; } = Protocol.Version;
    public Guid ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public Guid? PreferredServerId { get; set; }
    public int InstalledPrinterCount { get; set; }
}

public sealed class ClientPresence
{
    public Guid ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string Address { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public Guid? PreferredServerId { get; set; }
    public int InstalledPrinterCount { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

public sealed class LocalPrinterInfo
{
    public string Name { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string PortName { get; set; } = "";
    public string PrinterStatus { get; set; } = "";
}


public sealed class PrintJobRecord
{
    public Guid JobId { get; set; }
    public Guid ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public Guid ServerId { get; set; }
    public string ServerName { get; set; } = "";
    public Guid PrinterId { get; set; }
    public string PrinterName { get; set; } = "";
    public string LocalPrinterName { get; set; } = "";
    public string Status { get; set; } = "Neu";
    public string Message { get; set; } = "";
    public long Bytes { get; set; }
    public uint? SpoolerJobId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class PrintJobAck
{
    public Guid JobId { get; set; }
    public bool Success { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public long Bytes { get; set; }
    public uint? SpoolerJobId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
