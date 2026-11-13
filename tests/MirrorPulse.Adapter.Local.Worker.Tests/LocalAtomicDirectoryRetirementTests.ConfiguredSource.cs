extern alias Probe;
using MirrorPulse.Adapter.Sdk;
using LocalAtomicDirectoryCheckpoint = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryCheckpoint;
using LocalAtomicDirectoryReceipt = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryReceipt;
using ProbeFileOperations = Probe::MirrorPulse.Adapter.Local.Worker.LocalFileOperations;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

public sealed partial class LocalAtomicDirectoryRetirementTests
{
    [TestMethod]
    [DataRow("complete")]
    [DataRow("before-commit")]
    [DataRow("after-commit")]
    public async Task ConfiguredSourceRetainsTheSameBoundBatchAndReceiptAcrossInterruption(string cut)
    {
        using var fixture = new Fixture(configuredSource: true);
        var request = await BoundRequestAsync(fixture);
        var admission = await PrepareBoundAsync(fixture, request, configuredSource: true);
        var authorization = Authorize(admission) with { SdkRequestFingerprint = request.Fingerprint };
        authorization = authorization with { RequestFingerprint = authorization.CalculateFingerprint() };
        LocalAtomicDirectoryCheckpoint? checkpoint = cut switch
        {
            "before-commit" => LocalAtomicDirectoryCheckpoint.AfterReceiptStaged,
            "after-commit" => LocalAtomicDirectoryCheckpoint.AfterCommit,
            _ => null
        };
        Task<LocalAtomicDirectoryReceipt> RetireAsync(bool inject) =>
            ProbeFileOperations.RetireAtomicDirectoryAsync(fixture.Paths, admission, fixture.State, true,
                (point, _) => inject && point == checkpoint
                    ? ValueTask.FromException(new IOException("Interrupted configured-source transaction."))
                    : ValueTask.CompletedTask, CancellationToken.None, authorization, configuredSource: true);
        if (checkpoint is not null)
            await Assert.ThrowsExactlyAsync<IOException>(() => RetireAsync(inject: true));
        if (cut == "before-commit")
        {
            await fixture.AssertOriginalsAsync();
            Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
        }
        var accepted = await RetireAsync(inject: false);
        Assert.IsFalse(Directory.Exists(fixture.Tree));
        Directory.CreateDirectory(fixture.Tree);
        await File.WriteAllTextAsync(Path.Combine(fixture.Tree, "successor.bin"), "later configured source");
        var replay = await RetireAsync(inject: false);
        CollectionAssert.AreEqual(AdapterProtocolJson.Encode(accepted), AdapterProtocolJson.Encode(replay));
        Assert.AreEqual("later configured source", await File.ReadAllTextAsync(Path.Combine(fixture.Tree, "successor.bin")));
        Assert.AreEqual("unrelated peer", await File.ReadAllTextAsync(fixture.Peer));
    }

    [TestMethod]
    public async Task ConfiguredSourceRequiresOriginalHistoryAndHostAuthorizationBeforeAnyMutation()
    {
        using var fixture = new Fixture(configuredSource: true);
        var request = await BoundRequestAsync(fixture);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            ProbeFileOperations.PrepareAtomicDirectoryRetirementAsync(fixture.Paths, request.Instance, request.Admission,
                "docs", request.Relative, request.Operations.ToDictionary(value => value.Path, value => value.OperationId),
                fixture.State, true, CancellationToken.None, configuredSource: true));
        var admission = await PrepareBoundAsync(fixture, request, configuredSource: true);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            ProbeFileOperations.RetireAtomicDirectoryAsync(fixture.Paths, admission, fixture.State, true, null,
                CancellationToken.None, Authorize(admission), configuredSource: true));
        await fixture.AssertOriginalsAsync();
        Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
        Assert.IsFalse(File.Exists(AuthorizationPath(fixture, admission)));
    }
}
