#if LOCAL_ATOMIC_DIRECTORY_CANDIDATE
using System.Security.Cryptography;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Local.Worker;

/// <summary>Adapts the qualified native fixture transaction to the candidate SDK on the real Worker pipe.</summary>
internal sealed class LocalAtomicDirectoryBackend(LocalWorkerRoots roots, string stateDirectory) :
    IAdapterOriginalDirectoryAdmissionBackend, IAdapterAtomicDirectoryDeletionBackend
{
    internal static bool IsEnabled(string? stateDirectory) =>
        !string.IsNullOrWhiteSpace(stateDirectory) && Path.IsPathFullyQualified(stateDirectory) && Directory.Exists(stateDirectory);

    public async ValueTask<AdapterDirectoryAdmission> AdmitAsync(AdapterDirectoryAdmissionRequest request, CancellationToken cancellationToken)
    {
        var (_, original) = await LoadAsync(request, requireRetained: false, cancellationToken).ConfigureAwait(false);
        return ToSdkAdmission(request, original);
    }

    public async ValueTask<AdapterAtomicDirectoryDeletionReceipt> DeleteAtomicallyAsync(AdapterAtomicDirectoryDeletionRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate();
        var (paths, original) = await LoadAsync(request.OriginalRequest, requireRetained: true, cancellationToken).ConfigureAwait(false);
        var retained = ToSdkAdmission(request.OriginalRequest, original);
        if (retained.AdmissionFingerprint != request.Admission.AdmissionFingerprint)
            throw new InvalidDataException("LocalOriginalDirectoryAdmissionChanged");
        var authorization = new LocalAtomicDirectoryAuthorization(original.InstanceId, original.AdmissionId, original.Fingerprint,
            request.ManifestFingerprint, string.Empty, request.RequestFingerprint);
        authorization = authorization with { RequestFingerprint = authorization.CalculateFingerprint() };
        var receipt = await LocalFileOperations.RetireAtomicDirectoryAsync(paths, original, stateDirectory,
            enableExperimentalNtfsTransactions: true, checkpoint: LoseFixtureReply, cancellationToken, authorization).ConfigureAwait(false);
        return new(1, original.InstanceId, original.AdmissionId, receipt.TransactionId, request.RequestFingerprint,
            retained.AdmissionFingerprint, request.ManifestFingerprint, receipt.Effects.Select((effect, ordinal) =>
                new AdapterAtomicDirectoryDeletedOriginal(effect.OperationId, new(original.Original.RootKey, effect.Path),
                    retained.Operations[ordinal].OriginalGeneration)).ToArray(),
            Convert.ToBase64String(AdapterProtocolJson.Encode(new
            {
                version = 1,
                nativeAdmissionFingerprint = original.Fingerprint,
                nativeReceiptSha256 = Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(receipt))),
            })));

        ValueTask LoseFixtureReply(LocalAtomicDirectoryCheckpoint checkpoint, CancellationToken token)
        {
            if (checkpoint == LocalAtomicDirectoryCheckpoint.AfterCommit)
            {
                // The candidate native path has already validated an owned test
                // fixture. This one-time cut exits after commit and before reply.
                string cut = Path.Combine(stateDirectory, original.AdmissionId.ToString("N") + ".fixture-lose-reply");
                if (File.Exists(cut))
                {
                    File.Move(cut, cut + ".observed");
                    Environment.Exit(73);
                }
            }
            return ValueTask.CompletedTask;
        }
    }

    private async Task<(LocalWorkerPaths Paths, LocalAtomicDirectoryAdmission Admission)> LoadAsync(
        AdapterDirectoryAdmissionRequest request, bool requireRetained, CancellationToken token)
    {
        request.Validate();
        if (request.Operations.Any(value => value.OperationKind != "Delete"))
            throw new NotSupportedException("LocalOriginalDirectoryAdmissionRequiresDeletePlan");
        var root = request.Operations[^1].Operation;
        var paths = roots.GetPaths(root.RootKey);
        var operations = request.Operations.Select(value => value.Operation).ToArray();
        var original = await LocalFileOperations.PrepareAtomicDirectoryRetirementAsync(paths, request.InstanceId, request.AdmissionId,
            root.RootKey, root.Path, operations.ToDictionary(value => value.Path, value => value.OperationId, StringComparer.Ordinal),
            stateDirectory, enableExperimentalNtfsTransactions: true, token, verifyVolumeHistory: true, originalRequests: operations,
            originalRequestFingerprint: request.RequestFingerprint, requireRetainedAdmission: requireRetained).ConfigureAwait(false);
        return (paths, original);
    }

    private static AdapterDirectoryAdmission ToSdkAdmission(AdapterDirectoryAdmissionRequest request, LocalAtomicDirectoryAdmission native)
    {
        var projected = LocalFileOperations.ReadAtomicDirectorySdkAdmission(native);
        var response = new AdapterDirectoryAdmission(1, projected.InstanceId, projected.AdmissionId, projected.RequestFingerprint,
            projected.Operations.Select(value => new AdapterAdmittedDirectoryOperation(value.OperationId, value.OriginalGeneration,
                value.Directory, value.OriginalParents)).ToArray(), string.Empty);
        response = response with { AdmissionFingerprint = response.CalculateFingerprint() };
        response.RequireRequest(request);
        return response;
    }

    internal void RequireOrdinaryOperation(Guid operationId)
    {
        string claim = Path.Combine(stateDirectory, "operation-" + operationId.ToString("N") + ".claim.json");
        if (File.Exists(claim)) throw new InvalidDataException("OriginalAtomicDirectoryOperationAlreadyOwned");
    }
}
#endif
