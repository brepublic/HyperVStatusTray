namespace HyperVStatusTray;

public enum IndicatorState
{
    Off,
    Starting,
    Ready,
    Fault,
    Unknown
}

public sealed record VmStatusSnapshot
{
    public required VmConfig Config { get; init; }

    public required IndicatorState Indicator { get; init; }

    public required string Summary { get; init; }

    public required string Detail { get; init; }

    public VmObservation? Observation { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public string IndicatorText => Indicator switch
    {
        IndicatorState.Off => "已关闭",
        IndicatorState.Starting => "启动中/未就绪",
        IndicatorState.Ready => "已就绪",
        IndicatorState.Fault => "故障",
        IndicatorState.Unknown => "监控未知",
        _ => "未知"
    };
}
