namespace SimplePrint.Common;

public static class AppPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SimplePrint");
    public static string ServerRoot => Path.Combine(Root, "Server");
    public static string ClientRoot => Path.Combine(Root, "Client");
    public static string ServerConfig => Path.Combine(ServerRoot, "config.json");
    public static string ClientConfig => Path.Combine(ClientRoot, "config.json");
    public static string ServerLog => Path.Combine(ServerRoot, "server.log");
    public static string ServerClients => Path.Combine(ServerRoot, "clients.json");
    public static string ClientLog => Path.Combine(ClientRoot, "client.log");

    public static void Ensure()
    {
        Directory.CreateDirectory(ServerRoot);
        Directory.CreateDirectory(ClientRoot);
    }
}
