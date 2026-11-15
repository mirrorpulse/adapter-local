using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Local.Worker;

internal enum LocalDurableCopyCheckpoint { AfterIntent, AfterNativeStaged, AfterReceiptStaged, AfterCommit }

internal sealed record LocalDurableCopyContent(Stream Data, long Length, string Sha256);

internal static partial class LocalFileOperations
{
    private sealed record DurableCopyIntent(int Version, Guid InstanceId, Guid OperationId, string RequestFingerprint,
        string ParentFingerprint, string Checksum);

    private sealed record DurableCopyReceipt(int Version, Guid InstanceId, Guid OperationId, string RequestFingerprint,
        string RootKey, string TargetPath, string BackendScope, string StateScope,
        Guid TransactionId, string TargetGeneration, string AcceptedRevision, string Checksum);

    internal static async Task<string> ReadDurableCopyDirectoryRevisionAsync(Guid instanceId, LocalWorkerPaths paths,
        string rootKey, string path, string stateDirectory, Guid operationId, CancellationToken token)
    {
        string target = paths.ResolveAddress(path);
        using var targetGuard = new DirectoryLease(target);
        return await RequireDurableCopyDirectoryAsync(instanceId, paths, rootKey, path, target, stateDirectory, operationId, token)
            .ConfigureAwait(false);
    }

    private static async Task<string> RequireDurableCopyDirectoryAsync(Guid instanceId, LocalWorkerPaths paths, string rootKey,
        string relativePath, string fullPath, string stateDirectory, Guid operationId, CancellationToken token)
    {
        string receiptPath = DurableCopyStatePath(stateDirectory, operationId, ".accepted.json");
        using var ledger = new DirectoryLease(Path.GetDirectoryName(receiptPath)!);
        var receipt = await ReadDirectoryMoveStateAsync<DurableCopyReceipt>(receiptPath, token).ConfigureAwait(false);
        if (receipt.Version != 1 || receipt.InstanceId != instanceId || receipt.OperationId != operationId ||
            receipt.RootKey != rootKey || receipt.TargetPath != relativePath ||
            receipt.BackendScope != ReadDirectoryMoveScope(paths.Root).Fingerprint ||
            receipt.StateScope != ReadDirectoryMoveScope(stateDirectory).Fingerprint ||
            !IsValidCopyReceipt(receipt, null))
            throw new InvalidDataException("OperationBindingMismatch");
        using var current = ProtectedObject.TryOpen(fullPath, mutate: false);
        if (current is not { IsDirectory: true } || RevisionGeneration(current.Handle) != receipt.TargetGeneration)
            throw new InvalidDataException("RemoteConflict");
        return await current.RevisionAsync(token).ConfigureAwait(false);
    }

    internal static bool HasDurableCopyOwnership(string? stateDirectory, Guid operationId) => stateDirectory is not null &&
        (File.Exists(DurableCopyStatePath(stateDirectory, operationId, ".pending.json")) ||
         File.Exists(DurableCopyStatePath(stateDirectory, operationId, ".accepted.json")));

    internal static bool SupportsDurableCopy(LocalWorkerPaths paths, string stateDirectory)
    {
        if (ContainsPath(stateDirectory, paths.Root) || ContainsPath(paths.Root, stateDirectory)) return false;
        string? drive = Path.GetPathRoot(stateDirectory);
        if (drive is null || drive.Length != 3 || !drive.EndsWith(@":\", StringComparison.Ordinal)) return false;
        try
        {
            if (!string.Equals(new DriveInfo(drive).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) return false;
            using var backendGuard = new DirectoryLease(paths.Root);
            using var stateGuard = new DirectoryLease(stateDirectory);
            return ReadDirectoryMoveScope(paths.Root).Volume == ReadDirectoryMoveScope(stateDirectory).Volume;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Publishes an absent target and retains its exact acceptance in the same local transaction.</summary>
    /// <remarks>Recovery requires this Worker's original private intent or receipt. A later occupant is not a receipt.</remarks>
    internal static async Task<string> PublishDurableCopyAsync(Guid instanceId, LocalWorkerPaths paths,
        AdapterOperationRequest operation, string stateDirectory, LocalDurableCopyContent? content, bool recovering,
        Func<LocalDurableCopyCheckpoint, CancellationToken, ValueTask>? checkpoint = null, Guid? parentCopyOperationId = null,
        CancellationToken cancellationToken = default)
    {
        AdapterProtocolJson.ValidateMutation(operation, requiresDestination: false);
        if (instanceId == Guid.Empty || operation.OperationId == Guid.Empty || operation.IsDirectory == (content is not null) ||
            operation.DestinationRootKey is not null || operation.DestinationPath is not null ||
            parentCopyOperationId is { } parentId && (parentId == Guid.Empty || parentId == operation.OperationId) ||
            operation.Preconditions is not { ExpectedRevision: null, DestinationMustBeAbsent: true })
            throw new InvalidDataException("DurableCopyPreconditionsRequired");
        if (content is not null && (content.Length < 0 || !content.Data.CanRead || !content.Data.CanSeek ||
            content.Data.Length != content.Length || content.Sha256.Length != 64 ||
            !content.Sha256.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new InvalidDataException("DurableCopyContentInvalid");
        if (!Path.IsPathFullyQualified(stateDirectory) || !Directory.Exists(stateDirectory))
            throw new InvalidDataException("DurableCopyStateRequired");
        stateDirectory = Path.GetFullPath(stateDirectory);
        if (ContainsPath(stateDirectory, paths.Root) || ContainsPath(paths.Root, stateDirectory))
            throw new InvalidDataException("DurableCopyStateMustBePrivate");
        cancellationToken.ThrowIfCancellationRequested();

        string destination = paths.ResolveAddress(operation.Path);
        string intentPath = DurableCopyStatePath(stateDirectory, operation.OperationId, ".pending.json");
        string receiptPath = DurableCopyStatePath(stateDirectory, operation.OperationId, ".accepted.json");
        string ledger = Path.GetDirectoryName(intentPath)!;
        try
        {
            using var backendRoot = new DirectoryLease(paths.Root);
            using var stateRoot = new DirectoryLease(stateDirectory);
            var backendScope = ReadDirectoryMoveScope(paths.Root);
            var stateScope = ReadDirectoryMoveScope(stateDirectory);
            if (backendScope.Volume != stateScope.Volume) throw new InvalidDataException("CapabilityUnavailable");
            if (!Directory.Exists(ledger))
            {
                if (recovering) throw new InvalidDataException("MutationOutcomeAmbiguous");
                Directory.CreateDirectory(ledger);
            }
            using var ledgerGuard = new DirectoryLease(ledger);
            using var owner = new FileStream(DurableCopyStatePath(stateDirectory, operation.OperationId, ".owner"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            string request = Digest(new
            {
                version = 1,
                instanceId,
                operation.OperationId,
                operation.RootKey,
                operation.Path,
                operation.IsDirectory,
                parentCopyOperationId,
                backend = backendScope.Fingerprint,
                state = stateScope.Fingerprint,
                length = content?.Length,
                digest = content?.Sha256
            });
            if (File.Exists(receiptPath))
            {
                var retained = await ReadDirectoryMoveStateAsync<DurableCopyReceipt>(receiptPath, cancellationToken).ConfigureAwait(false);
                if (retained.Version != 1 || retained.InstanceId != instanceId || retained.OperationId != operation.OperationId ||
                    retained.RequestFingerprint != request || !IsValidCopyReceipt(retained, content))
                    throw new InvalidDataException("OperationBindingMismatch");
                return retained.AcceptedRevision;
            }

            string parent = Path.GetDirectoryName(destination)!;
            using var parentGuard = new DirectoryLease(parent);
            if (parentCopyOperationId is { } copy)
            {
                string parentPath = Path.GetRelativePath(paths.Root, parent).Replace(Path.DirectorySeparatorChar, '/');
                if (parentPath == ".") throw new InvalidDataException("DurableCopyParentRequired");
                _ = await RequireDurableCopyDirectoryAsync(instanceId, paths, operation.RootKey, parentPath, parent, stateDirectory,
                    copy, cancellationToken).ConfigureAwait(false);
            }
            EnsureNoRecovery(paths, operation);
            var observedParent = ReadDirectoryMoveScope(parent);
            if (observedParent.Volume != stateScope.Volume) throw new InvalidDataException("CapabilityUnavailable");
            var intent = new DurableCopyIntent(1, instanceId, operation.OperationId, request, observedParent.Fingerprint, string.Empty);
            intent = intent with { Checksum = Digest(intent) };
            if (File.Exists(intentPath))
            {
                var retained = await ReadDirectoryMoveStateAsync<DurableCopyIntent>(intentPath, cancellationToken).ConfigureAwait(false);
                if (retained != intent) throw new InvalidDataException("OperationBindingMismatch");
            }
            else
            {
                if (recovering) throw new InvalidDataException("MutationOutcomeAmbiguous");
                await WriteDurableIntentAsync(intentPath, intent, cancellationToken).ConfigureAwait(false);
            }
            await AtAsync(LocalDurableCopyCheckpoint.AfterIntent, cancellationToken).ConfigureAwait(false);
            // Upload size determines the copy time. The Worker owns cancellation and
            // closing the transaction before commit rolls back the target and receipt.
            using var transaction = content is null ? AtomicDirectoryNative.Begin() : AtomicDirectoryNative.Begin(timeoutMilliseconds: 0);
            if (content is null)
                AtomicDirectoryNative.CreateDirectory(destination, transaction);
            else
            {
                using var output = AtomicDirectoryNative.CreateFile(destination, transaction, "CreateCopyTransacted");
                content.Data.Position = 0;
                await content.Data.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            string generation;
            string revision;
            using (var created = AtomicDirectoryNative.Open(destination, transaction))
            {
                generation = RevisionGeneration(created.Handle);
                revision = await created.RevisionAsync(cancellationToken).ConfigureAwait(false);
                if (created.IsDirectory != operation.IsDirectory || !MatchesCopyRevision(generation, revision, content))
                    throw new InvalidDataException("DurableCopyContentChanged");
            }
            await AtAsync(LocalDurableCopyCheckpoint.AfterNativeStaged, cancellationToken).ConfigureAwait(false);
            var receipt = new DurableCopyReceipt(1, instanceId, operation.OperationId, request,
                operation.RootKey, operation.Path, backendScope.Fingerprint, stateScope.Fingerprint,
                AtomicDirectoryNative.Id(transaction), generation, revision, string.Empty);
            receipt = receipt with { Checksum = Digest(receipt) };
            AtomicDirectoryNative.WriteReceipt(receiptPath, AdapterProtocolJson.Encode(receipt), transaction);
            AtomicDirectoryNative.Remove(intentPath, directory: false, transaction);
            await AtAsync(LocalDurableCopyCheckpoint.AfterReceiptStaged, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            AtomicDirectoryNative.Commit(transaction);
            await AtAsync(LocalDurableCopyCheckpoint.AfterCommit, CancellationToken.None).ConfigureAwait(false);
            return revision;
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3 or 80 or 183)
        { throw new InvalidDataException("RemoteConflict", error); }
        catch (NotSupportedException error) { throw new InvalidDataException("CapabilityUnavailable", error); }
        catch (Win32Exception error) { throw new NativeFileException(error.NativeErrorCode); }

        ValueTask AtAsync(LocalDurableCopyCheckpoint point, CancellationToken token) =>
            checkpoint?.Invoke(point, token) ?? ValueTask.CompletedTask;
    }

    private static bool MatchesCopyRevision(string generation, string revision, LocalDurableCopyContent? content) =>
        !string.IsNullOrEmpty(generation) && !string.IsNullOrEmpty(revision) &&
        (content is null
            ? revision.StartsWith(generation + ":directory:", StringComparison.Ordinal)
            : revision.StartsWith(generation + ":file:" + content.Length.ToString(CultureInfo.InvariantCulture) + ":", StringComparison.Ordinal) &&
                revision.EndsWith(":" + content.Sha256, StringComparison.Ordinal));

    private static bool IsValidCopyReceipt(DurableCopyReceipt receipt, LocalDurableCopyContent? content) =>
        receipt.TransactionId != Guid.Empty && receipt.Checksum == Digest(receipt with { Checksum = string.Empty }) &&
        MatchesCopyRevision(receipt.TargetGeneration, receipt.AcceptedRevision, content);

    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(value)));

    private static string DurableCopyStatePath(string stateDirectory, Guid operationId, string suffix) =>
        Path.Combine(stateDirectory, "directory-copies", operationId.ToString("N") + suffix);
}
