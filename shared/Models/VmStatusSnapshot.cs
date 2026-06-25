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

    public string IndicatorText => FormatIndicatorText(AppText.DefaultLanguage);

    public string FormatIndicatorText(AppLanguage language) => Indicator switch
    {
        IndicatorState.Off => AppText.Get(language, TextId.IndicatorOff),
        IndicatorState.Starting => AppText.Get(language, TextId.IndicatorStarting),
        IndicatorState.Ready => AppText.Get(language, TextId.IndicatorReady),
        IndicatorState.Fault => AppText.Get(language, TextId.IndicatorFault),
        IndicatorState.Unknown => AppText.Get(language, TextId.IndicatorUnknown),
        _ => AppText.Get(language, TextId.Unknown)
    };
}
