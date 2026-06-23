using HyperVStatusTray.Broker.Services;
using HyperVStatusTray.Protocol;
using HyperVStatusTray.Services;

namespace HyperVStatusTray.Broker;

internal sealed class BrokerEngine
{
    private readonly object _configLock = new();
    private readonly HyperVClient _hyperVClient = new();
    private AppConfig _config;

    public BrokerEngine()
    {
        _config = ConfigService.LoadOrCreate(out string? warning);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            Logger.Warning(warning);
        }
    }

    public BrokerResponse Handle(BrokerRequest request)
    {
        try
        {
            return request.Command switch
            {
                BrokerCommand.Ping => BrokerResponse.Ok(request.RequestId, "OK"),
                BrokerCommand.GetSnapshot => BrokerResponse.Ok(request.RequestId, snapshot: CreateSnapshot()),
                BrokerCommand.ExecuteVmAction => ExecuteVmAction(request),
                BrokerCommand.ReloadConfig => ReloadConfig(request),
                _ => BrokerResponse.Fail(request.RequestId, "不支持的 Broker 命令。")
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Broker 请求失败：{request.Command}", ex);
            return BrokerResponse.Fail(request.RequestId, ex.Message);
        }
    }

    private BrokerResponse ExecuteVmAction(BrokerRequest request)
    {
        if (request.VmIndex is null || request.Action is null)
        {
            return BrokerResponse.Fail(request.RequestId, "ExecuteVmAction 缺少 VmIndex 或 Action。");
        }

        AppConfig config = GetConfig();
        int vmIndex = request.VmIndex.Value;
        if (vmIndex < 0 || vmIndex >= config.VirtualMachines.Count)
        {
            return BrokerResponse.Fail(request.RequestId, "VmIndex 不在允许范围内。");
        }

        string vmName = config.VirtualMachines[vmIndex].Name;
        switch (request.Action.Value)
        {
            case VmAction.Start:
                _hyperVClient.Start(vmName);
                break;
            case VmAction.ShutDownGuest:
                _hyperVClient.ShutDownGuest(vmName);
                break;
            case VmAction.TurnOff:
                _hyperVClient.TurnOff(vmName);
                break;
            case VmAction.RestartGuest:
                _hyperVClient.RestartGuest(vmName);
                break;
            case VmAction.Reset:
                _hyperVClient.Reset(vmName);
                break;
            default:
                return BrokerResponse.Fail(request.RequestId, "不支持的虚拟机操作。");
        }

        Logger.Info($"{vmName}: 已执行 {request.Action.Value}。");
        return BrokerResponse.Ok(request.RequestId, "操作已被 Hyper-V 接受。", CreateSnapshot(config));
    }

    private BrokerResponse ReloadConfig(BrokerRequest request)
    {
        if (!ConfigService.TryReload(out AppConfig? loaded, out string? error) || loaded is null)
        {
            return BrokerResponse.Fail(request.RequestId, $"配置文件无效：{error}");
        }

        lock (_configLock)
        {
            _config = loaded;
        }

        Logger.Info("配置已重新加载。");
        return BrokerResponse.Ok(request.RequestId, "配置已重新加载。", CreateSnapshot(loaded));
    }

    private BrokerSnapshot CreateSnapshot() => CreateSnapshot(GetConfig());

    private BrokerSnapshot CreateSnapshot(AppConfig config)
    {
        VmObservation[] observations = config.VirtualMachines
            .Select(vm => _hyperVClient.Observe(vm))
            .ToArray();

        return new BrokerSnapshot
        {
            Config = config,
            Observations = observations,
            ObservedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private AppConfig GetConfig()
    {
        lock (_configLock)
        {
            return _config;
        }
    }
}
