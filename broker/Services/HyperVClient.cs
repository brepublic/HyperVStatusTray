using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace HyperVStatusTray.Broker.Services;

internal sealed class HyperVClient
{
    private const string NamespacePath = @"\\.\root\virtualization\v2";

    public VmObservation Observe(VmConfig config)
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
                    MonitoringError = $"找不到名为 {config.Name} 的虚拟机。"
                };
            }

            ushort? enabledState = ReadUInt16(vm, "EnabledState");
            ushort? healthState = ReadUInt16(vm, "HealthState");
            ushort[] operationalStatus = ReadUInt16Array(vm, "OperationalStatus");
            string[] statusDescriptions = ReadStringArray(vm, "StatusDescriptions");
            ulong uptime = ReadUInt64(vm, "OnTimeInMilliseconds") ?? 0;

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
                PingError = pingError
            };
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            return new VmObservation
            {
                ObservedAtUtc = now,
                MonitoringSucceeded = false,
                Exists = false,
                MonitoringError = FormatManagementError(ex)
            };
        }
    }

    public void Start(string vmName) => RequestStateChange(vmName, requestedState: 2);

    public void TurnOff(string vmName) => RequestStateChange(vmName, requestedState: 3);

    public void Reset(string vmName) => RequestStateChange(vmName, requestedState: 11);

    public void ShutDownGuest(string vmName) => InvokeShutdownComponent(vmName, "InitiateShutdown");

    public void RestartGuest(string vmName) => InvokeShutdownComponent(vmName, "InitiateReboot");

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

    private static void RequestStateChange(string vmName, ushort requestedState)
    {
        ManagementScope scope = CreateConnectedScope();
        using ManagementObject vm = FindVirtualMachine(scope, vmName)
            ?? throw new InvalidOperationException($"找不到虚拟机 {vmName}。");

        using ManagementBaseObject input = vm.GetMethodParameters("RequestStateChange");
        input["RequestedState"] = requestedState;
        using ManagementBaseObject output = vm.InvokeMethod("RequestStateChange", input, null)
            ?? throw new InvalidOperationException("Hyper-V 没有返回操作结果。");

        uint result = Convert.ToUInt32(output["ReturnValue"], CultureInfo.InvariantCulture);
        if (result is not 0 and not 4096)
        {
            throw new InvalidOperationException($"Hyper-V RequestStateChange 失败，返回码：{result}。");
        }
    }

    private static void InvokeShutdownComponent(string vmName, string methodName)
    {
        ManagementScope scope = CreateConnectedScope();
        using ManagementObject vm = FindVirtualMachine(scope, vmName)
            ?? throw new InvalidOperationException($"找不到虚拟机 {vmName}。");

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
                    ?? throw new InvalidOperationException("Hyper-V 没有返回操作结果。");

                uint result = Convert.ToUInt32(output["ReturnValue"], CultureInfo.InvariantCulture);
                if (result is not 0 and not 4096)
                {
                    throw new InvalidOperationException($"{methodName} 失败，返回码：{result}。请检查 Shutdown 集成服务。");
                }

                return;
            }
        }

        throw new InvalidOperationException("未找到 Hyper-V Shutdown 集成服务；无法执行客户机内的正常关机/重启。");
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

    private static string FormatManagementError(Exception exception)
    {
        if (exception is UnauthorizedAccessException)
        {
            return "访问 Hyper-V WMI 被拒绝。请确认 HyperVStatusTrayBroker 服务账户属于 Hyper-V Administrators 组。";
        }

        if (exception is ManagementException managementException)
        {
            if (managementException.ErrorCode == ManagementStatus.AccessDenied)
            {
                return "访问 Hyper-V WMI 被拒绝。请确认 HyperVStatusTrayBroker 服务账户属于 Hyper-V Administrators 组。";
            }

            return $"Hyper-V WMI 查询失败：{managementException.ErrorCode} — {managementException.Message}";
        }

        return $"Hyper-V 监控失败：{exception.Message}";
    }
}
