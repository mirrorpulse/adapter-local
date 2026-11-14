using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Local.Worker;

internal enum LocalDirectoryMoveCheckpoint { AfterSourceChecked, AfterNativeStaged, AfterReceiptStaged, AfterCommit }

/// <summary>The original container move, not an acceptance of each descendant's current content.</summary>
internal sealed record LocalDirectoryMoveReceipt(int Version, Guid InstanceId, Guid OperationId, string RequestFingerprint,
    string SourceScope, string DestinationScope, string SourceGeneration, Guid TransactionId, string AcceptedRevision, string Checksum);

internal sealed record LocalDirectoryMoveIntent(int Version, Guid InstanceId, Guid OperationId, string RequestFingerprint,
    string SourceGeneration, long SourceUsn, string Checksum);

internal static partial class LocalFileOperations
{
    internal static bool HasDirectoryMoveOwnership(string? stateDirectory, Guid operationId) => stateDirectory is not null &&
        (File.Exists(DirectoryMoveReceiptPath(stateDirectory, operationId)) || File.Exists(DirectoryMoveIntentPath(stateDirectory, operationId)));

    internal static void RequireNoDurableDirectoryMove(string? stateDirectory, Guid operationId)
    {
        if (HasDirectoryMoveOwnership(stateDirectory, operationId)) throw new InvalidDataException("OperationBindingMismatch");
    }

    internal static async Task<string> MoveDirectoryDurablyAsync(Guid instanceId, LocalWorkerPaths sourcePaths,
        LocalWorkerPaths destinationPaths, AdapterOperationRequest operation, string stateDirectory,
        Func<LocalDirectoryMoveCheckpoint, CancellationToken, ValueTask>? checkpoint = null, CancellationToken cancellationToken = default)
    {
        AdapterProtocolJson.ValidateMutation(operation, requiresDestination: true);
        if (instanceId == Guid.Empty || !operation.IsDirectory ||
            operation.Preconditions is not { DestinationMustBeAbsent: true, ExpectedRevision: { Length: > 0 } })
            throw new InvalidDataException("DurableDirectoryMovePreconditionsRequired");
        if (!Path.IsPathFullyQualified(stateDirectory) || !Directory.Exists(stateDirectory))
            throw new InvalidDataException("DurableDirectoryMoveStateRequired");
        stateDirectory = Path.GetFullPath(stateDirectory);
        foreach (string root in new[] { sourcePaths.Root, destinationPaths.Root })
            if (ContainsPath(stateDirectory, root) || ContainsPath(root, stateDirectory))
                throw new InvalidDataException("DurableDirectoryMoveStateMustBePrivate");
        cancellationToken.ThrowIfCancellationRequested();
        string source = sourcePaths.ResolveAddress(operation.Path);
        string destination = destinationPaths.ResolveAddress(operation.DestinationPath!);
        string receiptPath = DirectoryMoveReceiptPath(stateDirectory, operation.OperationId);
        try
        {
            using var sourceRoot = new DirectoryLease(sourcePaths.Root);
            using var destinationRoot = new DirectoryLease(destinationPaths.Root);
            using var stateRoot = new DirectoryLease(stateDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
            using var receiptParents = new DirectoryLease(Path.GetDirectoryName(receiptPath)!);
            var sourceScope = ReadDirectoryMoveScope(sourcePaths.Root);
            var destinationScope = ReadDirectoryMoveScope(destinationPaths.Root);
            string fingerprint = DirectoryMoveRequestFingerprint(instanceId, operation, sourceScope.Fingerprint, destinationScope.Fingerprint);
            if (File.Exists(receiptPath))
                return (await ReadDirectoryMoveReceiptAsync(receiptPath, instanceId, operation, fingerprint,
                    sourceScope.Fingerprint, destinationScope.Fingerprint, cancellationToken).ConfigureAwait(false)).AcceptedRevision;
            var stateScope = ReadDirectoryMoveScope(stateDirectory);
            if (sourceScope.Volume != destinationScope.Volume || sourceScope.Volume != stateScope.Volume)
                throw new InvalidDataException("CapabilityUnavailable");
            using var sourceParents = new DirectoryLease(Path.GetDirectoryName(source)!);
            using var destinationParents = new DirectoryLease(Path.GetDirectoryName(destination)!);
            using var sourceObserver = Native.Open(source, 0x80, FileShare.ReadWrite | FileShare.Delete);
            string observedGeneration = RevisionGeneration(sourceObserver);
            long observedUsn = AtomicDirectoryNative.ReadFileUsn(sourceObserver);
            using var transaction = AtomicDirectoryNative.Begin();
            string generation;
            using (var original = AtomicDirectoryNative.Open(source, transaction))
            {
                if (!original.IsDirectory) throw new InvalidDataException("ItemKindMismatch");
                if (await original.RevisionAsync(cancellationToken).ConfigureAwait(false) != operation.Preconditions.ExpectedRevision)
                    throw new InvalidDataException("RemoteConflict");
                generation = RevisionGeneration(original.Handle);
            }
            if (generation != observedGeneration || observedUsn <= 0)
                throw new InvalidDataException("RemoteConflict");
            var intent = new LocalDirectoryMoveIntent(1, instanceId, operation.OperationId, fingerprint, generation, observedUsn, string.Empty);
            intent = intent with { Checksum = DirectoryMoveIntentChecksum(intent) };
            string intentPath = DirectoryMoveIntentPath(stateDirectory, operation.OperationId);
            if (File.Exists(intentPath))
            {
                var retained = await ReadDirectoryMoveStateAsync<LocalDirectoryMoveIntent>(intentPath, cancellationToken).ConfigureAwait(false);
                if (retained.Version != 1 || retained.InstanceId != instanceId || retained.OperationId != operation.OperationId ||
                    retained.RequestFingerprint != fingerprint || retained.Checksum != DirectoryMoveIntentChecksum(retained))
                    throw new InvalidDataException("DurableDirectoryMoveIntentChanged");
                if (retained.SourceGeneration != generation || retained.SourceUsn != observedUsn)
                    throw new InvalidDataException("RemoteConflict");
                intent = retained;
            }
            else await WriteDirectoryMoveIntentAsync(intentPath, intent, cancellationToken).ConfigureAwait(false);
            await AtAsync(LocalDirectoryMoveCheckpoint.AfterSourceChecked, cancellationToken).ConfigureAwait(false);
            AtomicDirectoryNative.Move(source, destination, transaction);
            // Reopen after staging: TxF refuses this rename while the original directory's
            // transacted handle is open. A substituted object must roll back, not be adopted.
            using var moved = AtomicDirectoryNative.Open(destination, transaction);
            if (!moved.IsDirectory || RevisionGeneration(moved.Handle) != generation ||
                await moved.RevisionAsync(cancellationToken).ConfigureAwait(false) != operation.Preconditions.ExpectedRevision ||
                AtomicDirectoryNative.ReadFileUsn(sourceObserver) != intent.SourceUsn)
                throw new InvalidDataException("RemoteConflict");
            await AtAsync(LocalDirectoryMoveCheckpoint.AfterNativeStaged, cancellationToken).ConfigureAwait(false);
            var receipt = new LocalDirectoryMoveReceipt(1, instanceId, operation.OperationId, fingerprint,
                sourceScope.Fingerprint, destinationScope.Fingerprint, generation, AtomicDirectoryNative.Id(transaction),
                operation.Preconditions.ExpectedRevision, string.Empty);
            receipt = receipt with { Checksum = DirectoryMoveReceiptChecksum(receipt) };
            AtomicDirectoryNative.WriteReceipt(receiptPath, AdapterProtocolJson.Encode(receipt), transaction);
            await AtAsync(LocalDirectoryMoveCheckpoint.AfterReceiptStaged, cancellationToken).ConfigureAwait(false);
            if (AtomicDirectoryNative.ReadFileUsn(sourceObserver) != intent.SourceUsn)
                throw new InvalidDataException("RemoteConflict");
            cancellationToken.ThrowIfCancellationRequested();
            AtomicDirectoryNative.Commit(transaction);
            await AtAsync(LocalDirectoryMoveCheckpoint.AfterCommit, CancellationToken.None).ConfigureAwait(false);
            return receipt.AcceptedRevision;
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        { throw new InvalidDataException("RemoteConflict", error); }
        catch (NotSupportedException error) { throw new InvalidDataException("CapabilityUnavailable", error); }
        catch (Win32Exception error) { throw new NativeFileException(error.NativeErrorCode); }

        ValueTask AtAsync(LocalDirectoryMoveCheckpoint point, CancellationToken token) =>
            checkpoint?.Invoke(point, token) ?? ValueTask.CompletedTask;
    }

    private static string DirectoryMoveReceiptPath(string stateDirectory, Guid operationId) =>
        Path.Combine(stateDirectory, "directory-moves", operationId.ToString("N") + ".accepted.json");

    private static string DirectoryMoveIntentPath(string stateDirectory, Guid operationId) =>
        Path.Combine(stateDirectory, "directory-moves", operationId.ToString("N") + ".pending.json");

    private static bool ContainsPath(string candidate, string root)
    {
        candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Fingerprint, ulong Volume) ReadDirectoryMoveScope(string path)
    {
        using var handle = Native.Open(path, 0x80, FileShare.ReadWrite | FileShare.Delete);
        var information = Native.Information(handle);
        if ((information.Attributes & 0x410) != 0x10) throw new InvalidDataException("DurableDirectoryMoveScopeChanged");
        var identity = Native.Identity(handle);
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical + "\n" + RevisionGeneration(handle)))), identity.Volume);
    }

    private static string DirectoryMoveRequestFingerprint(Guid instanceId, AdapterOperationRequest operation, string sourceScope, string destinationScope) =>
        Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(new
        {
            version = 1,
            kind = "DirectoryMove",
            instanceId,
            operation.OperationId,
            operation.RootKey,
            operation.Path,
            operation.DestinationRootKey,
            operation.DestinationPath,
            expectedRevision = operation.Preconditions!.ExpectedRevision,
            mustBeAbsent = operation.Preconditions.DestinationMustBeAbsent,
            sourceScope,
            destinationScope
        })));

    private static string DirectoryMoveReceiptChecksum(LocalDirectoryMoveReceipt receipt) =>
        Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(receipt with { Checksum = string.Empty })));

    private static string DirectoryMoveIntentChecksum(LocalDirectoryMoveIntent intent) =>
        Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(intent with { Checksum = string.Empty })));

    private static async Task WriteDirectoryMoveIntentAsync(string path, LocalDirectoryMoveIntent intent, CancellationToken token)
    {
        string staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(AdapterProtocolJson.Encode(intent), token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(staged, path, overwrite: false);
        }
        finally { File.Delete(staged); }
    }

    private static async Task<T> ReadDirectoryMoveStateAsync<T>(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length is <= 0 or > 65536) throw new InvalidDataException("DurableDirectoryMoveStateInvalid");
        byte[] payload = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return AdapterProtocolJson.Decode<T>(payload);
    }

    private static async Task<LocalDirectoryMoveReceipt> ReadDirectoryMoveReceiptAsync(string path, Guid instanceId,
        AdapterOperationRequest operation, string fingerprint, string sourceScope, string destinationScope, CancellationToken token)
    {
        var receipt = await ReadDirectoryMoveStateAsync<LocalDirectoryMoveReceipt>(path, token).ConfigureAwait(false);
        if (receipt.Version != 1 || receipt.InstanceId == Guid.Empty || receipt.OperationId != operation.OperationId ||
            receipt.TransactionId == Guid.Empty || string.IsNullOrEmpty(receipt.SourceGeneration) ||
            string.IsNullOrEmpty(receipt.AcceptedRevision) ||
            !receipt.AcceptedRevision.StartsWith(receipt.SourceGeneration + ":directory:", StringComparison.Ordinal) ||
            receipt.Checksum != DirectoryMoveReceiptChecksum(receipt))
            throw new InvalidDataException("DurableDirectoryMoveReceiptChanged");
        if (receipt.InstanceId != instanceId || receipt.RequestFingerprint != fingerprint ||
            receipt.SourceScope != sourceScope || receipt.DestinationScope != destinationScope ||
            receipt.AcceptedRevision != operation.Preconditions!.ExpectedRevision)
            throw new InvalidDataException("OperationBindingMismatch");
        return receipt;
    }
}
