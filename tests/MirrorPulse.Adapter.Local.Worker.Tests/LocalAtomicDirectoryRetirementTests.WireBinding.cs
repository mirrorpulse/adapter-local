extern alias Probe;
using System.Security.Cryptography;
using MirrorPulse.Adapter.Sdk;
using ProbeFileOperations = Probe::MirrorPulse.Adapter.Local.Worker.LocalFileOperations;
using ProbeLocalAtomicDirectoryAdmission = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryAdmission;
using ProbeLocalAtomicDirectoryCheckpoint = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryCheckpoint;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

public sealed partial class LocalAtomicDirectoryRetirementTests
{
    [TestMethod]
    [DataRow("tree")]
    [DataRow("tree/nested")]
    public async Task BoundOriginalAdmissionPreservesExactSdkConditionsOrderAndGenerationsAfterItsAtomicEffect(string relative)
    {
        using var fixture = new Fixture();
        var request = await BoundRequestAsync(fixture, relative);
        var admission = await PrepareBoundAsync(fixture, request);
        Assert.AreEqual(4, admission.Version);
        var sdk = ProbeFileOperations.ReadAtomicDirectorySdkAdmission(admission);
        Assert.AreEqual(request.Fingerprint, sdk.RequestFingerprint);
        CollectionAssert.AreEqual(request.Operations.Select(value => value.OperationId).ToArray(), sdk.Operations.Select(value => value.OperationId).ToArray());
        foreach (var member in sdk.Operations)
        {
            Assert.IsTrue(member.OriginalGeneration.StartsWith("native-v2:", StringComparison.Ordinal));
            Assert.HasCount(1, member.OriginalParents);
            member.OriginalParents[0].Validate();
            if (member.Directory is { } directory)
            {
                directory.Validate();
                Assert.AreEqual(member.OriginalParents[0].Generation, directory.ParentGeneration);
                Assert.AreEqual(request.Operations.Single(value => value.OperationId == member.OperationId).Preconditions!.ExpectedRevision, directory.OriginalRevision);
            }
        }
        var parent = sdk.Operations[^1].OriginalParents[0];
        Assert.AreEqual(relative == "tree" ? "" : "tree", parent.Directory.Path);
        if (relative == "tree") Assert.IsNull(parent.ParentGeneration);
        else Assert.IsNotNull(parent.ParentGeneration);
        byte[] retained = AdapterProtocolJson.Encode(sdk);
        var authorization = Authorize(admission);
        var receipt = await fixture.RetireAsync(admission, authorization: authorization);
        CollectionAssert.AreEqual(request.Operations.Select(value => value.OperationId).ToArray(), receipt.Effects.Select(value => value.OperationId).ToArray());
        string source = fixture.Paths.ResolveDirectory(relative);
        Assert.IsFalse(Directory.Exists(source));
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "successor.bin"), "later independent tree");
        var replayed = await PrepareBoundAsync(fixture, request);
        Assert.AreEqual(admission.Fingerprint, replayed.Fingerprint);
        CollectionAssert.AreEqual(retained, AdapterProtocolJson.Encode(ProbeFileOperations.ReadAtomicDirectorySdkAdmission(replayed)));
        var replay = await fixture.RetireAsync(replayed, authorization: authorization);
        CollectionAssert.AreEqual(AdapterProtocolJson.Encode(receipt), AdapterProtocolJson.Encode(replay));
        Assert.AreEqual("later independent tree", await File.ReadAllTextAsync(Path.Combine(source, "successor.bin")));
        Assert.AreEqual("unrelated peer", await File.ReadAllTextAsync(fixture.Peer));
    }

    [TestMethod]
    [DataRow("stale-revision")]
    [DataRow("not-directory")]
    [DataRow("omit-leaf")]
    [DataRow("changed-fingerprint")]
    [DataRow("move-destination")]
    [DataRow("parent-before-child")]
    public async Task BoundAdmissionRejectsChangedConditionsOrIncompleteOriginalPlanBeforePersisting(string damage)
    {
        using var fixture = new Fixture();
        var original = await BoundRequestAsync(fixture);
        var operations = original.Operations.ToArray();
        if (damage == "stale-revision") operations[0] = operations[0] with { Preconditions = new("a-different-original-revision") };
        if (damage == "not-directory") operations[2] = operations[2] with { IsDirectory = false };
        if (damage == "omit-leaf") operations = operations.Skip(1).ToArray();
        if (damage == "move-destination") operations[0] = operations[0] with { DestinationRootKey = "docs", DestinationPath = "destination" };
        if (damage == "parent-before-child") (operations[1], operations[2]) = (operations[2], operations[1]);
        var request = original with { Operations = operations, Fingerprint = RequestFingerprint(original.Instance, original.Admission, operations) };
        if (damage == "changed-fingerprint") request = request with { Fingerprint = new string('F', 64) };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PrepareBoundAsync(fixture, request));
        await fixture.AssertOriginalsAsync();
        Assert.IsFalse(File.Exists(Path.Combine(fixture.State, original.Admission.ToString("N") + ".admission.json")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.State, original.Admission.ToString("N") + ".committed.json")));
    }

    [TestMethod]
    public async Task BoundAdmissionRequiresTheHostManifestBeforeAnyOriginalCanRetire()
    {
        using var fixture = new Fixture();
        var request = await BoundRequestAsync(fixture);
        var admission = await PrepareBoundAsync(fixture, request);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RetireAsync(admission));
        await fixture.AssertOriginalsAsync();
        Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
        Assert.IsFalse(File.Exists(AuthorizationPath(fixture, admission)));
    }

    [TestMethod]
    public async Task BoundAdmissionCannotRebindItsRetainedIdToOtherOperationsOrRevisions()
    {
        using var fixture = new Fixture();
        var request = await BoundRequestAsync(fixture);
        var admission = await PrepareBoundAsync(fixture, request);
        byte[] original = await File.ReadAllBytesAsync(Path.Combine(fixture.State, request.Admission.ToString("N") + ".admission.json"));
        var changed = request.Operations.Select(value => value with { OperationId = Guid.NewGuid() }).ToArray();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PrepareBoundAsync(fixture,
            request with { Operations = changed, Fingerprint = RequestFingerprint(request.Instance, request.Admission, changed) }));
        changed = request.Operations.Select(value => value with { Preconditions = new("a-later-revision") }).ToArray();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PrepareBoundAsync(fixture,
            request with { Operations = changed, Fingerprint = RequestFingerprint(request.Instance, request.Admission, changed) }));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(Path.Combine(fixture.State, request.Admission.ToString("N") + ".admission.json")));
        await fixture.AssertOriginalsAsync();
        CollectionAssert.AreEqual(AdapterProtocolJson.Encode(ProbeFileOperations.ReadAtomicDirectorySdkAdmission(admission)),
            AdapterProtocolJson.Encode(ProbeFileOperations.ReadAtomicDirectorySdkAdmission(await PrepareBoundAsync(fixture, request))));
    }

    [TestMethod]
    public async Task BoundAdmissionAndAuthorizationRecoverTheOriginalReceiptAfterActualWorkerProcessDeath()
    {
        using var fixture = new Fixture();
        var request = await BoundRequestAsync(fixture);
        var admission = await PrepareBoundAsync(fixture, request);
        var authorization = Authorize(admission);
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.RetireAsync(admission, (point, _) =>
            point == ProbeLocalAtomicDirectoryCheckpoint.AfterAuthorizationCommitted ? ValueTask.FromException(new IOException("Reserve before process restart.")) : ValueTask.CompletedTask,
            authorization));
        var stopped = await RunProbeAsync("--retire-authorized", fixture, admission, nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterCommit));
        Assert.AreEqual(73, stopped.ExitCode, stopped.Output);
        byte[] receipt = await File.ReadAllBytesAsync(fixture.Receipt(admission));
        Directory.CreateDirectory(fixture.Tree);
        await File.WriteAllTextAsync(Path.Combine(fixture.Tree, "successor.bin"), "successor remains");
        var replay = await RunProbeAsync("--retire-authorized", fixture, admission);
        Assert.AreEqual(0, replay.ExitCode, replay.Output);
        CollectionAssert.AreEqual(receipt, await File.ReadAllBytesAsync(fixture.Receipt(admission)));
        Assert.AreEqual("successor remains", await File.ReadAllTextAsync(Path.Combine(fixture.Tree, "successor.bin")));
        Assert.AreEqual(admission.Fingerprint, (await PrepareBoundAsync(fixture, request)).Fingerprint);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BoundAdmissionReplayRefusesAReplacedRootOrCopiedStateDirectory(bool replaceState)
    {
        using var fixture = new Fixture();
        var request = await BoundRequestAsync(fixture);
        var admission = await PrepareBoundAsync(fixture, request);
        string originalPath = replaceState ? fixture.State : fixture.Paths.Root;
        string retainedPath = Path.Combine(fixture.DirectoryPath, "retained-original");
        Directory.Move(originalPath, retainedPath);
        Directory.CreateDirectory(originalPath);
        string file = request.Admission.ToString("N") + ".admission.json";
        if (replaceState) File.Copy(Path.Combine(retainedPath, file), Path.Combine(fixture.State, file));
        var refused = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PrepareBoundAsync(fixture, request));
        Assert.AreEqual("AtomicDirectorySourceOrStateGenerationChanged", refused.Message);
        if (replaceState)
        {
            await fixture.AssertOriginalsAsync();
            Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
        }
        else
        {
            Assert.AreEqual("original leaf bytes", await File.ReadAllTextAsync(Path.Combine(retainedPath, "tree", "nested", "leaf.bin")));
            Assert.IsEmpty(Directory.GetFileSystemEntries(originalPath));
        }
    }

    private sealed record BoundRequest(Guid Instance, Guid Admission, string Relative, AdapterOperationRequest[] Operations, string Fingerprint);

    private static async Task<BoundRequest> BoundRequestAsync(Fixture fixture, string relative = "tree")
    {
        string[] paths = relative == "tree" ? ["tree/top.bin", "tree/nested/leaf.bin", "tree/nested", "tree"] : ["tree/nested/leaf.bin", "tree/nested"];
        var operations = new List<AdapterOperationRequest>();
        foreach (string path in paths)
            operations.Add(new(Guid.NewGuid(), "docs", path, Preconditions: new(await ProbeFileOperations.RevisionAsync(fixture.Paths, path, CancellationToken.None)),
                IsDirectory: Directory.Exists(fixture.Paths.ResolveDirectory(path))));
        Guid instance = Guid.NewGuid(), admission = Guid.NewGuid();
        return new(instance, admission, relative, operations.ToArray(), RequestFingerprint(instance, admission, operations));
    }

    private static string RequestFingerprint(Guid instance, Guid admission, IEnumerable<AdapterOperationRequest> operations) =>
        Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(new
        {
            Version = 1,
            InstanceId = instance,
            AdmissionId = admission,
            Operations = operations.Select(operation => new { OperationKind = "Delete", Operation = operation }).ToArray()
        })));

    private static Task<ProbeLocalAtomicDirectoryAdmission> PrepareBoundAsync(Fixture fixture, BoundRequest request, bool configuredSource = false) =>
        ProbeFileOperations.PrepareAtomicDirectoryRetirementAsync(fixture.Paths, request.Instance, request.Admission, "docs", request.Relative,
            request.Operations.ToDictionary(operation => operation.Path, operation => operation.OperationId, StringComparer.Ordinal), fixture.State,
            true, CancellationToken.None, verifyVolumeHistory: true, originalRequests: request.Operations, originalRequestFingerprint: request.Fingerprint,
            configuredSource: configuredSource);
}
