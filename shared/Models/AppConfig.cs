namespace HyperVStatusTray;

public sealed class AppConfig
{
    public int PollIntervalSeconds { get; set; } = 5;

    public int StartupTimeoutSeconds { get; set; } = 180;

    public int SignalLossGraceSeconds { get; set; } = 20;

    public int MonitorFailureThreshold { get; set; } = 2;

    public List<VmConfig> VirtualMachines { get; set; } = [];

    public static AppConfig CreateDefault() => new();

    public void Validate()
    {
        if (PollIntervalSeconds is < 2 or > 60)
        {
            throw new InvalidDataException("PollIntervalSeconds 必须在 2 到 60 之间。");
        }

        if (StartupTimeoutSeconds is < 30 or > 3600)
        {
            throw new InvalidDataException("StartupTimeoutSeconds 必须在 30 到 3600 之间。");
        }

        if (SignalLossGraceSeconds is < 0 or > 600)
        {
            throw new InvalidDataException("SignalLossGraceSeconds 必须在 0 到 600 之间。");
        }

        if (MonitorFailureThreshold is < 1 or > 10)
        {
            throw new InvalidDataException("MonitorFailureThreshold 必须在 1 到 10 之间。");
        }

        if (VirtualMachines is null || VirtualMachines.Count is < 1 or > 2)
        {
            throw new InvalidDataException("VirtualMachines 必须包含一台或两台虚拟机；一台时托盘显示一个圆点，两台时第一台显示在上方、第二台显示在下方。");
        }

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (VmConfig vm in VirtualMachines)
        {
            vm.Validate();
            if (!names.Add(vm.Name))
            {
                throw new InvalidDataException("虚拟机不能使用重复的 Name。");
            }
        }
    }
}

public sealed class VmConfig
{
    public string Name { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public bool UseHeartbeat { get; set; } = true;

    public string? PingAddress { get; set; }

    public int PingTimeoutMilliseconds { get; set; } = 800;

    public void Validate()
    {
        Name = Name?.Trim() ?? string.Empty;
        Label = Label?.Trim() ?? string.Empty;
        PingAddress = string.IsNullOrWhiteSpace(PingAddress) ? null : PingAddress.Trim();

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidDataException("每台虚拟机都必须设置 Name。");
        }

        if (string.IsNullOrWhiteSpace(Label))
        {
            Label = Name;
        }

        if (PingTimeoutMilliseconds is < 100 or > 10000)
        {
            throw new InvalidDataException($"虚拟机 {Name} 的 PingTimeoutMilliseconds 必须在 100 到 10000 之间。");
        }

        if (!UseHeartbeat && string.IsNullOrWhiteSpace(PingAddress))
        {
            throw new InvalidDataException($"虚拟机 {Name} 已禁用 Heartbeat，因此必须配置 PingAddress。");
        }
    }
}
