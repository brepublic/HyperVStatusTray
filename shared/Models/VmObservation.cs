namespace HyperVStatusTray;

public enum HeartbeatKind
{
    NotRequested,
    Ok,
    Degraded,
    NoContact,
    LostCommunication,
    ProtocolError,
    Paused,
    Missing,
    Unknown
}

public sealed record VmObservation
{
    public required DateTimeOffset ObservedAtUtc { get; init; }

    public bool MonitoringSucceeded { get; init; }

    public string? MonitoringError { get; init; }

    public bool Exists { get; init; }

    public ushort? EnabledState { get; init; }

    public ushort? HealthState { get; init; }

    public ushort[] OperationalStatus { get; init; } = [];

    public string[] StatusDescriptions { get; init; } = [];

    public ulong UptimeMilliseconds { get; init; }

    public HeartbeatKind Heartbeat { get; init; } = HeartbeatKind.NotRequested;

    public ushort? HeartbeatCode { get; init; }

    public string[] HeartbeatDescriptions { get; init; } = [];

    public bool? PingSucceeded { get; init; }

    public long? PingRoundtripMilliseconds { get; init; }

    public string? PingError { get; init; }

    public VmStartupPolicy StartupPolicy { get; init; } = VmStartupPolicy.Unknown;

    public int? AutomaticStartDelaySeconds { get; init; }

    public bool IsReadySignalPresent =>
        Heartbeat is HeartbeatKind.Ok or HeartbeatKind.Degraded || PingSucceeded == true;

    public bool IsCriticalState =>
        EnabledState is >= 32781 and <= 32792 ||
        HealthState is >= 20 ||
        OperationalStatus.Any(status => status is 6 or 7 or 14 or 16);

    public bool IsOffLike => EnabledState is 3 or 32769 or 32779;

    public bool IsRunning => EnabledState == 2;

    public bool IsStartingOrResuming => EnabledState is 10 or 32770 or 32777;

    public bool IsStoppingOrSaving => EnabledState is 4 or 32773 or 32774 or 32780;

    public bool IsPaused => EnabledState == 32768;

    public bool IsPowered =>
        IsRunning ||
        IsStartingOrResuming ||
        IsStoppingOrSaving ||
        IsPaused ||
        EnabledState is 32771 or 32776;

    public string HyperVStateText => EnabledState switch
    {
        null => "未知",
        0 => "未知",
        1 => "其他",
        2 => "Running",
        3 => "Off",
        4 => "Shutting down",
        6 => "Offline",
        7 => "Test",
        8 => "Deferred",
        9 => "Quiesced",
        10 => "Rebooting",
        32768 => "Paused",
        32769 => "Saved",
        32770 => "Starting",
        32771 => "Snapshotting",
        32773 => "Saving",
        32774 => "Stopping",
        32776 => "Pausing",
        32777 => "Resuming",
        32779 => "Fast saved",
        32780 => "Fast saving",
        >= 32781 and <= 32792 => "Critical",
        _ => $"State {EnabledState}"
    };

    public string HeartbeatText => Heartbeat switch
    {
        HeartbeatKind.NotRequested => "未查询",
        HeartbeatKind.Ok => "OK",
        HeartbeatKind.Degraded => "Degraded（仍可用）",
        HeartbeatKind.NoContact => "No contact",
        HeartbeatKind.LostCommunication => "Lost communication",
        HeartbeatKind.ProtocolError => "Protocol error",
        HeartbeatKind.Paused => "Paused",
        HeartbeatKind.Missing => "未发现组件",
        _ => HeartbeatCode is null ? "未知" : $"未知 ({HeartbeatCode})"
    };
}
