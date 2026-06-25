using System.IO.Pipes;
using System.Security.Principal;
using HyperVStatusTray.Protocol;

namespace HyperVStatusTray.Services;

internal sealed class BrokerClient
{
    private const int ConnectTimeoutMilliseconds = 3000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public AppLanguage Language { get; set; } = AppText.DefaultLanguage;

    public async Task<BrokerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        BrokerResponse response = await SendAsync(
            new BrokerRequest { Command = BrokerCommand.GetSnapshot },
            cancellationToken);
        return response.Snapshot ?? throw new InvalidOperationException(T(TextId.BrokerNoSnapshot));
    }

    public async Task<BrokerSnapshot> ExecuteVmActionAsync(int vmIndex, VmAction action, CancellationToken cancellationToken)
    {
        BrokerResponse response = await SendAsync(
            new BrokerRequest
            {
                Command = BrokerCommand.ExecuteVmAction,
                VmIndex = vmIndex,
                Action = action
            },
            cancellationToken);
        return response.Snapshot ?? throw new InvalidOperationException(T(TextId.BrokerNoActionSnapshot));
    }

    public async Task<BrokerSnapshot> ReloadConfigAsync(CancellationToken cancellationToken)
    {
        BrokerResponse response = await SendAsync(
            new BrokerRequest { Command = BrokerCommand.ReloadConfig },
            cancellationToken);
        return response.Snapshot ?? throw new InvalidOperationException(T(TextId.BrokerNoReloadSnapshot));
    }

    public async Task<BrokerSnapshot> SetVmStartupPolicyAsync(
        int vmIndex,
        VmStartupPolicy policy,
        int? automaticStartDelaySeconds,
        CancellationToken cancellationToken)
    {
        BrokerResponse response = await SendAsync(
            new BrokerRequest
            {
                Command = BrokerCommand.SetVmStartupPolicy,
                VmIndex = vmIndex,
                StartupPolicy = policy,
                AutomaticStartDelaySeconds = automaticStartDelaySeconds
            },
            cancellationToken);
        return response.Snapshot ?? throw new InvalidOperationException(T(TextId.BrokerNoStartupPolicySnapshot));
    }

    public async Task<BrokerSnapshot> SetLanguageAsync(AppLanguage language, CancellationToken cancellationToken)
    {
        BrokerResponse response = await SendAsync(
            new BrokerRequest
            {
                Command = BrokerCommand.SetLanguage,
                Language = language
            },
            cancellationToken);
        return response.Snapshot ?? throw new InvalidOperationException(T(TextId.BrokerNoLanguageSnapshot));
    }

    private async Task<BrokerResponse> SendAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(RequestTimeout);

        try
        {
            using NamedPipeClientStream pipe = new(
                ".",
                BrokerProtocol.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Impersonation);

            await pipe.ConnectAsync(ConnectTimeoutMilliseconds, requestCancellation.Token).ConfigureAwait(false);
            await BrokerProtocol.WriteMessageAsync(pipe, request, requestCancellation.Token).ConfigureAwait(false);
            BrokerResponse response = await BrokerProtocol.ReadMessageAsync<BrokerResponse>(pipe, requestCancellation.Token).ConfigureAwait(false);
            if (response.RequestId != request.RequestId)
            {
                throw new InvalidOperationException(T(TextId.BrokerMismatchedResponse));
            }

            if (!response.Success)
            {
                throw new InvalidOperationException(response.Error ?? T(TextId.BrokerRequestFailed));
            }

            return response;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(T(TextId.BrokerRequestTimedOut), ex);
        }
    }

    private string T(TextId id) => AppText.Get(Language, id);
}
