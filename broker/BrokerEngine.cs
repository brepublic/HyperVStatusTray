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
                BrokerCommand.SetVmStartupPolicy => SetVmStartupPolicy(request),
                BrokerCommand.SetLanguage => SetLanguage(request),
                _ => BrokerResponse.Fail(request.RequestId, T(TextId.BrokerUnsupportedCommand))
            };
        }
        catch (Exception ex)
        {
            Logger.Error(F(TextId.BrokerRequestFailedLog, request.Command), ex);
            return BrokerResponse.Fail(request.RequestId, ex.Message);
        }
    }

    private BrokerResponse ExecuteVmAction(BrokerRequest request)
    {
        if (request.VmIndex is null || request.Action is null)
        {
            return BrokerResponse.Fail(request.RequestId, T(TextId.ExecuteVmActionMissing));
        }

        AppConfig config = GetConfig();
        int vmIndex = request.VmIndex.Value;
        if (vmIndex < 0 || vmIndex >= config.VirtualMachines.Count)
        {
            return BrokerResponse.Fail(request.RequestId, T(TextId.VmIndexOutOfRange));
        }

        string vmName = config.VirtualMachines[vmIndex].Name;
        switch (request.Action.Value)
        {
            case VmAction.Start:
                _hyperVClient.Start(vmName, config.Language);
                break;
            case VmAction.ShutDownGuest:
                _hyperVClient.ShutDownGuest(vmName, config.Language);
                break;
            case VmAction.TurnOff:
                _hyperVClient.TurnOff(vmName, config.Language);
                break;
            case VmAction.RestartGuest:
                _hyperVClient.RestartGuest(vmName, config.Language);
                break;
            case VmAction.Reset:
                _hyperVClient.Reset(vmName, config.Language);
                break;
            default:
                return BrokerResponse.Fail(request.RequestId, T(TextId.UnsupportedVmAction));
        }

        Logger.Info(AppText.Format(config.Language, TextId.ActionExecutedLog, vmName, request.Action.Value));
        return BrokerResponse.Ok(request.RequestId, AppText.Get(config.Language, TextId.OperationAccepted), CreateSnapshot(config));
    }

    private BrokerResponse SetVmStartupPolicy(BrokerRequest request)
    {
        if (request.VmIndex is null || request.StartupPolicy is null)
        {
            return BrokerResponse.Fail(request.RequestId, T(TextId.SetStartupPolicyMissing));
        }

        AppConfig config = GetConfig();
        int vmIndex = request.VmIndex.Value;
        if (vmIndex < 0 || vmIndex >= config.VirtualMachines.Count)
        {
            return BrokerResponse.Fail(request.RequestId, T(TextId.VmIndexOutOfRange));
        }

        VmStartupPolicy policy = request.StartupPolicy.Value;
        if (policy == VmStartupPolicy.Unknown)
        {
            return BrokerResponse.Fail(request.RequestId, T(TextId.UnsupportedVmStartupPolicy));
        }

        if (policy != VmStartupPolicy.Disabled && request.AutomaticStartDelaySeconds is null)
        {
            return BrokerResponse.Fail(request.RequestId, T(TextId.AutomaticStartDelayMissing));
        }

        if (request.AutomaticStartDelaySeconds is < 0)
        {
            return BrokerResponse.Fail(request.RequestId, T(TextId.AutomaticStartDelayNegative));
        }

        string vmName = config.VirtualMachines[vmIndex].Name;
        _hyperVClient.SetStartupPolicy(vmName, policy, request.AutomaticStartDelaySeconds, config.Language);

        Logger.Info(AppText.Format(config.Language, TextId.StartupPolicyUpdatedLog, vmName, policy));
        return BrokerResponse.Ok(request.RequestId, AppText.Get(config.Language, TextId.StartupPolicyUpdated), CreateSnapshot(config));
    }

    private BrokerResponse ReloadConfig(BrokerRequest request)
    {
        if (!ConfigService.TryReload(out AppConfig? loaded, out string? error) || loaded is null)
        {
            return BrokerResponse.Fail(request.RequestId, F(TextId.ConfigInvalidShort, error));
        }

        lock (_configLock)
        {
            _config = loaded;
        }

        Logger.Info(AppText.Get(loaded.Language, TextId.BrokerConfigReloaded));
        return BrokerResponse.Ok(request.RequestId, AppText.Get(loaded.Language, TextId.BrokerConfigReloaded), CreateSnapshot(loaded));
    }

    private BrokerResponse SetLanguage(BrokerRequest request)
    {
        if (request.Language is null)
        {
            return BrokerResponse.Fail(request.RequestId, T(TextId.SetLanguageMissing));
        }

        AppLanguage language = AppText.Normalize(request.Language.Value);
        AppConfig config;
        lock (_configLock)
        {
            _config.Language = language;
            _config.Validate();
            ConfigService.Save(_config);
            config = _config;
        }

        Logger.Info(AppText.Get(language, TextId.LanguageUpdatedMessage));
        return BrokerResponse.Ok(request.RequestId, AppText.Get(language, TextId.LanguageUpdatedMessage), CreateSnapshot(config));
    }

    private BrokerSnapshot CreateSnapshot() => CreateSnapshot(GetConfig());

    private BrokerSnapshot CreateSnapshot(AppConfig config)
    {
        VmObservation[] observations = config.VirtualMachines
            .Select(vm => _hyperVClient.Observe(vm, config.Language))
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

    public AppLanguage Language => GetConfig().Language;

    private string T(TextId id) => AppText.Get(Language, id);

    private string F(TextId id, params object?[] args) => AppText.Format(Language, id, args);
}
