using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HyperVStatusTray.Protocol;
using HyperVStatusTray.Services;
using Microsoft.Win32.SafeHandles;

namespace HyperVStatusTray.Broker;

internal sealed class PipeBrokerServer : IDisposable
{
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int QueryProcessImageNameWin32Path = 0;
    private const int ProcessImagePathBufferChars = 32768;
    private static readonly TimeSpan ClientRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureResponseTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClientPathLookupWarningInterval = TimeSpan.FromMinutes(5);

    private readonly CancellationTokenSource _cancellation = new();
    private readonly BrokerEngine _engine = new();
    private readonly BrokerSecurityOptions _securityOptions;
    private readonly object _warningLock = new();
    private Task? _acceptLoop;
    private DateTimeOffset? _lastClientPathLookupWarningUtc;

    public PipeBrokerServer()
    {
        _securityOptions = ConfigService.LoadSecurityOptions();
    }

    public void Start()
    {
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
    }

    public async Task StopAsync()
    {
        _cancellation.Cancel();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal during service shutdown.
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                NamedPipeServerStream connectedPipe = pipe;
                pipe = null;
                _ = Task.Run(async () =>
                {
                    using (connectedPipe)
                    {
                        await HandleClientAsync(connectedPipe, cancellationToken).ConfigureAwait(false);
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                Logger.Error("接受 Pipe 连接失败。", ex);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(ClientRequestTimeout);

        BrokerRequest? request = null;
        string clientDescription = "unknown";
        try
        {
            request = await BrokerProtocol.ReadMessageAsync<BrokerRequest>(pipe, requestCancellation.Token).ConfigureAwait(false);

            ClientAuthorization authorization = AuthorizeClient(pipe);
            clientDescription = authorization.Description;
            if (!authorization.IsAuthorized)
            {
                Logger.Warning($"拒绝未授权 Pipe 客户端。Client={clientDescription}");
                await BrokerProtocol.WriteMessageAsync(
                    pipe,
                    BrokerResponse.Fail(request.RequestId, authorization.FailureMessage ?? "Pipe 客户端未授权。"),
                    requestCancellation.Token).ConfigureAwait(false);
                return;
            }

            BrokerResponse response = _engine.Handle(request);
            await BrokerProtocol.WriteMessageAsync(pipe, response, requestCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal during service shutdown.
        }
        catch (OperationCanceledException)
        {
            Logger.Warning($"Pipe 客户端请求超时。Client={clientDescription}");
            if (request is not null)
            {
                await TryWriteFailureAsync(pipe, request.RequestId, "Broker 请求超时。", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (EndOfStreamException ex)
        {
            Logger.Warning($"Pipe 客户端在发送完整请求前断开。Client={clientDescription}; {ex.Message}");
        }
        catch (IOException ex)
        {
            Logger.Warning($"Pipe 客户端通信中断。Client={clientDescription}; {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Error($"处理 Pipe 客户端失败。Client={clientDescription}", ex);
            if (request is not null)
            {
                await TryWriteFailureAsync(pipe, request.RequestId, "Broker 处理请求失败。", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private ClientAuthorization AuthorizeClient(NamedPipeServerStream pipe)
    {
        string? clientSid = TryGetClientUserSid(pipe, out string? sidError);
        string? clientPath = TryGetClientProcessPath(pipe, out uint? processId, out string? pathError);

        string sidDescription = clientSid ?? $"unavailable ({sidError ?? "unknown error"})";
        string pathDescription = clientPath ?? $"unavailable (PID={processId?.ToString() ?? "unknown"}; {pathError ?? "unknown error"})";
        string description = $"Sid={sidDescription}; Path={pathDescription}";
        bool sidMatches = string.Equals(clientSid, _securityOptions.AllowedUserSid, StringComparison.OrdinalIgnoreCase);

        if (clientSid is not null && !sidMatches)
        {
            return new ClientAuthorization(false, description, "Pipe 客户端用户未授权。");
        }

        if (clientPath is not null)
        {
            return IsAuthorizedClientPath(clientPath)
                ? new ClientAuthorization(true, description, null)
                : new ClientAuthorization(false, description, "Pipe 客户端程序路径未授权。");
        }

        if (sidMatches)
        {
            LogClientPathLookupFallback(description, pathError ?? "unknown error");
            return new ClientAuthorization(true, description, null);
        }

        return new ClientAuthorization(false, description, "无法确认 Pipe 客户端身份。");
    }

    private NamedPipeServerStream CreatePipe()
    {
        // additionalAccessRights is ORed into Win32 CreateNamedPipe dwOpenMode;
        // ACL-style rights such as ReadWrite/CreateNewInstance make that mode invalid.
        return NamedPipeServerStreamAcl.Create(
            BrokerProtocol.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 10,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: BrokerProtocol.MaxMessageBytes,
            outBufferSize: BrokerProtocol.MaxMessageBytes,
            pipeSecurity: CreatePipeSecurity(),
            inheritability: HandleInheritability.None);
    }

    private PipeSecurity CreatePipeSecurity()
    {
        PipeSecurity security = new();
        AddRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null), PipeAccessRights.FullControl);
        AddRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null), PipeAccessRights.FullControl);
        AddRule(security, new SecurityIdentifier(_securityOptions.AllowedUserSid), PipeAccessRights.ReadWrite);

        try
        {
            SecurityIdentifier serviceSid = (SecurityIdentifier)new NTAccount(AppPaths.ServiceAccountName)
                .Translate(typeof(SecurityIdentifier));
            AddRule(security, serviceSid, PipeAccessRights.FullControl);
        }
        catch (Exception ex)
        {
            Logger.Warning($"无法解析服务账户 SID：{ex.Message}");
        }

        return security;
    }

    private static void AddRule(PipeSecurity security, IdentityReference identity, PipeAccessRights rights)
    {
        security.AddAccessRule(new PipeAccessRule(
            identity,
            rights,
            AccessControlType.Allow));
    }

    private bool IsAuthorizedClientPath(string clientPath)
    {
        string expected = Path.GetFullPath(_securityOptions.AllowedClientPath);
        string actual = Path.GetFullPath(clientPath);
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private void LogClientPathLookupFallback(string clientDescription, string error)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_warningLock)
        {
            if (_lastClientPathLookupWarningUtc is not null &&
                now - _lastClientPathLookupWarningUtc.Value < ClientPathLookupWarningInterval)
            {
                return;
            }

            _lastClientPathLookupWarningUtc = now;
        }

        Logger.Warning($"无法读取 Pipe 客户端进程路径，已退回到用户 SID 校验。Client={clientDescription}; Error={error}");
    }

    private static string? TryGetClientUserSid(NamedPipeServerStream pipe, out string? error)
    {
        error = null;
        string? sid = null;
        try
        {
            pipe.RunAsClient(() =>
            {
                using WindowsIdentity? identity = WindowsIdentity.GetCurrent(true);
                sid = identity?.User?.Value;
            });

            if (string.IsNullOrWhiteSpace(sid))
            {
                error = "client SID was empty";
                return null;
            }

            return sid;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static string? TryGetClientProcessPath(NamedPipeServerStream pipe, out uint? processId, out string? error)
    {
        processId = null;
        error = null;

        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientProcessId))
        {
            error = $"GetNamedPipeClientProcessId failed. Win32Error={Marshal.GetLastWin32Error()}";
            return null;
        }

        processId = clientProcessId;
        using SafeProcessHandle processHandle = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, clientProcessId);
        if (processHandle.IsInvalid)
        {
            int errorCode = Marshal.GetLastWin32Error();
            error = $"OpenProcess failed for PID={clientProcessId}. {new Win32Exception(errorCode).Message}";
            return null;
        }

        StringBuilder path = new(ProcessImagePathBufferChars);
        int pathLength = path.Capacity;
        if (!QueryFullProcessImageName(processHandle, QueryProcessImageNameWin32Path, path, ref pathLength))
        {
            int errorCode = Marshal.GetLastWin32Error();
            error = $"QueryFullProcessImageName failed for PID={clientProcessId}. {new Win32Exception(errorCode).Message}";
            return null;
        }

        return path.ToString(0, pathLength);
    }

    private static async Task TryWriteFailureAsync(
        NamedPipeServerStream pipe,
        Guid requestId,
        string error,
        CancellationToken cancellationToken)
    {
        if (!pipe.IsConnected)
        {
            return;
        }

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(FailureResponseTimeout);
            await BrokerProtocol.WriteMessageAsync(pipe, BrokerResponse.Fail(requestId, error), timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Best effort only; the client may already be gone.
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    private sealed record ClientAuthorization(bool IsAuthorized, string Description, string? FailureMessage);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(int desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        int flags,
        StringBuilder exeName,
        ref int size);
}
