extern alias Probe;
using ProbeFileOperations = Probe::MirrorPulse.Adapter.Local.Worker.LocalFileOperations;
using ProbeLocalAtomicDirectoryAdmission = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryAdmission;
using ProbeLocalAtomicDirectoryCheckpoint = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryCheckpoint;
using ProbeLocalAtomicDirectoryEffect = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryEffect;
using ProbeLocalAtomicDirectoryEntry = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryEntry;
using ProbeLocalAtomicDirectoryReceipt = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryReceipt;
using ProbeWorkerPaths = Probe::MirrorPulse.Adapter.Local.Worker.LocalWorkerPaths;
namespace MirrorPulse.Adapter.Local.Worker.Tests;

public sealed partial class LocalAtomicDirectoryRetirementTests
{
    [TestMethod]
    [DataRow("content")]
    [DataRow("metadata")]
    [DataRow("restored-parent-time")]
    [DataRow("added-child")]
    [DataRow("removed-child")]
    [DataRow("replacement")]
    public async Task OriginalAtomicAdmissionCannotAdoptChangedBytesMetadataInventoryOrGeneration(string damage)
    {
        using var fixture = new Fixture(); var admission = await fixture.PrepareAsync();
        if (damage == "content") await File.WriteAllTextAsync(Path.Combine(fixture.Tree, "top.bin"), "changed original content");
        if (damage == "metadata") File.SetAttributes(Path.Combine(fixture.Tree, "top.bin"), FileAttributes.Hidden);
        if (damage == "restored-parent-time")
        {
            DateTime original = Directory.GetLastWriteTimeUtc(fixture.Tree);
            await File.WriteAllTextAsync(Path.Combine(fixture.Tree, "transient.bin"), "unowned temporary child");
            File.Delete(Path.Combine(fixture.Tree, "transient.bin")); Directory.SetLastWriteTimeUtc(fixture.Tree, original);
        }
        if (damage == "added-child") await File.WriteAllTextAsync(Path.Combine(fixture.Tree, "unowned.bin"), "new unowned content");
        if (damage == "removed-child") File.Delete(Path.Combine(fixture.Tree, "nested", "leaf.bin"));
        if (damage == "replacement")
        {
            Directory.Move(fixture.Tree, Path.Combine(fixture.DirectoryPath, "original-tree"));
            Directory.CreateDirectory(Path.Combine(fixture.Tree, "nested"));
            await File.WriteAllTextAsync(Path.Combine(fixture.Tree, "top.bin"), "replacement top");
            await File.WriteAllTextAsync(Path.Combine(fixture.Tree, "nested", "leaf.bin"), "replacement leaf");
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RetireAsync(admission));
        Assert.IsTrue(Directory.Exists(fixture.Tree)); Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
        Assert.AreEqual("unrelated peer", await File.ReadAllTextAsync(fixture.Peer));
    }

    [TestMethod]
    [DataRow("instance")]
    [DataRow("admission")]
    [DataRow("fingerprint")]
    [DataRow("operation")]
    [DataRow("missing-child")]
    [DataRow("receipt")]
    public async Task ForeignOrDamagedOriginalAuthorityNeverExecutesAnotherBatch(string damage)
    {
        using var fixture = new Fixture(); var original = await fixture.PrepareAsync();
        var changed = damage switch
        {
            "instance" => original with { InstanceId = Guid.NewGuid() },
            "admission" => original with { AdmissionId = Guid.NewGuid() },
            "fingerprint" => original with { Fingerprint = new string('A', 64) },
            "operation" => original with { Entries = original.Entries.Select((entry, index) => index == 0 ? entry with { OperationId = Guid.NewGuid() } : entry).ToArray() },
            "missing-child" => original with { Entries = original.Entries.Skip(1).ToArray() },
            _ => original
        };
        if (damage == "receipt") await File.WriteAllTextAsync(fixture.Receipt(original), "{\"version\":1}");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RetireAsync(changed));
        await fixture.AssertOriginalsAsync();
    }

    [TestMethod]
    public async Task TheExperimentalBackendAndSeparatePersistentStateGrantAreBothExplicit()
    {
        using var fixture = new Fixture(); var admission = await fixture.PrepareAsync();
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => ProbeFileOperations.RetireAtomicDirectoryAsync(fixture.Paths,
            admission, fixture.State, false, null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ProbeFileOperations.RetireAtomicDirectoryAsync(fixture.Paths,
            admission, fixture.Tree, true, null, CancellationToken.None));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ProbeFileOperations.RetireAtomicDirectoryAsync(fixture.Paths,
            admission, fixture.State, true, null, canceled.Token));
        await fixture.AssertOriginalsAsync(); Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
    }
}
