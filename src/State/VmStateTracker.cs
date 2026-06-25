using System.Text;

namespace HyperVStatusTray.State;

internal sealed class VmStateTracker
{
    private readonly AppConfig _appConfig;
    private int _consecutiveMonitorFailures;
    private DateTimeOffset? _bootNotReadySinceUtc;
    private DateTimeOffset? _signalLostSinceUtc;
    private bool _startAttemptActive;
    private DateTimeOffset? _startRequestedAtUtc;
    private bool _observedPoweredDuringStart;
    private bool _restartAttemptActive;
    private bool _restartTransitionObserved;
    private bool _expectedStop;
    private bool _faultLatched;
    private string? _latchedFaultReason;

    public VmStateTracker(AppConfig appConfig, VmConfig vmConfig)
    {
        _appConfig = appConfig;
        Config = vmConfig;
        Current = new VmStatusSnapshot
        {
            Config = Config,
            Indicator = IndicatorState.Unknown,
            Summary = T(TextId.WaitingFirstQuery),
            Detail = T(TextId.NoStateRead),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public VmConfig Config { get; }

    public VmStatusSnapshot Current { get; private set; }

    private AppLanguage Language => _appConfig.Language;

    private string T(TextId id) => AppText.Get(Language, id);

    private string F(TextId id, params object?[] args) => AppText.Format(Language, id, args);

    public void MarkStartRequested()
    {
        _startAttemptActive = true;
        _startRequestedAtUtc = DateTimeOffset.UtcNow;
        _observedPoweredDuringStart = false;
        _restartAttemptActive = false;
        _restartTransitionObserved = false;
        _expectedStop = false;
        _faultLatched = false;
        _latchedFaultReason = null;
        _bootNotReadySinceUtc = DateTimeOffset.UtcNow;
        _signalLostSinceUtc = null;
        Current = CreateSnapshot(IndicatorState.Starting, T(TextId.StartRequestSent), T(TextId.WaitingHyperVReady), Current.Observation);
    }

    public void MarkRestartRequested()
    {
        _startAttemptActive = true;
        _startRequestedAtUtc = DateTimeOffset.UtcNow;
        _observedPoweredDuringStart = true;
        _restartAttemptActive = true;
        _restartTransitionObserved = false;
        _expectedStop = false;
        _faultLatched = false;
        _latchedFaultReason = null;
        _bootNotReadySinceUtc = DateTimeOffset.UtcNow;
        _signalLostSinceUtc = null;
        Current = CreateSnapshot(
            IndicatorState.Starting,
            T(TextId.RestartRequestSent),
            T(TextId.WaitingRestartReady),
            Current.Observation);
    }

    public void MarkExpectedStop()
    {
        _expectedStop = true;
        _restartAttemptActive = false;
        _restartTransitionObserved = false;
    }

    public void MarkOperationFailure(string summary, string reason)
    {
        _startAttemptActive = false;
        _startRequestedAtUtc = null;
        _observedPoweredDuringStart = false;
        _restartAttemptActive = false;
        _restartTransitionObserved = false;
        _faultLatched = true;
        _latchedFaultReason = reason;
        Current = CreateSnapshot(IndicatorState.Fault, summary, reason, Current.Observation);
    }

    public void ClearFault()
    {
        _faultLatched = false;
        _latchedFaultReason = null;
        _startAttemptActive = false;
        _startRequestedAtUtc = null;
        _observedPoweredDuringStart = false;
        _restartAttemptActive = false;
        _restartTransitionObserved = false;
        _bootNotReadySinceUtc = null;
        _signalLostSinceUtc = null;
    }

    public VmStatusSnapshot Update(VmObservation observation)
    {
        if (!observation.MonitoringSucceeded)
        {
            _consecutiveMonitorFailures++;
            if (_consecutiveMonitorFailures < _appConfig.MonitorFailureThreshold)
            {
                Current = Current with
                {
                    Detail = F(
                        TextId.MonitorQueryFailed,
                        _consecutiveMonitorFailures,
                        _appConfig.MonitorFailureThreshold,
                        observation.MonitoringError),
                    Observation = observation,
                    UpdatedAtUtc = observation.ObservedAtUtc
                };
                return Current;
            }

            Current = CreateSnapshot(
                IndicatorState.Unknown,
                T(TextId.MonitorApiUnavailable),
                observation.MonitoringError ?? T(TextId.UnknownMonitoringError),
                observation);
            return Current;
        }

        _consecutiveMonitorFailures = 0;

        if (!observation.Exists)
        {
            _faultLatched = false;
            _latchedFaultReason = null;
            _startAttemptActive = false;
            _startRequestedAtUtc = null;
            _observedPoweredDuringStart = false;
            _restartAttemptActive = false;
            _restartTransitionObserved = false;
            _bootNotReadySinceUtc = null;
            _signalLostSinceUtc = null;
            string reason = observation.MonitoringError ?? F(TextId.VmNotFoundReason, Config.Name);
            Current = CreateSnapshot(IndicatorState.Fault, T(TextId.VmMissingSummary), reason, observation);
            return Current;
        }

        if (observation.IsCriticalState)
        {
            string reason = BuildCriticalReason(observation);
            _faultLatched = true;
            _latchedFaultReason = reason;
            Current = CreateSnapshot(IndicatorState.Fault, T(TextId.HyperVCriticalFault), reason, observation);
            return Current;
        }

        if (observation.IsOffLike)
        {
            bool failedRestart = _restartAttemptActive;
            bool startGraceElapsed = _startRequestedAtUtc is not null &&
                observation.ObservedAtUtc - _startRequestedAtUtc.Value >= TimeSpan.FromSeconds(10);
            bool failedDuringStart = _startAttemptActive && !_expectedStop &&
                (_observedPoweredDuringStart || startGraceElapsed);

            if (_startAttemptActive && !_expectedStop && !failedDuringStart)
            {
                Current = CreateSnapshot(
                    IndicatorState.Starting,
                    T(TextId.StartRequestWaitingState),
                    BuildObservationDetail(observation),
                    observation);
                return Current;
            }

            _bootNotReadySinceUtc = null;
            _signalLostSinceUtc = null;
            _startAttemptActive = false;
            _startRequestedAtUtc = null;
            _observedPoweredDuringStart = false;
            _restartAttemptActive = false;
            _restartTransitionObserved = false;

            if (failedDuringStart)
            {
                _faultLatched = true;
                _latchedFaultReason = failedRestart
                    ? T(TextId.RestartFailedOff)
                    : T(TextId.StartFailedOff);
            }

            if (_faultLatched)
            {
                Current = CreateSnapshot(
                    IndicatorState.Fault,
                    T(TextId.FaultLatched),
                    _latchedFaultReason ?? T(TextId.PreviousFaultUncleared),
                    observation);
            }
            else
            {
                Current = CreateSnapshot(
                    IndicatorState.Off,
                    observation.EnabledState is 32769 or 32779 ? T(TextId.Saved) : T(TextId.Off),
                    BuildObservationDetail(observation),
                    observation);
            }

            _expectedStop = false;
            return Current;
        }

        if (observation.IsPaused)
        {
            if (_startAttemptActive)
            {
                _observedPoweredDuringStart = true;
            }

            if (_restartAttemptActive)
            {
                _restartTransitionObserved = true;
            }

            _signalLostSinceUtc = null;
            Current = CreateSnapshot(
                IndicatorState.Starting,
                T(TextId.Paused),
                BuildObservationDetail(observation),
                observation);
            return Current;
        }

        if (observation.IsStartingOrResuming)
        {
            _startAttemptActive = true;
            _startRequestedAtUtc ??= observation.ObservedAtUtc;
            _observedPoweredDuringStart = true;
            if (_restartAttemptActive)
            {
                _restartTransitionObserved = true;
            }

            _bootNotReadySinceUtc ??= observation.ObservedAtUtc;
            _signalLostSinceUtc = null;

            TimeSpan elapsed = observation.ObservedAtUtc - _bootNotReadySinceUtc.Value;
            if (elapsed.TotalSeconds >= _appConfig.StartupTimeoutSeconds)
            {
                return SetStartupTimeoutFault(observation, elapsed);
            }

            Current = CreateSnapshot(
                IndicatorState.Starting,
                F(TextId.WaitingReady, observation.FormatHyperVStateText(Language)),
                BuildObservationDetail(observation, elapsed),
                observation);
            return Current;
        }

        if (observation.IsStoppingOrSaving)
        {
            if (_restartAttemptActive)
            {
                _restartTransitionObserved = true;
            }
            else
            {
                _expectedStop = true;
            }
            _signalLostSinceUtc = null;
            Current = CreateSnapshot(
                IndicatorState.Starting,
                observation.FormatHyperVStateText(Language),
                BuildObservationDetail(observation),
                observation);
            return Current;
        }

        if (observation.IsRunning)
        {
            if (observation.IsReadySignalPresent)
            {
                if (_restartAttemptActive && !_restartTransitionObserved)
                {
                    ulong? previousUptime = Current.Observation?.UptimeMilliseconds;
                    if (previousUptime is not null && observation.UptimeMilliseconds + 2000 < previousUptime.Value)
                    {
                        _restartTransitionObserved = true;
                    }
                }

                if (_restartAttemptActive && !_restartTransitionObserved)
                {
                    DateTimeOffset requestedAt = _startRequestedAtUtc ?? observation.ObservedAtUtc;
                    TimeSpan waitingForRestart = observation.ObservedAtUtc - requestedAt;
                    if (waitingForRestart.TotalSeconds >= _appConfig.StartupTimeoutSeconds)
                    {
                        _faultLatched = true;
                        _latchedFaultReason = F(TextId.RestartNotStartedReason, FormatDuration(waitingForRestart));
                        Current = CreateSnapshot(
                            IndicatorState.Fault,
                            T(TextId.RestartNotStartedSummary),
                            BuildObservationDetail(observation, waitingForRestart, _latchedFaultReason),
                            observation);
                        return Current;
                    }

                    Current = CreateSnapshot(
                        IndicatorState.Starting,
                        T(TextId.WaitingGuestRestart),
                        BuildObservationDetail(observation, waitingForRestart),
                        observation);
                    return Current;
                }

                _bootNotReadySinceUtc = null;
                _signalLostSinceUtc = null;
                _startAttemptActive = false;
                _startRequestedAtUtc = null;
                _observedPoweredDuringStart = false;
                _restartAttemptActive = false;
                _restartTransitionObserved = false;
                _expectedStop = false;
                _faultLatched = false;
                _latchedFaultReason = null;

                string readiness = observation.Heartbeat is HeartbeatKind.Ok or HeartbeatKind.Degraded
                    ? $"Heartbeat {observation.FormatHeartbeatText(Language)}"
                    : observation.PingSucceeded == true
                        ? F(TextId.PingSucceededMs, observation.PingRoundtripMilliseconds ?? 0)
                        : T(TextId.ReadySignalNormal);

                Current = CreateSnapshot(
                    IndicatorState.Ready,
                    readiness,
                    BuildObservationDetail(observation),
                    observation);
                return Current;
            }

            if (_restartAttemptActive)
            {
                _restartTransitionObserved = true;
            }

            bool wasReady = !_restartAttemptActive &&
                (Current.Indicator == IndicatorState.Ready || _signalLostSinceUtc is not null);
            if (wasReady)
            {
                _signalLostSinceUtc ??= observation.ObservedAtUtc;
                TimeSpan lostFor = observation.ObservedAtUtc - _signalLostSinceUtc.Value;
                if (lostFor.TotalSeconds >= _appConfig.SignalLossGraceSeconds)
                {
                    Current = CreateSnapshot(
                        IndicatorState.Fault,
                        T(TextId.ReadySignalLostFault),
                        BuildObservationDetail(observation, lostFor),
                        observation);
                    return Current;
                }

                Current = CreateSnapshot(
                    IndicatorState.Starting,
                    T(TextId.ReadySignalLostTemporary),
                    BuildObservationDetail(observation, lostFor),
                    observation);
                return Current;
            }

            _bootNotReadySinceUtc ??= EstimateBootStart(observation);
            TimeSpan notReadyFor = observation.ObservedAtUtc - _bootNotReadySinceUtc.Value;
            _startAttemptActive = true;
            _startRequestedAtUtc ??= _bootNotReadySinceUtc;
            _observedPoweredDuringStart = true;

            if (notReadyFor.TotalSeconds >= _appConfig.StartupTimeoutSeconds)
            {
                return SetStartupTimeoutFault(observation, notReadyFor);
            }

            Current = CreateSnapshot(
                IndicatorState.Starting,
                T(TextId.RunningGuestNotReady),
                BuildObservationDetail(observation, notReadyFor),
                observation);
            return Current;
        }

        Current = CreateSnapshot(
            IndicatorState.Unknown,
            F(TextId.UnhandledHyperVState, observation.FormatHyperVStateText(Language)),
            BuildObservationDetail(observation),
            observation);
        return Current;
    }

    private VmStatusSnapshot SetStartupTimeoutFault(VmObservation observation, TimeSpan elapsed)
    {
        bool isRestart = _restartAttemptActive;
        _faultLatched = true;
        _latchedFaultReason = F(TextId.StartupTimeoutReason, FormatDuration(elapsed), _appConfig.StartupTimeoutSeconds);
        Current = CreateSnapshot(
            IndicatorState.Fault,
            isRestart ? T(TextId.RestartTimeout) : T(TextId.StartupTimeout),
            BuildObservationDetail(observation, elapsed, _latchedFaultReason),
            observation);
        return Current;
    }

    private static DateTimeOffset EstimateBootStart(VmObservation observation)
    {
        TimeSpan uptime = TimeSpan.FromMilliseconds(Math.Min(observation.UptimeMilliseconds, (ulong)TimeSpan.FromDays(30).TotalMilliseconds));
        return observation.ObservedAtUtc - uptime;
    }

    private VmStatusSnapshot CreateSnapshot(
        IndicatorState state,
        string summary,
        string detail,
        VmObservation? observation)
    {
        return new VmStatusSnapshot
        {
            Config = Config,
            Indicator = state,
            Summary = summary,
            Detail = detail,
            Observation = observation,
            UpdatedAtUtc = observation?.ObservedAtUtc ?? DateTimeOffset.UtcNow
        };
    }

    private string BuildCriticalReason(VmObservation observation)
    {
        StringBuilder builder = new();
        builder.Append("EnabledState=").Append(observation.EnabledState?.ToString() ?? "null");
        builder.Append(", HealthState=").Append(observation.HealthState?.ToString() ?? "null");

        if (observation.OperationalStatus.Length > 0)
        {
            builder.Append(", OperationalStatus=").Append(string.Join(",", observation.OperationalStatus));
        }

        if (observation.StatusDescriptions.Length > 0)
        {
            builder.Append(" (").Append(string.Join("; ", observation.StatusDescriptions)).Append(')');
        }

        return builder.ToString();
    }

    private string BuildObservationDetail(
        VmObservation observation,
        TimeSpan? elapsed = null,
        string? prefix = null)
    {
        StringBuilder builder = new();
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            builder.Append(prefix).AppendLine();
        }

        builder.Append("Hyper-V: ").Append(observation.FormatHyperVStateText(Language));
        builder.Append(" | ").Append(T(TextId.HealthStateLabel)).Append(": ").Append(observation.HealthState?.ToString() ?? T(TextId.Unknown));
        builder.AppendLine();
        builder.Append(T(TextId.HeartbeatLabel)).Append(": ").Append(observation.FormatHeartbeatText(Language));

        if (observation.HeartbeatDescriptions.Length > 0)
        {
            builder.Append(" (").Append(string.Join("; ", observation.HeartbeatDescriptions)).Append(')');
        }

        if (observation.PingSucceeded is not null)
        {
            builder.AppendLine();
            builder.Append(T(TextId.PingLabel)).Append(": ").Append(observation.PingSucceeded == true ? T(TextId.PingSucceeded) : T(TextId.PingFailed));
            if (observation.PingRoundtripMilliseconds is not null)
            {
                builder.Append(", ").Append(observation.PingRoundtripMilliseconds).Append(" ms");
            }
            else if (!string.IsNullOrWhiteSpace(observation.PingError))
            {
                builder.Append(" (").Append(observation.PingError).Append(')');
            }
        }

        builder.AppendLine();
        builder.Append(T(TextId.UptimeLabel)).Append(": ").Append(FormatDuration(TimeSpan.FromMilliseconds(observation.UptimeMilliseconds)));
        if (elapsed is not null)
        {
            builder.Append(" | ").Append(T(TextId.CurrentStageLabel)).Append(": ").Append(FormatDuration(elapsed.Value));
        }

        return builder.ToString();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration:hh\\:mm\\:ss}";
        }

        return duration.ToString(@"hh\:mm\:ss");
    }
}
