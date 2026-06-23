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
            Summary = "等待第一次查询",
            Detail = "尚未从 Hyper-V 读取状态。",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public VmConfig Config { get; }

    public VmStatusSnapshot Current { get; private set; }

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
        Current = CreateSnapshot(IndicatorState.Starting, "启动请求已发送", "正在等待 Hyper-V 和客户机就绪信号。", Current.Observation);
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
            "重启请求已发送",
            "正在等待客户机进入重启过程并重新取得就绪信号。",
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
                    Detail = $"本次监控查询失败（{_consecutiveMonitorFailures}/{_appConfig.MonitorFailureThreshold}）：{observation.MonitoringError}",
                    Observation = observation,
                    UpdatedAtUtc = observation.ObservedAtUtc
                };
                return Current;
            }

            Current = CreateSnapshot(
                IndicatorState.Unknown,
                "无法访问监控接口",
                observation.MonitoringError ?? "未知监控错误。",
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
            string reason = observation.MonitoringError ?? $"找不到虚拟机 {Config.Name}。";
            Current = CreateSnapshot(IndicatorState.Fault, "虚拟机不存在", reason, observation);
            return Current;
        }

        if (observation.IsCriticalState)
        {
            string reason = BuildCriticalReason(observation);
            _faultLatched = true;
            _latchedFaultReason = reason;
            Current = CreateSnapshot(IndicatorState.Fault, "Hyper-V 报告关键故障", reason, observation);
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
                    "启动请求已发送，等待 Hyper-V 状态变化",
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
                    ? "虚拟机在重启期间掉回 Off/Saved 且未恢复就绪，疑似重启失败。"
                    : "虚拟机在达到就绪状态前重新回到 Off/Saved，疑似启动失败。";
            }

            if (_faultLatched)
            {
                Current = CreateSnapshot(
                    IndicatorState.Fault,
                    "故障已锁存",
                    _latchedFaultReason ?? "先前的故障仍未清除。",
                    observation);
            }
            else
            {
                Current = CreateSnapshot(
                    IndicatorState.Off,
                    observation.EnabledState is 32769 or 32779 ? "已保存" : "已关闭",
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
                "虚拟机已暂停",
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
                $"{observation.HyperVStateText}，等待就绪",
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
                observation.HyperVStateText,
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
                        _latchedFaultReason = $"重启请求已发送，但持续 {FormatDuration(waitingForRestart)} 未观察到客户机进入重启过程。";
                        Current = CreateSnapshot(
                            IndicatorState.Fault,
                            "重启未开始",
                            BuildObservationDetail(observation, waitingForRestart, _latchedFaultReason),
                            observation);
                        return Current;
                    }

                    Current = CreateSnapshot(
                        IndicatorState.Starting,
                        "等待客户机开始重启",
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
                    ? $"Heartbeat {observation.HeartbeatText}"
                    : observation.PingSucceeded == true
                        ? $"ICMP Ping 成功（{observation.PingRoundtripMilliseconds ?? 0} ms）"
                        : "就绪信号正常";

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
                        "就绪信号持续丢失",
                        BuildObservationDetail(observation, lostFor),
                        observation);
                    return Current;
                }

                Current = CreateSnapshot(
                    IndicatorState.Starting,
                    "就绪信号暂时丢失",
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
                "Hyper-V 已运行，客户机未就绪",
                BuildObservationDetail(observation, notReadyFor),
                observation);
            return Current;
        }

        Current = CreateSnapshot(
            IndicatorState.Unknown,
            $"未处理的 Hyper-V 状态：{observation.HyperVStateText}",
            BuildObservationDetail(observation),
            observation);
        return Current;
    }

    private VmStatusSnapshot SetStartupTimeoutFault(VmObservation observation, TimeSpan elapsed)
    {
        bool isRestart = _restartAttemptActive;
        _faultLatched = true;
        _latchedFaultReason = $"虚拟机已持续 {FormatDuration(elapsed)} 未取得 Heartbeat/Ping 就绪信号，超过 {_appConfig.StartupTimeoutSeconds} 秒阈值。";
        Current = CreateSnapshot(
            IndicatorState.Fault,
            isRestart ? "重启超时" : "启动超时",
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

    private static string BuildCriticalReason(VmObservation observation)
    {
        StringBuilder builder = new();
        builder.Append("EnabledState=").Append(observation.EnabledState?.ToString() ?? "null");
        builder.Append("，HealthState=").Append(observation.HealthState?.ToString() ?? "null");

        if (observation.OperationalStatus.Length > 0)
        {
            builder.Append("，OperationalStatus=").Append(string.Join(",", observation.OperationalStatus));
        }

        if (observation.StatusDescriptions.Length > 0)
        {
            builder.Append("（").Append(string.Join("；", observation.StatusDescriptions)).Append('）');
        }

        return builder.ToString();
    }

    private static string BuildObservationDetail(
        VmObservation observation,
        TimeSpan? elapsed = null,
        string? prefix = null)
    {
        StringBuilder builder = new();
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            builder.Append(prefix).AppendLine();
        }

        builder.Append("Hyper-V: ").Append(observation.HyperVStateText);
        builder.Append(" | HealthState: ").Append(observation.HealthState?.ToString() ?? "未知");
        builder.AppendLine();
        builder.Append("Heartbeat: ").Append(observation.HeartbeatText);

        if (observation.HeartbeatDescriptions.Length > 0)
        {
            builder.Append("（").Append(string.Join("；", observation.HeartbeatDescriptions)).Append('）');
        }

        if (observation.PingSucceeded is not null)
        {
            builder.AppendLine();
            builder.Append("Ping: ").Append(observation.PingSucceeded == true ? "成功" : "失败");
            if (observation.PingRoundtripMilliseconds is not null)
            {
                builder.Append("，").Append(observation.PingRoundtripMilliseconds).Append(" ms");
            }
            else if (!string.IsNullOrWhiteSpace(observation.PingError))
            {
                builder.Append("（").Append(observation.PingError).Append('）');
            }
        }

        builder.AppendLine();
        builder.Append("Uptime: ").Append(FormatDuration(TimeSpan.FromMilliseconds(observation.UptimeMilliseconds)));
        if (elapsed is not null)
        {
            builder.Append(" | 当前阶段: ").Append(FormatDuration(elapsed.Value));
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
