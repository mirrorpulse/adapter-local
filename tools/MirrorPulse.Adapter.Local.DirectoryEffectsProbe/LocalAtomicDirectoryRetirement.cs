using System.Collections.ObjectModel;
using System.Security.Cryptography;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Local.Worker;

internal sealed record LocalAtomicDirectoryEntry(Guid OperationId, LocalDirectoryTreeEntry Original, long OriginalFileUsn);
internal sealed record LocalAtomicDirectoryAdmission(int Version, Guid InstanceId, Guid AdmissionId, string SourceRootIdentity,
    string StateDirectoryIdentity, LocalDirectoryTreeObservation Original, IReadOnlyList<LocalAtomicDirectoryEntry> Entries, string Fingerprint);
internal sealed record LocalAtomicDirectoryEffect(Guid OperationId, string Path, string NativeIdentity, long OriginalFileUsn, string OriginalRevision);
internal sealed record LocalAtomicDirectoryReceipt(int Version, Guid InstanceId, Guid AdmissionId, Guid TransactionId,
    string AdmissionFingerprint, IReadOnlyList<LocalAtomicDirectoryEffect> Effects, DateTimeOffset PreparedAt);
internal enum LocalAtomicDirectoryCheckpoint { AfterOriginalsLocked, AfterFirstChildStaged, AfterParentStaged, AfterReceiptStaged, AfterCommit }

internal static partial class LocalFileOperations
{
    private const int MaximumAtomicEvidenceBytes = 16 * 1024 * 1024;

    /// <summary>Prepares original IDs and file USNs in an explicitly granted persistent directory, without mutating the source.</summary>
    /// <remarks>This experimental NTFS backend is never inferred from ordinary Local or protocol-v2 support.</remarks>
    internal static async Task<LocalAtomicDirectoryAdmission> PrepareAtomicDirectoryRetirementAsync(LocalWorkerPaths paths,
        Guid instanceId, Guid admissionId, string rootKey, string relative, IReadOnlyDictionary<string, Guid> originalOperations,
        string stateDirectory, bool enableExperimentalNtfsTransactions, CancellationToken token)
    {
        RequireExperimentalAtomicDirectoryBackend(enableExperimentalNtfsTransactions);
        RequireOwnedProbeFixture(paths);
        if (instanceId == Guid.Empty || admissionId == Guid.Empty) throw new InvalidDataException("DirectoryAdmissionRequired");
        ArgumentNullException.ThrowIfNull(originalOperations);
        string state = RequireAtomicStateDirectory(paths, stateDirectory);
        using var stateAncestors = new DirectoryLease(state);
        var observed = await ObserveDirectoryTreeAsync(paths, admissionId, rootKey, relative, token).ConfigureAwait(false);
        if (originalOperations.Count != observed.Entries.Count || originalOperations.Values.Any(value => value == Guid.Empty) ||
            originalOperations.Values.Distinct().Count() != originalOperations.Count ||
            originalOperations.Keys.Except(observed.Entries.Select(entry => entry.Path), StringComparer.Ordinal).Any())
            throw new InvalidDataException("CompleteOriginalDirectoryOperationsRequired");
        using var root = ProtectedObject.TryOpen(paths.ResolveDirectory(string.Empty), mutate: false) ?? throw new DirectoryNotFoundException();
        using var stateObject = ProtectedObject.TryOpen(state, mutate: false) ?? throw new DirectoryNotFoundException();
        var rootInfo = Native.Information(root.Handle); var stateInfo = Native.Information(stateObject.Handle);
        if (rootInfo.Volume != stateInfo.Volume) throw new NotSupportedException("DirectoryEffectStateRequiresSameVolume");
        var entries = new List<LocalAtomicDirectoryEntry>(observed.Entries.Count);
        foreach (var entry in observed.Entries)
        {
            token.ThrowIfCancellationRequested();
            using var item = ProtectedObject.TryOpen(paths.ResolveDirectory(entry.Path), mutate: false)
                ?? throw new FileNotFoundException("An original directory member disappeared before its journal observation.");
            var before = Native.Information(item.Handle);
            if (Identity(before) != entry.NativeIdentity || item.IsDirectory != entry.IsDirectory ||
                await item.RevisionAsync(token).ConfigureAwait(false) != entry.Revision)
                throw new InvalidDataException("OriginalDirectoryObservationChanged");
            long usn = AtomicDirectoryNative.ReadFileUsn(item.Handle);
            RequireSame(before, Native.Information(item.Handle));
            if (usn <= 0) throw new NotSupportedException("DirectoryEffectsRequireOriginalFileJournalSequence");
            entries.Add(new(originalOperations[entry.Path], entry, usn));
        }
        var admission = new LocalAtomicDirectoryAdmission(1, instanceId, admissionId, Identity(rootInfo), Identity(stateInfo),
            observed, new ReadOnlyCollection<LocalAtomicDirectoryEntry>(entries), string.Empty);
        admission = admission with { Fingerprint = AtomicFingerprint(admission) };
        byte[] payload = AtomicPayload(admission);
        await WriteOriginalAtomicAdmissionAsync(Path.Combine(state, admissionId.ToString("N") + ".admission.json"), payload, token).ConfigureAwait(false);
        return admission;
    }

    /// <summary>Characterizes leaf-first removals and a receipt in one NTFS transaction inside an owned disposable fixture.</summary>
    /// <remarks>Not a production effect or SDK receipt: directory USNs do not establish complete original-parent history.</remarks>
    internal static async Task<LocalAtomicDirectoryReceipt> RetireAtomicDirectoryAsync(LocalWorkerPaths paths,
        LocalAtomicDirectoryAdmission admission, string stateDirectory, bool enableExperimentalNtfsTransactions,
        Func<LocalAtomicDirectoryCheckpoint, CancellationToken, ValueTask>? checkpoint, CancellationToken token)
    {
        RequireExperimentalAtomicDirectoryBackend(enableExperimentalNtfsTransactions);
        RequireOwnedProbeFixture(paths);
        ArgumentNullException.ThrowIfNull(admission);
        if (admission.Version != 1 || admission.InstanceId == Guid.Empty || admission.AdmissionId == Guid.Empty ||
            admission.Entries is not { Count: >= 1 and <= 2048 } || admission.Fingerprint != AtomicFingerprint(admission))
            throw new InvalidDataException("OriginalAtomicDirectoryAdmissionRequired");
        new AdapterFileAddress(admission.Original.RootKey, admission.Original.Path).Validate();
        if (admission.Original.Path.Length == 0 || admission.Original.AdmissionId != admission.AdmissionId)
            throw new InvalidDataException("RootMutationForbidden");
        string state = RequireAtomicStateDirectory(paths, stateDirectory);
        using var stateAncestors = new DirectoryLease(state);
        string admissionPath = Path.Combine(state, admission.AdmissionId.ToString("N") + ".admission.json");
        byte[] retained = await ReadAtomicEvidenceAsync(admissionPath, token).ConfigureAwait(false);
        if (!retained.AsSpan().SequenceEqual(AtomicPayload(admission))) throw new InvalidDataException("OriginalAtomicDirectoryAdmissionChanged");
        string receiptPath = Path.Combine(state, admission.AdmissionId.ToString("N") + ".committed.json");
        using var root = ProtectedObject.TryOpen(paths.ResolveDirectory(string.Empty), mutate: false) ?? throw new DirectoryNotFoundException();
        using var stateObject = ProtectedObject.TryOpen(state, mutate: false) ?? throw new DirectoryNotFoundException();
        var sourceRootInfo = Native.Information(root.Handle); var stateInfo = Native.Information(stateObject.Handle);
        if (Identity(sourceRootInfo) != admission.SourceRootIdentity || Identity(stateInfo) != admission.StateDirectoryIdentity ||
            sourceRootInfo.Volume != stateInfo.Volume) throw new InvalidDataException("AtomicDirectorySourceOrStateGenerationChanged");
        // A matching committed receipt is historical evidence only. In particular,
        // replay never opens or mutates a later tree at the same source path.
        if (File.Exists(receiptPath)) return ReadAtomicReceipt(admission, await ReadAtomicEvidenceAsync(receiptPath, token).ConfigureAwait(false));
        token.ThrowIfCancellationRequested();
        string source = paths.ResolveDirectory(admission.Original.Path);
        using var sourceAncestors = new DirectoryLease(Path.GetDirectoryName(source)!);
        using var originalParent = ProtectedObject.TryOpen(Path.GetDirectoryName(source)!, mutate: false) ?? throw new DirectoryNotFoundException();
        if (Identity(Native.Information(originalParent.Handle)) != admission.Original.ParentNativeIdentity)
            throw new InvalidDataException("OriginalAtomicDirectoryParentChanged");
        using var transaction = AtomicDirectoryNative.Begin();
        var opened = new Dictionary<string, AtomicDirectoryNative.Item>(StringComparer.Ordinal);
        var originalDirectories = new Dictionary<string, Microsoft.Win32.SafeHandles.SafeFileHandle>(StringComparer.Ordinal);
        var originalChangeTimes = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            foreach (var entry in admission.Entries.OrderBy(entry => entry.Original.Path, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (entry.Original.IsDirectory)
                {
                    var original = Native.Open(paths.ResolveDirectory(entry.Original.Path), 0x80000000,
                        FileShare.ReadWrite | FileShare.Delete);
                    originalDirectories.Add(entry.Original.Path, original);
                    originalChangeTimes.Add(entry.Original.Path, AtomicDirectoryNative.ReadChangeTime(original));
                    if (Identity(Native.Information(original)) != entry.Original.NativeIdentity ||
                        AtomicDirectoryNative.ReadFileUsn(original) != entry.OriginalFileUsn)
                        throw new InvalidDataException("OriginalAtomicDirectoryGenerationChanged");
                }
                var item = AtomicDirectoryNative.Open(paths.ResolveDirectory(entry.Original.Path), transaction);
                opened.Add(entry.Original.Path, item);
                var info = Native.Information(item.Handle);
                if (Identity(info) != entry.Original.NativeIdentity || item.IsDirectory != entry.Original.IsDirectory ||
                    !item.IsDirectory && info.Links != 1 || await item.RevisionAsync(token).ConfigureAwait(false) != entry.Original.Revision ||
                    AtomicDirectoryNative.ReadFileUsn(item.Handle) != entry.OriginalFileUsn)
                    throw new InvalidDataException("OriginalAtomicDirectoryGenerationOrRevisionChanged");
            }
            var actual = ReadAtomicTreeInventory(source, admission.Original.Path, token);
            if (!actual.SetEquals(admission.Entries.Select(entry => entry.Original.Path)))
                throw new InvalidDataException("OriginalAtomicDirectoryInventoryChanged");
            await CheckpointAsync(LocalAtomicDirectoryCheckpoint.AfterOriginalsLocked, token).ConfigureAwait(false);
            var effects = new List<LocalAtomicDirectoryEffect>(admission.Entries.Count);
            foreach (var entry in admission.Entries.OrderByDescending(entry => entry.Original.Path.Count(character => character == '/'))
                .ThenBy(entry => entry.Original.Path, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                string path = paths.ResolveDirectory(entry.Original.Path);
                AtomicDirectoryNative.Remove(path, entry.Original.IsDirectory, transaction);
                opened[entry.Original.Path].Dispose(); opened.Remove(entry.Original.Path);
                effects.Add(new(entry.OperationId, entry.Original.Path, entry.Original.NativeIdentity, entry.OriginalFileUsn, entry.Original.Revision));
                if (effects.Count == 1) await CheckpointAsync(LocalAtomicDirectoryCheckpoint.AfterFirstChildStaged, token).ConfigureAwait(false);
            }
            await CheckpointAsync(LocalAtomicDirectoryCheckpoint.AfterParentStaged, token).ConfigureAwait(false);
            // Transacted directory handles do not exclude external child creation.
            // Once every original parent is staged, new names are excluded. Check
            // the retained nontransacted objects then, before producing a receipt,
            // so even an already-removed unowned child cannot be adopted.
            foreach (var entry in admission.Entries.Where(entry => entry.Original.IsDirectory))
                if (AtomicDirectoryNative.ReadFileUsn(originalDirectories[entry.Original.Path]) != entry.OriginalFileUsn ||
                    AtomicDirectoryNative.ReadChangeTime(originalDirectories[entry.Original.Path]) != originalChangeTimes[entry.Original.Path])
                    throw new InvalidDataException("UnownedOriginalDirectoryJournalChange");
            var receipt = new LocalAtomicDirectoryReceipt(1, admission.InstanceId, admission.AdmissionId, AtomicDirectoryNative.Id(transaction),
                admission.Fingerprint, new ReadOnlyCollection<LocalAtomicDirectoryEffect>(effects), DateTimeOffset.UtcNow);
            AtomicDirectoryNative.WriteReceipt(receiptPath, AtomicPayload(receipt), transaction);
            await CheckpointAsync(LocalAtomicDirectoryCheckpoint.AfterReceiptStaged, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            AtomicDirectoryNative.Commit(transaction);
            // Once committed, cancellation or a lost reply cannot roll back the
            // source. Both effect and receipt already share their durable boundary.
            await CheckpointAsync(LocalAtomicDirectoryCheckpoint.AfterCommit, CancellationToken.None).ConfigureAwait(false);
            var readback = ReadAtomicReceipt(admission, await ReadAtomicEvidenceAsync(receiptPath, CancellationToken.None).ConfigureAwait(false));
            if (!AtomicPayload(readback).AsSpan().SequenceEqual(AtomicPayload(receipt)))
                throw new InvalidDataException("CommittedAtomicDirectoryReceiptChanged");
            return readback;
        }
        finally
        {
            foreach (var item in opened.Values) item.Dispose();
            foreach (var original in originalDirectories.Values) original.Dispose();
        }

        ValueTask CheckpointAsync(LocalAtomicDirectoryCheckpoint point, CancellationToken stop) => checkpoint?.Invoke(point, stop) ?? ValueTask.CompletedTask;
    }

    private static HashSet<string> ReadAtomicTreeInventory(string path, string relative, CancellationToken token)
    {
        var names = new HashSet<string>(StringComparer.Ordinal); Read(path, relative, 0); return names;
        void Read(string current, string name, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 64 || names.Count >= 2048 || !names.Add(name)) throw new InvalidDataException("DirectoryObservationInventoryBoundExceeded");
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("ReparsePointForbidden");
            if (!Directory.Exists(current)) return;
            foreach (string child in Directory.EnumerateFileSystemEntries(current)) Read(child, name + "/" + Path.GetFileName(child), depth + 1);
        }
    }

    private static void RequireExperimentalAtomicDirectoryBackend(bool enabled)
    {
        if (!enabled || !OperatingSystem.IsWindows()) throw new NotSupportedException("ExperimentalNtfsDirectoryEffectsNotEnabled");
    }

    private static void RequireOwnedProbeFixture(LocalWorkerPaths paths)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Root));
        string fixture = Path.GetDirectoryName(source) ?? throw new InvalidDataException("OwnedAtomicProbeFixtureRequired");
        string name = Path.GetFileName(fixture); const string prefix = "mp-local-atomic-";
        string marker = Path.Combine(fixture, ".mp-atomic-test-fixture");
        if (Path.GetFileName(source) != "source" ||
            !string.Equals(Path.GetDirectoryName(fixture), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
            !File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(marker).Length > 256 || File.ReadAllText(marker) != name)
            throw new InvalidDataException("OwnedAtomicProbeFixtureRequired");
    }

    private static string RequireAtomicStateDirectory(LocalWorkerPaths paths, string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        string state = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDirectory));
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.ResolveDirectory(string.Empty)));
        if (!Directory.Exists(state) || string.Equals(state, source, StringComparison.OrdinalIgnoreCase) ||
            state.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(state + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(state) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("SeparateExplicitDirectoryEffectStateGrantRequired");
        return state;
    }

    private static string AtomicFingerprint(LocalAtomicDirectoryAdmission admission) =>
        Convert.ToHexString(SHA256.HashData(AtomicPayload(admission with { Fingerprint = string.Empty })));

    private static byte[] AtomicPayload<T>(T value)
    {
        byte[] payload = AdapterProtocolJson.Encode(value);
        if (payload.Length is 0 or > MaximumAtomicEvidenceBytes) throw new InvalidDataException("AtomicDirectoryEvidenceBoundExceeded");
        return payload;
    }

    private static async Task WriteOriginalAtomicAdmissionAsync(string path, byte[] payload, CancellationToken token)
    {
        if (File.Exists(path))
        {
            if (!(await ReadAtomicEvidenceAsync(path, token).ConfigureAwait(false)).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("AtomicDirectoryAdmissionIdAlreadyOwned");
            return;
        }
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        await stream.WriteAsync(payload, token).ConfigureAwait(false); stream.Flush(true);
    }

    private static async Task<byte[]> ReadAtomicEvidenceAsync(string path, CancellationToken token)
    {
        using var handle = Native.Open(path, 0x80000000, FileShare.Read);
        var information = Native.Information(handle);
        if ((information.Attributes & 0x410) != 0 || information.Links != 1) throw new InvalidDataException("AtomicDirectoryEvidenceRequiresOriginalRegularFile");
        await using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        if (stream.Length is 0 or > MaximumAtomicEvidenceBytes) throw new InvalidDataException("AtomicDirectoryEvidenceBoundExceeded");
        byte[] bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false); return bytes;
    }

    private static LocalAtomicDirectoryReceipt ReadAtomicReceipt(LocalAtomicDirectoryAdmission admission, byte[] payload)
    {
        var receipt = AdapterProtocolJson.Decode<LocalAtomicDirectoryReceipt>(payload);
        if (receipt.Version != 1 || receipt.InstanceId != admission.InstanceId || receipt.AdmissionId != admission.AdmissionId ||
            receipt.TransactionId == Guid.Empty || receipt.AdmissionFingerprint != admission.Fingerprint || receipt.Effects is null ||
            receipt.Effects.Count != admission.Entries.Count || receipt.PreparedAt == default)
            throw new InvalidDataException("CommittedOriginalAtomicDirectoryReceiptRequired");
        var expected = admission.Entries.OrderByDescending(entry => entry.Original.Path.Count(character => character == '/'))
            .ThenBy(entry => entry.Original.Path, StringComparer.Ordinal)
            .Select(entry => new LocalAtomicDirectoryEffect(entry.OperationId, entry.Original.Path, entry.Original.NativeIdentity, entry.OriginalFileUsn, entry.Original.Revision));
        if (!expected.SequenceEqual(receipt.Effects)) throw new InvalidDataException("CommittedOriginalAtomicDirectoryEffectsChanged");
        return receipt;
    }
}
