extern alias Probe;
using System.Text.Json;
using MirrorPulse.Adapter.Sdk;
using ProbeLocalAtomicDirectoryAdmission = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryAdmission;
using ProbeLocalAtomicDirectoryAuthorization = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryAuthorization;
using ProbeLocalAtomicDirectoryCheckpoint = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryCheckpoint;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

public sealed partial class LocalAtomicDirectoryRetirementTests
{
    [TestMethod]
    public async Task AuthorizedAtomicDeletionRequiresCompleteHistoryAndExactNativeAdmission()
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RetireAsync(admission, authorization: Authorize(admission)));
        admission = await fixture.PrepareAsync(verifyVolumeHistory: true);
        var authorization = Authorize(admission);
        foreach (var changed in new[]
        {
            authorization with { InstanceId = Guid.NewGuid() }, authorization with { AdmissionId = Guid.NewGuid() },
            authorization with { NativeAdmissionFingerprint = new string('C', 64) },
            authorization with { ManifestFingerprint = new string('C', 64) }, authorization with { ManifestFingerprint = "" },
        }) await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RetireAsync(admission, authorization: changed));
        await fixture.AssertOriginalsAsync();
        Assert.IsFalse(File.Exists(AuthorizationPath(fixture, admission)));
        Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
    }

    [TestMethod]
    [DataRow(nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterOriginalsLocked))]
    [DataRow(nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterFirstChildStaged))]
    [DataRow(nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterParentStaged))]
    [DataRow(nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterReceiptStaged))]
    public async Task AuthorizedInterruptionRetainsOriginalBytesAndReservesTheSameManifest(string cut)
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync(verifyVolumeHistory: true);
        var authorization = Authorize(admission);
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.RetireAsync(admission, (point, _) =>
            point.ToString() == cut ? ValueTask.FromException(new IOException("Synthetic authorized transaction interruption.")) : ValueTask.CompletedTask,
            authorization));
        await fixture.AssertOriginalsAsync();
        Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
        var retained = AdapterProtocolJson.Decode<ProbeLocalAtomicDirectoryAuthorization>(await File.ReadAllBytesAsync(AuthorizationPath(fixture, admission)));
        Assert.AreEqual(authorization, retained);
        var rebound = authorization with { ManifestFingerprint = new string('D', 64) };
        rebound = rebound with { RequestFingerprint = rebound.CalculateFingerprint() };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RetireAsync(admission, authorization: rebound));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RetireAsync(admission));
        await fixture.AssertOriginalsAsync();
        var resumed = await RunProbeAsync("--retire-authorized", fixture, admission);
        Assert.AreEqual(0, resumed.ExitCode, resumed.Output);
        Assert.IsFalse(Directory.Exists(fixture.Tree));
        using var receipt = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.Receipt(admission)));
        Assert.AreEqual(authorization.RequestFingerprint, receipt.RootElement.GetProperty("authorization").GetProperty("requestFingerprint").GetString());
    }

    [TestMethod]
    public async Task AuthorizedCommitAndLostReplyReplayExactReceiptInNewProcessWithoutTouchingSuccessor()
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync(verifyVolumeHistory: true);
        var authorization = Authorize(admission);
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.RetireAsync(admission, (point, _) =>
            point == ProbeLocalAtomicDirectoryCheckpoint.AfterCommit ? ValueTask.FromException(new IOException("Synthetic lost reply.")) : ValueTask.CompletedTask,
            authorization));
        byte[] originalReceipt = await File.ReadAllBytesAsync(fixture.Receipt(admission));
        Directory.CreateDirectory(fixture.Tree);
        await File.WriteAllTextAsync(Path.Combine(fixture.Tree, "successor.bin"), "independent later original");
        var rebound = authorization with { ManifestFingerprint = new string('D', 64) };
        rebound = rebound with { RequestFingerprint = rebound.CalculateFingerprint() };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RetireAsync(admission, authorization: rebound));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RetireAsync(admission));
        var replay = await RunProbeAsync("--retire-authorized", fixture, admission);
        Assert.AreEqual(0, replay.ExitCode, replay.Output);
        CollectionAssert.AreEqual(originalReceipt, await File.ReadAllBytesAsync(fixture.Receipt(admission)));
        Assert.AreEqual("independent later original", await File.ReadAllTextAsync(Path.Combine(fixture.Tree, "successor.bin")));
        Assert.AreEqual("unrelated peer", await File.ReadAllTextAsync(fixture.Peer));
    }

    [TestMethod]
    public async Task AuthorizedRealProcessExitRollsBackAndANewProcessReplaysTheReservedOriginals()
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync(verifyVolumeHistory: true);
        var authorization = Authorize(admission);
        // Reserve once without committing source effects, then kill a separate
        // process during the actual native transaction (managed finally is bypassed).
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.RetireAsync(admission, (point, _) =>
            point == ProbeLocalAtomicDirectoryCheckpoint.AfterOriginalsLocked ? ValueTask.FromException(new IOException("Reserve authorization.")) : ValueTask.CompletedTask,
            authorization));
        var stopped = await RunProbeAsync("--retire-authorized", fixture, admission, nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterReceiptStaged));
        Assert.AreEqual(73, stopped.ExitCode, stopped.Output);
        await fixture.AssertOriginalsAsync();
        Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
        var resumed = await RunProbeAsync("--retire-authorized", fixture, admission);
        Assert.AreEqual(0, resumed.ExitCode, resumed.Output);
        Assert.IsFalse(Directory.Exists(fixture.Tree));
        var replay = await fixture.RetireAsync(admission, authorization: authorization);
        Assert.AreEqual(authorization, replay.Authorization);
        Assert.HasCount(admission.Entries.Count, replay.Effects);
    }

    [TestMethod]
    [DataRow(nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterAuthorizationStaged), false)]
    [DataRow(nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterAuthorizationCommitted), true)]
    public async Task AuthorizedReservationHasAnAtomicCrashBoundaryBeforeSourceEffects(string cut, bool reservationCommitted)
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync(verifyVolumeHistory: true);
        var authorization = Authorize(admission);
        string input = Path.Combine(fixture.State, admission.AdmissionId.ToString("N") + ".authorization-request.json");
        await File.WriteAllBytesAsync(input, AdapterProtocolJson.Encode(authorization));
        var stopped = await RunProbeAsync("--retire-authorized", fixture, admission, cut);
        Assert.AreEqual(73, stopped.ExitCode, stopped.Output);
        Assert.AreEqual(reservationCommitted, File.Exists(AuthorizationPath(fixture, admission)));
        Assert.HasCount(reservationCommitted ? admission.Entries.Count : 0, Directory.GetFiles(fixture.State, "operation-*.claim.json"));
        Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
        await fixture.AssertOriginalsAsync();
        var resumed = await RunProbeAsync("--retire-authorized", fixture, admission);
        Assert.AreEqual(0, resumed.ExitCode, resumed.Output);
        Assert.IsFalse(Directory.Exists(fixture.Tree));
        var receipt = await fixture.RetireAsync(admission, authorization: authorization);
        Assert.AreEqual(authorization, receipt.Authorization);
    }

    [TestMethod]
    public async Task AuthorizedOperationsCannotJoinAnotherAdmissionAfterTheirAtomicReservation()
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync(verifyVolumeHistory: true);
        var authorization = Authorize(admission);
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.RetireAsync(admission, (point, _) =>
            point == ProbeLocalAtomicDirectoryCheckpoint.AfterAuthorizationCommitted ? ValueTask.FromException(new IOException("Reserve only.")) : ValueTask.CompletedTask,
            authorization));
        var operations = admission.Entries.ToDictionary(entry => entry.Original.Path, entry => entry.OperationId, StringComparer.Ordinal);
        var other = await Probe::MirrorPulse.Adapter.Local.Worker.LocalFileOperations.PrepareAtomicDirectoryRetirementAsync(fixture.Paths,
            admission.InstanceId, Guid.NewGuid(), "docs", "tree", operations, fixture.State, true, CancellationToken.None, verifyVolumeHistory: true);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RetireAsync(other, authorization: Authorize(other)));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RetireAsync(other));
        Assert.IsFalse(File.Exists(AuthorizationPath(fixture, other)));
        Assert.IsFalse(File.Exists(fixture.Receipt(other)));
        await fixture.AssertOriginalsAsync();
        var receipt = await fixture.RetireAsync(admission, authorization: authorization);
        Assert.AreEqual(admission.AdmissionId, receipt.AdmissionId);
    }

    [TestMethod]
    public async Task AuthorizedExecutionRefusesAMissingOriginalOperationClaim()
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync(verifyVolumeHistory: true);
        var authorization = Authorize(admission);
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.RetireAsync(admission, (point, _) =>
            point == ProbeLocalAtomicDirectoryCheckpoint.AfterAuthorizationCommitted ? ValueTask.FromException(new IOException("Reserve only.")) : ValueTask.CompletedTask,
            authorization));
        File.Delete(Path.Combine(fixture.State, "operation-" + admission.Entries[0].OperationId.ToString("N") + ".claim.json"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RetireAsync(admission, authorization: authorization));
        await fixture.AssertOriginalsAsync();
        Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
    }

    [TestMethod]
    public async Task AuthorizedReceiptCannotLoseItsManifestBindingOnReplay()
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync(verifyVolumeHistory: true);
        var authorization = Authorize(admission);
        var receipt = await fixture.RetireAsync(admission, authorization: authorization);
        await File.WriteAllBytesAsync(fixture.Receipt(admission), AdapterProtocolJson.Encode(receipt with { Authorization = null }));
        Directory.CreateDirectory(fixture.Tree);
        await File.WriteAllTextAsync(Path.Combine(fixture.Tree, "successor.bin"), "successor remains");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RetireAsync(admission, authorization: authorization));
        Assert.AreEqual("successor remains", await File.ReadAllTextAsync(Path.Combine(fixture.Tree, "successor.bin")));
    }

    private static ProbeLocalAtomicDirectoryAuthorization Authorize(ProbeLocalAtomicDirectoryAdmission admission)
    {
        var authorization = new ProbeLocalAtomicDirectoryAuthorization(admission.InstanceId, admission.AdmissionId, admission.Fingerprint,
            new string('B', 64), "");
        return authorization with { RequestFingerprint = authorization.CalculateFingerprint() };
    }

    private static string AuthorizationPath(Fixture fixture, ProbeLocalAtomicDirectoryAdmission admission) =>
        Path.Combine(fixture.State, admission.AdmissionId.ToString("N") + ".authorization.json");
}
