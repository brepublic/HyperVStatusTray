using System.Runtime.InteropServices;
using HyperVStatusTray.Services;

namespace HyperVStatusTray.Broker;

internal static class WindowsServiceHost
{
    private const int ServiceWin32OwnProcess = 0x00000010;
    private const int ServiceAcceptStop = 0x00000001;
    private const int ServiceAcceptShutdown = 0x00000004;
    private const int ServiceControlStop = 0x00000001;
    private const int ServiceControlShutdown = 0x00000005;
    private const int NoError = 0;

    private static readonly ManualResetEventSlim StopRequested = new(false);
    private static ServiceMainDelegate? _serviceMain;
    private static ServiceControlHandlerEx? _handler;
    private static IntPtr _statusHandle;
    private static PipeBrokerServer? _server;

    public static void Run(string serviceName)
    {
        _serviceMain = ServiceMain;
        ServiceTableEntry[] serviceTable =
        [
            new() { ServiceName = serviceName, ServiceMain = _serviceMain },
            new() { ServiceName = null, ServiceMain = null }
        ];

        if (!StartServiceCtrlDispatcher(serviceTable))
        {
            throw new InvalidOperationException(AppText.Format(
                AppText.DefaultLanguage,
                TextId.ServiceControlManagerConnectFailed,
                Marshal.GetLastWin32Error()));
        }
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _handler = ServiceHandler;
        _statusHandle = RegisterServiceCtrlHandlerEx(AppPaths.ServiceName, _handler, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            return;
        }

        SetStatus(ServiceState.StartPending, controlsAccepted: 0);

        try
        {
            Logger.Initialize(AppPaths.MachineDataDirectory, "HyperVStatusTrayBroker.log");
            _server = new PipeBrokerServer();
            _server.Start();
            Logger.Info(AppText.Get(AppText.DefaultLanguage, TextId.BrokerServiceStarted));
            SetStatus(ServiceState.Running, ServiceAcceptStop | ServiceAcceptShutdown);

            StopRequested.Wait();

            SetStatus(ServiceState.StopPending, controlsAccepted: 0);
            _server.StopAsync().GetAwaiter().GetResult();
            _server.Dispose();
            _server = null;
            Logger.Info(AppText.Get(AppText.DefaultLanguage, TextId.BrokerServiceStopped));
            SetStatus(ServiceState.Stopped, controlsAccepted: 0);
        }
        catch (Exception ex)
        {
            Logger.Error(AppText.Get(AppText.DefaultLanguage, TextId.BrokerServiceFatalError), ex);
            SetStatus(ServiceState.Stopped, controlsAccepted: 0, win32ExitCode: 1);
        }
    }

    private static int ServiceHandler(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        if (control is ServiceControlStop or ServiceControlShutdown)
        {
            SetStatus(ServiceState.StopPending, controlsAccepted: 0);
            StopRequested.Set();
            return NoError;
        }

        return NoError;
    }

    private static void SetStatus(ServiceState state, int controlsAccepted, int win32ExitCode = 0)
    {
        if (_statusHandle == IntPtr.Zero)
        {
            return;
        }

        ServiceStatus status = new()
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = controlsAccepted,
            Win32ExitCode = win32ExitCode,
            ServiceSpecificExitCode = 0,
            CheckPoint = state is ServiceState.StartPending or ServiceState.StopPending ? 1 : 0,
            WaitHint = state is ServiceState.StartPending or ServiceState.StopPending ? 30000 : 0
        };

        _ = SetServiceStatus(_statusHandle, ref status);
    }

    private delegate void ServiceMainDelegate(int argc, IntPtr argv);

    private delegate int ServiceControlHandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        public string? ServiceName;
        public ServiceMainDelegate? ServiceMain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public ServiceState CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    private enum ServiceState
    {
        Stopped = 1,
        StartPending = 2,
        StopPending = 3,
        Running = 4
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(
        string serviceName,
        ServiceControlHandlerEx handler,
        IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr serviceStatusHandle, ref ServiceStatus serviceStatus);
}
