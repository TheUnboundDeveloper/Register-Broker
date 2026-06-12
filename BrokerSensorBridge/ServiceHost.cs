using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| ServiceHost                                                                |
|                                                                            |
|   Hosts the broker (or the write-only RGB control service) under the       |
|   Windows Service Control Manager. The actual work lives in                |
|   Program.RunBrokerAsync / Program.RunControlServiceAsync — this only       |
|   wires them into the SCM lifecycle so they get clean start/stop and a      |
|   bounded graceful-shutdown window.                                         |
|                                                                            |
|   On a stop request the host cancels the BackgroundService's stoppingToken; |
|   the broker body returns and disposes the kernel-driver handle in its      |
|   finally, which releases \\.\BrokerSmbus so the BrokerSmbus kernel       |
|   service can unload. The installer makes the broker service depend on the  |
|   driver service, so SCM start order is driver -> broker.                   |
|                                                                            |
|   AddWindowsService() degrades gracefully: when the process is NOT launched |
|   by the SCM it uses the console lifetime, so these entry points are also    |
|   safe to run interactively (though Program routes dev runs to the plain    |
|   console path).                                                            |
\*---------------------------------------------------------------------------*/
internal static class ServiceHost
{
    internal const string SensorServiceName  = "SensorBroker";
    internal const string ControlServiceName = "BrokerControl";

    public static Task<int> RunSensorBrokerAsync(string[] args)
        => RunHostedAsync(args, SensorServiceName, Program.RunBrokerAsync);

    public static Task<int> RunControlServiceAsync(string[] args)
        => RunHostedAsync(args, ControlServiceName, Program.RunControlServiceAsync);

    private static async Task<int> RunHostedAsync(
        string[] args, string serviceName, Func<string[], CancellationToken, Task<int>> body)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(o => o.ServiceName = serviceName);

        /* Give the broker time to drain the control loop and release the driver handle
           before the SCM kills the process. The kernel service can only unload once that
           handle is closed, so a clean window matters for stop/uninstall. */
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));

        builder.Services.AddHostedService(sp =>
            new BrokerWorker(sp.GetRequiredService<IHostApplicationLifetime>(), token => body(args, token)));

        await builder.Build().RunAsync();
        return 0;
    }

    /*-----------------------------------------------------------*\
    | Adapts a "run until cancelled" broker body to a hosted      |
    | service. If the body returns on its own (e.g. a fatal in     |
    | the control loop) we stop the whole host so the SCM reports  |
    | the service stopped rather than wedged.                      |
    \*-----------------------------------------------------------*/
    private sealed class BrokerWorker : BackgroundService
    {
        private readonly IHostApplicationLifetime _life;
        private readonly Func<CancellationToken, Task<int>> _run;

        public BrokerWorker(IHostApplicationLifetime life, Func<CancellationToken, Task<int>> run)
        {
            _life = life;
            _run = run;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await _run(stoppingToken);
            }
            finally
            {
                /* Whether the body finished by cancellation or by returning early,
                   make sure the host comes down with it. */
                if (!stoppingToken.IsCancellationRequested)
                    _life.StopApplication();
            }
        }
    }
}
