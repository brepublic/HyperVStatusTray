namespace HyperVStatusTray.Services;

public static class AppPaths
{
    public const string ServiceName = "HyperVStatusTrayBroker";

    public const string ServiceAccountName = @"NT SERVICE\HyperVStatusTrayBroker";

    public static string InstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "HyperVStatusTray");

    public static string UserDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HyperVStatusTray");

    public static string MachineDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HyperVStatusTray");

    public static string ConfigPath => Path.Combine(MachineDataDirectory, "config.json");

    public static string BrokerSecurityPath => Path.Combine(MachineDataDirectory, "broker-security.json");
}
