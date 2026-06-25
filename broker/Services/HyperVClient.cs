using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using HyperVStatusTray.Services;

namespace HyperVStatusTray.Broker.Services;

internal sealed class HyperVClient
{
    private const string NamespacePath = @"\\.\root\virtualization\v2";
    private const ushort AutomaticStartupActionNone = 2;
    private const ushort AutomaticStartupActionStartIfRunning = 3;
    private const ushort AutomaticStartupActionAlwaysStart = 4;

    public VmObservation Observe(VmConfig config, AppLanguage language)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        try
        {
            ManagementScope scope = CreateConnectedScope();
            using ManagementObject? vm = FindVirtualMachine(scope, config.Name);
            if (vm is null)
            {
                return new VmObservation
                {
                    ObservedAtUtc = now,
                    MonitoringSucceeded = true,
                    Exists = false,
                    MonitoringError = AppText.Format(language, TextId.VmNamedNotFound, config.Name)
                };
            }

            ushort? enabledState = ReadUInt16(vm, "EnabledState");
            ushort? healthState = ReadUInt16(vm, "HealthState");
            ushort[] operationalStatus = ReadUInt16Array(vm, "OperationalStatus");
            string[] statusDescriptions = ReadStringArray(vm, "StatusDescriptions");
            ulong uptime = ReadUInt64(vm, "OnTimeInMilliseconds") ?? 0;
            (VmStartupPolicy startupPolicy, int? automaticStartDelaySeconds) = ReadStartupSettings(vm);

            HeartbeatKind heartbeatKind = HeartbeatKind.NotRequested;
            ushort? heartbeatCode = null;
            string[] heartbeatDescriptions = [];

            if (config.UseHeartbeat && enabledState is not 3 and not 32769 and not 32779)
            {
                (heartbeatKind, heartbeatCode, heartbeatDescriptions) = ReadHeartbeat(vm);
            }

            bool? pingSucceeded = null;
            long? pingRoundtrip = null;
            string? pingError = null;

            bool heartbeatReady = heartbeatKind is HeartbeatKind.Ok or HeartbeatKind.Degraded;
            if (!heartbeatReady && !string.IsNullOrWhiteSpace(config.PingAddress) && enabledState == 2)
            {
                (pingSucceeded, pingRoundtrip, pingError) = TryPing(config.PingAddress, config.PingTimeoutMilliseconds);
            }

            return new VmObservation
            {
                ObservedAtUtc = now,
                MonitoringSucceeded = true,
                Exists = true,
                EnabledState = enabledState,
                HealthState = healthState,
                OperationalStatus = operationalStatus,
                StatusDescriptions = statusDescriptions,
                UptimeMilliseconds = uptime,
                Heartbeat = heartbeatKind,
                HeartbeatCode = heartbeatCode,
                HeartbeatDescriptions = heartbeatDescriptions,
                PingSucceeded = pingSucceeded,
                PingRoundtripMilliseconds = pingRoundtrip,
                PingError = pingError,
                StartupPolicy = startupPolicy,
                AutomaticStartDelaySeconds = automaticStartDelaySeconds
            };
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            return new VmObservation
            {
                ObservedAtUtc = now,
                MonitoringSucceeded = false,
                Exists = false,
                MonitoringError = FormatManagementError(ex, language)
            };
        }
    }

    public void Start(string vmName, AppLanguage language) => RequestStateChange(vmName, requestedState: 2, language);

    public void TurnOff(string vmName, AppLanguage language) => RequestStateChange(vmName, requestedState: 3, language);

    public void Reset(string vmName, AppLanguage language) => RequestStateChange(vmName, requestedState: 11, language);

    public void ShutDownGuest(string vmName, AppLanguage language) => InvokeShutdownComponent(vmName, "InitiateShutdown", language);

    public void RestartGuest(string vmName, AppLanguage language) => InvokeShutdownComponent(vmName, "InitiateReboot", language);

    public void SetStartupPolicy(string vmName, VmStartupPolicy policy, int? delaySeconds, AppLanguage language)
    {
        if (policy == VmStartupPolicy.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), AppText.Get(language, TextId.UnknownStartupPolicyNotWritable));
        }

        if (policy != VmStartupPolicy.Disabled && delaySeconds is null)
        {
            throw new ArgumentException(AppText.Get(language, TextId.AutomaticStartDelayRequiredWhenEnabled), nameof(delaySeconds));
        }

        if (delaySeconds is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delaySeconds), AppText.Get(language, TextId.AutomaticStartDelayMustBeZeroOrGreater));
        }

        ManagementScope scope = CreateConnectedScope();
        using ManagementObject vm = FindVirtualMachine(scope, vmName)
            ?? throw new InvalidOperationException(AppText.Format(language, TextId.VirtualMachineNotFound, vmName));
        using ManagementObject settings = GetActiveVirtualSystemSettingData(vm)
            ?? throw new InvalidOperationException(AppText.Get(language, TextId.ActiveVmSettingsNotFound));

        settings["AutomaticStartupAction"] = ToAutomaticStartupAction(policy);
        if (policy != VmStartupPolicy.Disabled)
        {
            settings["AutomaticStartupActionDelay"] = ManagementDateTimeConverter.ToDmtfTimeInterval(TimeSpan.FromSeconds(delaySeconds!.Value));
        }

        using ManagementObject service = GetVirtualSystemManagementService(scope);
        using ManagementBaseObject input = service.GetMethodParameters("ModifySystemSettings");
        input["SystemSettings"] = settings.GetText(TextFormat.CimDtd20);
        using ManagementBaseObject output = service.InvokeMethod("ModifySystemSettings", input, null)
            ?? throw new InvalidOperationException(AppText.Get(language, TextId.HyperVNoModifyResult));

        uint result = Convert.ToUInt32(output["ReturnValue"], CultureInfo.InvariantCulture);
        if (result is not 0 and not 4096)
        {
            throw new InvalidOperationException(AppText.Format(language, TextId.HyperVModifyFailed, result));
        }
    }

    private static ManagementScope CreateConnectedScope()
    {
        ConnectionOptions options = new()
        {
            EnablePrivileges = true,
            Impersonation = ImpersonationLevel.Impersonate
        };

        ManagementScope scope = new(NamespacePath, options);
        scope.Connect();
        return scope;
    }

    private static ManagementObject? FindVirtualMachine(ManagementScope scope, string vmName)
    {
        ObjectQuery query = new("SELECT * FROM Msvm_ComputerSystem");
        System.Management.EnumerationOptions options = CreateEnumerationOptions();

        using ManagementObjectSearcher searcher = new(scope, query, options);
        using ManagementObjectCollection results = searcher.Get();

        string? matchingPath = null;
        foreach (ManagementObject item in results)
        {
            using (item)
            {
                string? elementName = item["ElementName"] as string;
                string? description = item["Description"] as string;
                string? caption = item["Caption"] as string;
                bool isVirtualMachine =
                    string.Equals(description, "Microsoft Virtual Computer System", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(caption, "Virtual Machine", StringComparison.OrdinalIgnoreCase);

                if (isVirtualMachine && string.Equals(elementName, vmName, StringComparison.OrdinalIgnoreCase))
                {
                    matchingPath = item.Path.Path;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(matchingPath))
        {
            return null;
        }

        ManagementObject result = new(scope, new ManagementPath(matchingPath), new ObjectGetOptions());
        result.Get();
        return result;
    }

    private static (HeartbeatKind Kind, ushort? Code, string[] Descriptions) ReadHeartbeat(ManagementObject vm)
    {
        using ManagementObjectCollection related = vm.GetRelated(
            relatedClass: "Msvm_HeartbeatComponent",
            relationshipClass: "Msvm_SystemDevice",
            relationshipQualifier: null,
            relatedQualifier: null,
            relatedRole: null,
            thisRole: null,
            classDefinitionsOnly: false,
            options: CreateEnumerationOptions());

        foreach (ManagementObject heartbeat in related)
        {
            using (heartbeat)
            {
                ushort[] statuses = ReadUInt16Array(heartbeat, "OperationalStatus");
                string[] descriptions = ReadStringArray(heartbeat, "StatusDescriptions");
                ushort? code = statuses.Length > 0 ? statuses[0] : null;

                HeartbeatKind kind = code switch
                {
                    2 => HeartbeatKind.Ok,
                    3 => HeartbeatKind.Degraded,
                    7 => HeartbeatKind.ProtocolError,
                    12 => HeartbeatKind.NoContact,
                    13 => HeartbeatKind.LostCommunication,
                    15 => HeartbeatKind.Paused,
                    null => HeartbeatKind.Unknown,
                    _ => HeartbeatKind.Unknown
                };

                return (kind, code, descriptions);
            }
        }

        return (HeartbeatKind.Missing, null, []);
    }

    private static (VmStartupPolicy Policy, int? DelaySeconds) ReadStartupSettings(ManagementObject vm)
    {
        try
        {
            using ManagementObject? settings = GetActiveVirtualSystemSettingData(vm);
            if (settings is null)
            {
                return (VmStartupPolicy.Unknown, null);
            }

            VmStartupPolicy policy = ReadUInt16(settings, "AutomaticStartupAction") switch
            {
                AutomaticStartupActionNone => VmStartupPolicy.Disabled,
                AutomaticStartupActionStartIfRunning => VmStartupPolicy.StartIfRunning,
                AutomaticStartupActionAlwaysStart => VmStartupPolicy.AlwaysStart,
                _ => VmStartupPolicy.Unknown
            };

            int? delaySeconds = null;
            if (settings["AutomaticStartupActionDelay"] is string interval &&
                !string.IsNullOrWhiteSpace(interval))
            {
                TimeSpan delay = ManagementDateTimeConverter.ToTimeSpan(interval);
                delaySeconds = Math.Max(0, Convert.ToInt32(delay.TotalSeconds, CultureInfo.InvariantCulture));
            }

            return (policy, delaySeconds);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            Logger.Warning($"Failed to read VM startup policy: {ex.Message}");
            return (VmStartupPolicy.Unknown, null);
        }
    }

    private static (bool? Success, long? RoundtripMilliseconds, string? Error) TryPing(string address, int timeoutMilliseconds)
    {
        try
        {
            using Ping ping = new();
            PingReply reply = ping.Send(address, timeoutMilliseconds);
            return reply.Status == IPStatus.Success
                ? (true, reply.RoundtripTime, null)
                : (false, null, reply.Status.ToString());
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException or ArgumentException)
        {
            return (false, null, ex.Message);
        }
    }

    private static void RequestStateChange(string vmName, ushort requestedState, AppLanguage language)
    {
        ManagementScope scope = CreateConnectedScope();
        using ManagementObject vm = FindVirtualMachine(scope, vmName)
            ?? throw new InvalidOperationException(AppText.Format(language, TextId.VmNotFoundReason, vmName));

        using ManagementBaseObject input = vm.GetMethodParameters("RequestStateChange");
        input["RequestedState"] = requestedState;
        using ManagementBaseObject output = vm.InvokeMethod("RequestStateChange", input, null)
            ?? throw new InvalidOperationException(AppText.Get(language, TextId.HyperVNoOperationResult));

        uint result = Convert.ToUInt32(output["ReturnValue"], CultureInfo.InvariantCulture);
        if (result is not 0 and not 4096)
        {
            throw new InvalidOperationException(AppText.Format(language, TextId.HyperVRequestStateChangeFailed, result));
        }
    }

    private static ManagementObject GetVirtualSystemManagementService(ManagementScope scope)
    {
        ObjectQuery query = new("SELECT * FROM Msvm_VirtualSystemManagementService");
        System.Management.EnumerationOptions options = CreateEnumerationOptions();

        using ManagementObjectSearcher searcher = new(scope, query, options);
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementObject service in results)
        {
            return service;
        }

        throw new InvalidOperationException("Hyper-V VirtualSystemManagementService was not found.");
    }

    private static ManagementObject? GetActiveVirtualSystemSettingData(ManagementObject vm)
    {
        using ManagementObjectCollection related = vm.GetRelated(
            relatedClass: "Msvm_VirtualSystemSettingData",
            relationshipClass: "Msvm_SettingsDefineState",
            relationshipQualifier: null,
            relatedQualifier: null,
            relatedRole: null,
            thisRole: null,
            classDefinitionsOnly: false,
            options: CreateEnumerationOptions());

        ManagementObject? fallback = null;
        foreach (ManagementObject settings in related)
        {
            string? virtualSystemType = settings["VirtualSystemType"] as string;
            string? description = settings["Description"] as string;
            if (string.Equals(virtualSystemType, "Microsoft:Hyper-V:System:Realized", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(description, "Active settings for the virtual machine", StringComparison.OrdinalIgnoreCase))
            {
                fallback?.Dispose();
                return settings;
            }

            if (fallback is null)
            {
                fallback = settings;
            }
            else
            {
                settings.Dispose();
            }
        }

        return fallback;
    }

    private static ushort ToAutomaticStartupAction(VmStartupPolicy policy) => policy switch
    {
        VmStartupPolicy.Disabled => AutomaticStartupActionNone,
        VmStartupPolicy.StartIfRunning => AutomaticStartupActionStartIfRunning,
        VmStartupPolicy.AlwaysStart => AutomaticStartupActionAlwaysStart,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unsupported VM startup policy.")
    };

    private static void InvokeShutdownComponent(string vmName, string methodName, AppLanguage language)
    {
        ManagementScope scope = CreateConnectedScope();
        using ManagementObject vm = FindVirtualMachine(scope, vmName)
            ?? throw new InvalidOperationException(AppText.Format(language, TextId.VmNotFoundReason, vmName));

        using ManagementObjectCollection related = vm.GetRelated(
            relatedClass: "Msvm_ShutdownComponent",
            relationshipClass: "Msvm_SystemDevice",
            relationshipQualifier: null,
            relatedQualifier: null,
            relatedRole: null,
            thisRole: null,
            classDefinitionsOnly: false,
            options: CreateEnumerationOptions());

        foreach (ManagementObject shutdown in related)
        {
            using (shutdown)
            using (ManagementBaseObject input = shutdown.GetMethodParameters(methodName))
            {
                input["Force"] = false;
                input["Reason"] = "Requested from HyperVStatusTray";
                using ManagementBaseObject output = shutdown.InvokeMethod(methodName, input, null)
                    ?? throw new InvalidOperationException(AppText.Get(language, TextId.HyperVNoOperationResult));

                uint result = Convert.ToUInt32(output["ReturnValue"], CultureInfo.InvariantCulture);
                if (result is not 0 and not 4096)
                {
                    throw new InvalidOperationException(AppText.Format(language, TextId.ShutdownComponentFailed, methodName, result));
                }

                return;
            }
        }

        throw new InvalidOperationException(AppText.Get(language, TextId.ShutdownComponentMissing));
    }

    private static ushort? ReadUInt16(ManagementBaseObject obj, string propertyName)
    {
        object? value = obj[propertyName];
        return value is null ? null : Convert.ToUInt16(value, CultureInfo.InvariantCulture);
    }

    private static ulong? ReadUInt64(ManagementBaseObject obj, string propertyName)
    {
        object? value = obj[propertyName];
        return value is null ? null : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
    }

    private static ushort[] ReadUInt16Array(ManagementBaseObject obj, string propertyName)
    {
        object? value = obj[propertyName];
        if (value is ushort[] array)
        {
            return array;
        }

        if (value is Array genericArray)
        {
            return genericArray.Cast<object>()
                .Select(item => Convert.ToUInt16(item, CultureInfo.InvariantCulture))
                .ToArray();
        }

        return [];
    }

    private static string[] ReadStringArray(ManagementBaseObject obj, string propertyName)
    {
        object? value = obj[propertyName];
        return value switch
        {
            string[] array => array,
            Array genericArray => genericArray.Cast<object>().Select(item => item?.ToString() ?? string.Empty).ToArray(),
            _ => []
        };
    }

    private static System.Management.EnumerationOptions CreateEnumerationOptions() => new()
    {
        ReturnImmediately = false,
        Rewindable = false,
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static string FormatManagementError(Exception exception, AppLanguage language)
    {
        if (exception is UnauthorizedAccessException)
        {
            return AppText.Get(language, TextId.HyperVWmiAccessDenied);
        }

        if (exception is ManagementException managementException)
        {
            if (managementException.ErrorCode == ManagementStatus.AccessDenied)
            {
                return AppText.Get(language, TextId.HyperVWmiAccessDenied);
            }

            return AppText.Format(language, TextId.HyperVWmiQueryFailed, managementException.ErrorCode, managementException.Message);
        }

        return AppText.Format(language, TextId.HyperVMonitoringFailed, exception.Message);
    }
}
