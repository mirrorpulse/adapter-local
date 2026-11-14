extern alias Probe;
using System.Text.Json;
using ProbeLocalAtomicDirectoryCheckpoint = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryCheckpoint;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

public sealed partial class LocalAtomicDirectoryRetirementTests
{
    [TestMethod]
    public async Task NamespaceFencedBatchCommitsOriginalEffectsAndReceiptAndReplaysWithoutTouchingSuccessor()
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync(fenceParentNamespace: true);
        Assert.AreEqual(2, admission.Version);
        var receipt = await fixture.RetireAsync(admission);
        Assert.AreEqual(2, receipt.Version); Assert.HasCount(4, receipt.Effects);
        Assert.IsFalse(Directory.Exists(fixture.Tree));
        Assert.AreEqual("unrelated peer", await File.ReadAllTextAsync(fixture.Peer));
        string original = await File.ReadAllTextAsync(fixture.Receipt(admission));
        Directory.CreateDirectory(fixture.Tree);
        await File.WriteAllTextAsync(Path.Combine(fixture.Tree, "later.bin"), "later source generation");
        var replay = await fixture.RetireAsync(admission);
        Assert.AreEqual(JsonSerializer.Serialize(receipt), JsonSerializer.Serialize(replay));
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Receipt(admission)));
        Assert.AreEqual("later source generation", await File.ReadAllTextAsync(Path.Combine(fixture.Tree, "later.bin")));
    }

    [TestMethod]
    [DataRow(nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterOriginalsLocked))]
    [DataRow(nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterFirstChildStaged))]
    [DataRow(nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterParentStaged))]
    public async Task NamespaceFencedBatchRefusesActualIndependentChildCreateBeforeItsCommit(string checkpoint)
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync(fenceParentNamespace: true);
        bool attempted = false;
        var receipt = await fixture.RetireAsync(admission, async (point, _) =>
        {
            if (point.ToString() != checkpoint) return;
            var contender = await RunProbeAsync("--create-extra", fixture, admission);
            Assert.AreEqual(91, contender.ExitCode, contender.Output);
            using var result = JsonDocument.Parse(contender.Output);
            Assert.AreEqual(6800, result.RootElement.GetProperty("hresult").GetInt32() & 0xffff);
            attempted = true;
        });
        Assert.IsTrue(attempted); Assert.AreEqual(2, receipt.Version); Assert.HasCount(4, receipt.Effects);
        Assert.IsFalse(Directory.Exists(fixture.Tree));
        Assert.AreEqual("unrelated peer", await File.ReadAllTextAsync(fixture.Peer));
    }

    [TestMethod]
    public async Task NamespaceFencedBatchRollsBackAllOriginalsBeforeCommitAndCanRetry()
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync(fenceParentNamespace: true);
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.RetireAsync(admission, (point, _) =>
            point == ProbeLocalAtomicDirectoryCheckpoint.AfterFirstChildStaged
                ? ValueTask.FromException(new IOException("Owned fenced transaction interruption.")) : ValueTask.CompletedTask));
        await fixture.AssertOriginalsAsync();
        Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
        Assert.AreEqual(2, (await fixture.RetireAsync(admission)).Version);
    }

    [TestMethod]
    [DataRow(nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterOriginalsLocked))]
    [DataRow(nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterFirstChildStaged))]
    [DataRow(nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterReceiptStaged))]
    public async Task NamespaceFencedBatchRecoversRealProcessDeathBeforeCommitWithOriginalBytes(string checkpoint)
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync(fenceParentNamespace: true);
        var stopped = await RunProbeAsync("--retire", fixture, admission, checkpoint);
        Assert.AreEqual(73, stopped.ExitCode, stopped.Output);
        await fixture.AssertOriginalsAsync(); Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
        var resumed = await RunProbeAsync("--retire", fixture, admission);
        Assert.AreEqual(0, resumed.ExitCode, resumed.Output);
        Assert.IsFalse(Directory.Exists(fixture.Tree)); Assert.IsTrue(File.Exists(fixture.Receipt(admission)));
    }

    [TestMethod]
    public async Task NamespaceFencedBatchRecoversRealLostCommitReplyWithoutDeletingALaterTree()
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync(fenceParentNamespace: true);
        var stopped = await RunProbeAsync("--retire", fixture, admission, nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterCommit));
        Assert.AreEqual(73, stopped.ExitCode, stopped.Output);
        Assert.IsFalse(Directory.Exists(fixture.Tree));
        string original = await File.ReadAllTextAsync(fixture.Receipt(admission));
        Directory.CreateDirectory(fixture.Tree);
        await File.WriteAllTextAsync(Path.Combine(fixture.Tree, "later.bin"), "later committed generation");
        var replay = await RunProbeAsync("--retire", fixture, admission);
        Assert.AreEqual(0, replay.ExitCode, replay.Output);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Receipt(admission)));
        Assert.AreEqual("later committed generation", await File.ReadAllTextAsync(Path.Combine(fixture.Tree, "later.bin")));
    }
}
