using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimplePrint.Common;

namespace SimplePrint.Service;

internal static class Program
{
    public static void Main(string[] args)
    {
        AppPaths.Ensure();

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options =>
            options.ServiceName = "SimplePrint");
        builder.Services.AddHostedService<DeviceWorker>();

        builder.Build().Run();
    }
}
