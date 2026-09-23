using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimplePrint.Common;

namespace SimplePrint.Server.Service;

internal static class Program
{
    public static void Main(string[] args)
    {
        AppPaths.Ensure();
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(o => o.ServiceName = "SimplePrint Server");
        builder.Services.AddHostedService<ServerWorker>();
        builder.Build().Run();
    }
}
