extern alias Probe;
using System.ComponentModel;
using System.Text.Json;
using LocalAtomicDirectoryAdmission = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryAdmission;
using LocalAtomicDirectoryCheckpoint = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryCheckpoint;
using LocalAtomicDirectoryEffect = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryEffect;
using LocalAtomicDirectoryEntry = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryEntry;
using LocalAtomicDirectoryReceipt = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryReceipt;
using ProbeFileOperations = Probe::MirrorPulse.Adapter.Local.Worker.LocalFileOperations;
using ProbeWorkerPaths = Probe::MirrorPulse.Adapter.Local.Worker.LocalWorkerPaths;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

[TestClass]
public sealed partial class LocalAtomicDirectoryRetirementTests
{
    [TestMethod]
    public async Task CompleteOriginalTreeAndReceiptCommitTogetherAndReplayPreservesALaterTree()
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync();
        var receipt = await fixture.RetireAsync(admission);
        Assert.HasCount(4, receipt.Effects);
        Assert.IsFalse(Directory.Exists(fixture.Tree)); Assert.IsTrue(File.Exists(fixture.Receipt(admission)));
        Assert.AreEqual("unrelated peer", await File.ReadAllTextAsync(fixture.Peer));
        string original = await File.ReadAllTextAsync(fixture.Receipt(admission));
        Directory.CreateDirectory(fixture.Tree);
        await File.WriteAllTextAsync(Path.Combine(fixture.Tree, "later.bin"), "later generation");
        var replayed = await fixture.RetireAsync(admission);
        Assert.AreEqual(JsonSerializer.Serialize(receipt), JsonSerializer.Serialize(replayed));
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Receipt(admission)));
        Assert.AreEqual("later generation", await File.ReadAllTextAsync(Path.Combine(fixture.Tree, "later.bin")));
    }

    [TestMethod]
    [DataRow(nameof(LocalAtomicDirectoryCheckpoint.AfterOriginalsLocked))]
    [DataRow(nameof(LocalAtomicDirectoryCheckpoint.AfterFirstChildStaged))]
    [DataRow(nameof(LocalAtomicDirectoryCheckpoint.AfterParentStaged))]
    [DataRow(nameof(LocalAtomicDirectoryCheckpoint.AfterReceiptStaged))]
    public async Task BeforeCommitInterruptionRetainsAllOriginalBytesAndNoAcceptedReceipt(string cutName)
    {
        var cut = Enum.Parse<LocalAtomicDirectoryCheckpoint>(cutName);
        using var fixture = new Fixture(); var admission = await fixture.PrepareAsync();
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.RetireAsync(admission,
            (point, _) => point == cut ? ValueTask.FromException(new IOException("Synthetic original transaction interruption.")) : ValueTask.CompletedTask));
        await fixture.AssertOriginalsAsync(); Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
        var completed = await fixture.RetireAsync(admission);
        Assert.HasCount(4, completed.Effects); Assert.IsFalse(Directory.Exists(fixture.Tree));
    }

    [TestMethod]
    public async Task ANewUnownedChildBeforeParentRetirementRollsBackEveryStagedOriginalRemoval()
    {
        using var fixture = new Fixture(); var admission = await fixture.PrepareAsync(); bool injected = false;
        var error = await Assert.ThrowsExactlyAsync<Win32Exception>(() => fixture.RetireAsync(admission, async (point, token) =>
        {
            if (point != LocalAtomicDirectoryCheckpoint.AfterFirstChildStaged) return;
            await File.WriteAllTextAsync(Path.Combine(fixture.Tree, "unowned.bin"), "unowned new content", token); injected = true;
        }));
        Assert.IsTrue(injected); Assert.IsTrue(error.NativeErrorCode is 145 or 6800 or 6801);
        await fixture.AssertOriginalsAsync();
        Assert.AreEqual("unowned new content", await File.ReadAllTextAsync(Path.Combine(fixture.Tree, "unowned.bin")));
        Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
    }

    [TestMethod]
    public async Task ACommittedEffectSurvivesALostReturnAndRetainsTheOriginalReceipt()
    {
        using var fixture = new Fixture(); var admission = await fixture.PrepareAsync();
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.RetireAsync(admission,
            (point, _) => point == LocalAtomicDirectoryCheckpoint.AfterCommit ? ValueTask.FromException(new IOException("Synthetic lost committed return.")) : ValueTask.CompletedTask));
        Assert.IsFalse(Directory.Exists(fixture.Tree)); string original = await File.ReadAllTextAsync(fixture.Receipt(admission));
        var recovered = await fixture.RetireAsync(admission); Assert.HasCount(4, recovered.Effects);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Receipt(admission)));
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string[] OriginalPaths = ["tree", "tree/nested", "tree/nested/leaf.bin", "tree/top.bin"];
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "mp-local-atomic-" + Guid.NewGuid().ToString("N"));
        internal ProbeWorkerPaths Paths { get; }
        internal string State { get; }
        internal string Tree { get; }
        internal string Peer { get; }
        internal string DirectoryPath => _directory;
        internal Fixture()
        {
            string source = Path.Combine(_directory, "source"); State = Path.Combine(_directory, "state"); Tree = Path.Combine(source, "tree");
            Directory.CreateDirectory(Path.Combine(Tree, "nested")); Directory.CreateDirectory(State);
            File.WriteAllText(Path.Combine(_directory, ".mp-atomic-test-fixture"), Path.GetFileName(_directory));
            File.WriteAllText(Path.Combine(Tree, "nested", "leaf.bin"), "original leaf bytes");
            File.WriteAllText(Path.Combine(Tree, "top.bin"), "original top bytes");
            Peer = Path.Combine(source, "peer.bin"); File.WriteAllText(Peer, "unrelated peer"); Paths = new(source);
        }
        internal Task<LocalAtomicDirectoryAdmission> PrepareAsync()
        {
            var operations = OriginalPaths.ToDictionary(path => path, _ => Guid.NewGuid(), StringComparer.Ordinal);
            return ProbeFileOperations.PrepareAtomicDirectoryRetirementAsync(Paths, Guid.NewGuid(), Guid.NewGuid(), "docs", "tree", operations, State, true, CancellationToken.None);
        }
        internal Task<LocalAtomicDirectoryReceipt> RetireAsync(LocalAtomicDirectoryAdmission admission,
            Func<LocalAtomicDirectoryCheckpoint, CancellationToken, ValueTask>? checkpoint = null) =>
            ProbeFileOperations.RetireAtomicDirectoryAsync(Paths, admission, State, true, checkpoint, CancellationToken.None);
        internal string Receipt(LocalAtomicDirectoryAdmission admission) => Path.Combine(State, admission.AdmissionId.ToString("N") + ".committed.json");
        internal async Task AssertOriginalsAsync()
        {
            Assert.AreEqual("original leaf bytes", await File.ReadAllTextAsync(Path.Combine(Tree, "nested", "leaf.bin")));
            Assert.AreEqual("original top bytes", await File.ReadAllTextAsync(Path.Combine(Tree, "top.bin")));
            Assert.AreEqual("unrelated peer", await File.ReadAllTextAsync(Peer));
        }
        public void Dispose()
        {
            string path = Path.GetFullPath(_directory);
            if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(path).StartsWith("mp-local-atomic-", StringComparison.Ordinal)) throw new InvalidOperationException("Fixture cleanup escaped its original temporary root.");
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
