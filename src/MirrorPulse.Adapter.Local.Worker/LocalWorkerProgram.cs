using System.Text;
using System.Text.Json;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Local.Worker;

public static class LocalWorkerProgram
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        AdapterWorkerProcessArguments arguments;
        try { arguments = AdapterWorkerProcessArguments.Parse(args); }
        catch (ArgumentException) { return 2; }
        await using AdapterNamedPipeClient pipe = await AdapterNamedPipeClient.ConnectAsync(
            arguments.PipeName, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        await using var channel = new AdapterControlChannel(pipe, arguments.InstanceId, arguments.WorkerSessionId);
        Guid helloId = Guid.NewGuid();
        IReadOnlyList<string> capabilities = AdapterHandshake.V2Capabilities;
#if LOCAL_ATOMIC_DIRECTORY_CANDIDATE
        string? stateDirectory = Environment.GetEnvironmentVariable("MP_WORKER_STATE_DIR");
        if (LocalAtomicDirectoryBackend.IsEnabled(stateDirectory))
            capabilities = [.. capabilities, AdapterOriginalDirectoryWire.Capability, AdapterAtomicDirectoryDeletionWire.Capability];
#endif
        var offer = new AdapterHello(new(2, 2), capabilities);
        await channel.SendAsync("Hello", helloId, false, offer, cancellationToken).ConfigureAwait(false);
        try
        {
            AdapterControlFrame frame = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (frame.MessageType == "HandshakeRejected") return 4;
            if (frame.MessageType != "Ready" || !frame.IsResponse || frame.RequestId != helloId)
                throw new InvalidDataException("InvalidReady");
            AdapterReady ready = AdapterProtocolJson.Decode<AdapterReady>(Encoding.UTF8.GetBytes(frame.Payload.GetRawText()));
            AdapterHandshake.ValidateReady(ready, offer);
            channel.SelectProtocol(ready.SelectedVersion);
            string cache = Environment.GetEnvironmentVariable("MP_TRANSFER_CACHE_DIR")
                ?? throw new InvalidDataException("TransferCacheRequired");
            var roots = new LocalWorkerRoots(ready.Roots);
            await using var protocol = new LocalWorkerProtocol(channel, arguments, roots, cache);
#if LOCAL_ATOMIC_DIRECTORY_CANDIDATE
            if (ready.Capabilities.Except(offer.Capabilities, StringComparer.Ordinal).Any())
                throw new InvalidDataException("UnadvertisedCapabilitySelected");
            if (LocalAtomicDirectoryBackend.IsEnabled(stateDirectory))
                protocol.ConfigureAtomicDirectoryBackend(new(roots, stateDirectory!), ready.Capabilities);
#endif
            await channel.SendAsync("Connected", helloId, false, new { }, cancellationToken).ConfigureAwait(false);
            await protocol.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 0; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or JsonException or UnauthorizedAccessException)
        {
            // Paths and configuration values must not become ordinary diagnostic output.
            string code = exception is UnauthorizedAccessException ? "AccessDenied" : "InvalidConfiguration";
            await channel.SendAsync("Error", helloId, false, new { code }, CancellationToken.None).ConfigureAwait(false);
            return 1;
        }
    }
}
