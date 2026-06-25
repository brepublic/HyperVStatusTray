namespace HyperVStatusTray;

public sealed class AppConfig
{
    public AppLanguage Language { get; set; } = AppText.DefaultLanguage;

    public int PollIntervalSeconds { get; set; } = 5;

    public int StartupTimeoutSeconds { get; set; } = 180;

    public int SignalLossGraceSeconds { get; set; } = 20;

    public int MonitorFailureThreshold { get; set; } = 2;

    public List<VmConfig> VirtualMachines { get; set; } = [];

    public static AppConfig CreateDefault() => new();

    public void Validate()
    {
        Language = AppText.Normalize(Language);
        string T(TextId id) => AppText.Get(Language, id);

        if (PollIntervalSeconds is < 2 or > 60)
        {
            throw new InvalidDataException(T(TextId.PollIntervalRange));
        }

        if (StartupTimeoutSeconds is < 30 or > 3600)
        {
            throw new InvalidDataException(T(TextId.StartupTimeoutRange));
        }

        if (SignalLossGraceSeconds is < 0 or > 600)
        {
            throw new InvalidDataException(T(TextId.SignalLossRange));
        }

        if (MonitorFailureThreshold is < 1 or > 10)
        {
            throw new InvalidDataException(T(TextId.MonitorFailureRange));
        }

        if (VirtualMachines is null || VirtualMachines.Count is < 1 or > 2)
        {
            throw new InvalidDataException(T(TextId.VmCountRange));
        }

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (VmConfig vm in VirtualMachines)
        {
            vm.Validate(Language);
            if (!names.Add(vm.Name))
            {
                throw new InvalidDataException(T(TextId.DuplicateVmName));
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

    public void Validate(AppLanguage language)
    {
        language = AppText.Normalize(language);
        string T(TextId id) => AppText.Get(language, id);
        string F(TextId id, params object?[] args) => AppText.Format(language, id, args);

        Name = Name?.Trim() ?? string.Empty;
        Label = Label?.Trim() ?? string.Empty;
        PingAddress = string.IsNullOrWhiteSpace(PingAddress) ? null : PingAddress.Trim();

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidDataException(T(TextId.VmNameRequired));
        }

        if (string.IsNullOrWhiteSpace(Label))
        {
            Label = Name;
        }

        if (PingTimeoutMilliseconds is < 100 or > 10000)
        {
            throw new InvalidDataException(F(TextId.PingTimeoutRange, Name));
        }

        if (!UseHeartbeat && string.IsNullOrWhiteSpace(PingAddress))
        {
            throw new InvalidDataException(F(TextId.PingAddressRequired, Name));
        }
    }
}
