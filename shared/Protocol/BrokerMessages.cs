namespace HyperVStatusTray.Protocol;

public enum BrokerCommand
{
    Ping,
    GetSnapshot,
    ExecuteVmAction,
    ReloadConfig,
    SetVmStartupPolicy
}

public enum VmAction
{
    Start,
    ShutDownGuest,
    TurnOff,
    RestartGuest,
    Reset
}

public sealed record BrokerRequest
{
    public Guid RequestId { get; init; } = Guid.NewGuid();

    public BrokerCommand Command { get; init; }

    public int? VmIndex { get; init; }

    public VmAction? Action { get; init; }

    public VmStartupPolicy? StartupPolicy { get; init; }

    public int? AutomaticStartDelaySeconds { get; init; }
}

public sealed record BrokerResponse
{
    public Guid RequestId { get; init; }

    public bool Success { get; init; }

    public string? Error { get; init; }

    public string? Message { get; init; }

    public BrokerSnapshot? Snapshot { get; init; }

    public static BrokerResponse Ok(Guid requestId, string? message = null, BrokerSnapshot? snapshot = null) => new()
    {
        RequestId = requestId,
        Success = true,
        Message = message,
        Snapshot = snapshot
    };

    public static BrokerResponse Fail(Guid requestId, string error) => new()
    {
        RequestId = requestId,
        Success = false,
        Error = error
    };
}

public sealed record BrokerSnapshot
{
    public required AppConfig Config { get; init; }

    public required VmObservation[] Observations { get; init; }

    public required DateTimeOffset ObservedAtUtc { get; init; }
}
