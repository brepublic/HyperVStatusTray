using HyperVStatusTray.Broker;
using HyperVStatusTray.Services;

if (args.Any(arg => string.Equals(arg, "--console", StringComparison.OrdinalIgnoreCase)))
{
    Logger.Initialize(AppPaths.MachineDataDirectory, "HyperVStatusTrayBroker.log");
    using PipeBrokerServer server = new();
    server.Start();
    Console.WriteLine("HyperVStatusTrayBroker is running. Press Enter to stop.");
    Console.ReadLine();
    await server.StopAsync();
    return;
}

WindowsServiceHost.Run(AppPaths.ServiceName);
