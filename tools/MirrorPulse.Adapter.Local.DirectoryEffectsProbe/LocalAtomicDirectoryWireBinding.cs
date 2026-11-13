using System.Security.Cryptography;
using System.Text;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Local.Worker;

internal sealed record LocalAtomicDirectoryOriginalParent(string Path, string NativeIdentity, string OriginalGeneration,
    string OriginalRevision, long OriginalFileUsn, string? ParentGeneration);
internal sealed record LocalAtomicDirectoryWireBinding(string RequestFingerprint, IReadOnlyList<AdapterOperationRequest> Operations,
    LocalAtomicDirectoryOriginalParent Parent);
internal sealed record LocalAtomicDirectorySdkMember(Guid OperationId, string OriginalGeneration,
    AdapterDirectoryGenerationWitness? Directory, IReadOnlyList<AdapterDirectoryGenerationWitness> OriginalParents);
internal sealed record LocalAtomicDirectorySdkAdmission(Guid InstanceId, Guid AdmissionId, string RequestFingerprint,
    IReadOnlyList<LocalAtomicDirectorySdkMember> Operations);

internal static partial class LocalFileOperations
{
    private static void RequireBoundRequest(Guid instanceId, Guid admissionId, string rootKey, string relative,
        IReadOnlyDictionary<string, Guid> operations, IReadOnlyList<AdapterOperationRequest> requests, string? fingerprint)
    {
        if (requests is not { Count: >= 1 and <= 2048 } || requests.Count != operations.Count ||
            requests[^1] is not { IsDirectory: true } root || root.Path != relative || root.RootKey != rootKey)
            throw new InvalidDataException("CompleteOriginalDirectoryRequestRequired");
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var ids = new HashSet<Guid>();
        for (int ordinal = 0; ordinal < requests.Count; ordinal++)
        {
            var request = requests[ordinal];
            if (request is null) throw new InvalidDataException("OriginalDirectoryOperationRequired");
            AdapterProtocolJson.ValidateMutation(request, requiresDestination: false);
            if (request.RootKey != rootKey || request.Path.Length == 0 || request.DestinationPath is not null || request.DestinationRootKey is not null ||
                request.Preconditions is not { DestinationMustBeAbsent: true, ExpectedRevision: { Length: > 0 } } ||
                !operations.TryGetValue(request.Path, out Guid operation) || operation != request.OperationId || !ids.Add(operation) ||
                !positions.TryAdd(request.Path, ordinal))
                throw new InvalidDataException("OriginalDirectoryConditionalRequestRequired");
        }
        for (int ordinal = 0; ordinal < requests.Count - 1; ordinal++)
        {
            if (!positions.TryGetValue(ParentPath(requests[ordinal].Path), out int parent) || parent <= ordinal || !requests[parent].IsDirectory)
                throw new InvalidDataException("OriginalDirectoryLeafFirstRequestRequired");
        }
        string actual = Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(new
        {
            Version = 1,
            InstanceId = instanceId,
            AdmissionId = admissionId,
            Operations = requests.Select(operation => new { OperationKind = "Delete", Operation = operation }).ToArray(),
        })));
        if (actual != fingerprint) throw new InvalidDataException("OriginalDirectoryRequestFingerprintChanged");
    }

    private static async Task<LocalAtomicDirectoryWireBinding> CaptureWireBindingAsync(LocalWorkerPaths paths,
        IReadOnlyList<AdapterOperationRequest> requests, string requestFingerprint, LocalDirectoryTreeObservation observed, CancellationToken token)
    {
        var entries = observed.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        foreach (var request in requests)
        {
            if (!entries.TryGetValue(request.Path, out var original) || original.IsDirectory != request.IsDirectory ||
                original.Revision != request.Preconditions!.ExpectedRevision)
                throw new InvalidDataException("OriginalDirectoryRequestConditionChanged");
        }
        string parentPath = ParentPath(observed.Path);
        using var ancestors = new DirectoryLease(Path.GetDirectoryName(paths.ResolveDirectory(parentPath))!);
        using var parent = ProtectedObject.TryOpen(paths.ResolveDirectory(parentPath), mutate: false) ?? throw new DirectoryNotFoundException();
        var before = Native.Information(parent.Handle);
        if (!parent.IsDirectory || Identity(before) != observed.ParentNativeIdentity)
            throw new InvalidDataException("OriginalDirectoryParentChanged");
        string revision = await parent.RevisionAsync(token).ConfigureAwait(false);
        long usn = AtomicDirectoryNative.ReadFileUsn(parent.Handle);
        string? grandparentGeneration = null;
        if (parentPath.Length > 0)
        {
            using var grandparent = ProtectedObject.TryOpen(paths.ResolveDirectory(ParentPath(parentPath)), mutate: false)
                ?? throw new DirectoryNotFoundException();
            grandparentGeneration = RevisionGeneration(grandparent.Handle);
        }
        RequireSame(before, Native.Information(parent.Handle));
        if (usn <= 0) throw new InvalidDataException("OriginalDirectoryParentJournalRequired");
        return new(requestFingerprint, requests, new(parentPath, Identity(before), RevisionGeneration(parent.Handle), revision, usn, grandparentGeneration));
    }

    private static void RequireWireBinding(LocalAtomicDirectoryAdmission admission)
    {
        if (admission.Version != 4 || admission.History is null || admission.WireBinding is not { Parent: { } parent } binding ||
            admission.Fingerprint != AtomicFingerprint(admission) || parent.Path != ParentPath(admission.Original.Path) ||
            parent.NativeIdentity != admission.Original.ParentNativeIdentity || parent.OriginalFileUsn <= 0 ||
            string.IsNullOrWhiteSpace(parent.OriginalGeneration) || string.IsNullOrWhiteSpace(parent.OriginalRevision) ||
            parent.Path.Length > 0 && string.IsNullOrWhiteSpace(parent.ParentGeneration) || parent.Path.Length == 0 && parent.ParentGeneration is not null ||
            admission.Entries is not { Count: >= 1 and <= 2048 } || admission.Entries.Any(value => string.IsNullOrWhiteSpace(value.OriginalGeneration)))
            throw new InvalidDataException("OriginalBoundDirectoryAdmissionRequired");
        var entries = admission.Entries.ToDictionary(entry => entry.OperationId);
        var operations = admission.Entries.ToDictionary(entry => entry.Original.Path, entry => entry.OperationId, StringComparer.Ordinal);
        RequireBoundRequest(admission.InstanceId, admission.AdmissionId, admission.Original.RootKey, admission.Original.Path,
            operations, binding.Operations, binding.RequestFingerprint);
        foreach (var request in binding.Operations)
        {
            var original = entries[request.OperationId].Original;
            if (original.Path != request.Path || original.IsDirectory != request.IsDirectory || original.Revision != request.Preconditions!.ExpectedRevision)
                throw new InvalidDataException("OriginalBoundDirectoryConditionChanged");
        }
    }

    /// <summary>Materializes immutable SDK 0.3 witness types from the retained native binding, without reading current occupants.</summary>
    internal static LocalAtomicDirectorySdkAdmission ReadAtomicDirectorySdkAdmission(LocalAtomicDirectoryAdmission admission)
    {
        RequireWireBinding(admission);
        var binding = admission.WireBinding!;
        var generations = admission.Entries.ToDictionary(entry => entry.Original.Path, entry => entry.OriginalGeneration!, StringComparer.Ordinal);
        generations.Add(binding.Parent.Path, binding.Parent.OriginalGeneration);
        var directories = new Dictionary<string, AdapterDirectoryGenerationWitness>(StringComparer.Ordinal);
        foreach (var entry in admission.Entries.Where(entry => entry.Original.IsDirectory))
            directories.Add(entry.Original.Path, Witness(entry.Original.Path, entry.OriginalGeneration!, entry.Original.Revision,
                entry.OriginalFileUsn, generations[ParentPath(entry.Original.Path)]));
        directories.Add(binding.Parent.Path, Witness(binding.Parent.Path, binding.Parent.OriginalGeneration, binding.Parent.OriginalRevision,
            binding.Parent.OriginalFileUsn, binding.Parent.ParentGeneration));
        var entries = admission.Entries.ToDictionary(entry => entry.OperationId);
        return new(admission.InstanceId, admission.AdmissionId, binding.RequestFingerprint, binding.Operations.Select(operation =>
        {
            var entry = entries[operation.OperationId];
            return new LocalAtomicDirectorySdkMember(operation.OperationId, entry.OriginalGeneration!,
                operation.IsDirectory ? directories[operation.Path] : null, [directories[ParentPath(operation.Path)]]);
        }).ToArray());

        AdapterDirectoryGenerationWitness Witness(string path, string generation, string revision, long usn, string? parentGeneration)
        {
            byte[] id = SHA256.HashData(Encoding.UTF8.GetBytes("local-original-directory-witness-v1\0" + admission.Fingerprint + "\0" + path));
            id[0] |= 1;
            var witness = new AdapterDirectoryGenerationWitness(1, admission.InstanceId, admission.AdmissionId, new Guid(id.AsSpan(0, 16)),
                new(admission.Original.RootKey, path), generation, revision, parentGeneration, binding.RequestFingerprint,
                Convert.ToBase64String(AdapterProtocolJson.Encode(new
                {
                    Version = 1,
                    NativeAdmissionFingerprint = admission.Fingerprint,
                    Path = path,
                    OriginalGeneration = generation,
                    OriginalRevision = revision,
                    OriginalFileUsn = usn
                })));
            witness.Validate();
            return witness;
        }
    }

    private static IEnumerable<LocalAtomicDirectoryEntry> AtomicExecutionOrder(LocalAtomicDirectoryAdmission admission)
    {
        if (admission.WireBinding is not { } binding)
            return admission.Entries.OrderByDescending(entry => entry.Original.Path.Count(character => character == '/'))
                .ThenBy(entry => entry.Original.Path, StringComparer.Ordinal);
        var entries = admission.Entries.ToDictionary(entry => entry.OperationId);
        return binding.Operations.Select(operation => entries[operation.OperationId]);
    }

    private static string ParentPath(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }
}
