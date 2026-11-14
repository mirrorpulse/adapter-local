extern alias Probe;
using System.ComponentModel;
using System.Text.Json;
using AtomicNative = Probe::MirrorPulse.Adapter.Local.Worker.AtomicDirectoryNative;
using ProbeFileOperations = Probe::MirrorPulse.Adapter.Local.Worker.LocalFileOperations;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

public sealed partial class LocalAtomicDirectoryRetirementTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeWholeTreeMoveAndReceiptCommitOrRollBackTogether(bool commit)
    {
        // A native feasibility probe, without SDK admission, a Worker request, or namespace-history qualification.
        using var fixture = new Fixture();
        string parent = Path.GetDirectoryName(fixture.Tree)!;
        string destination = Path.Combine(parent, "moved");
        string receiptPath = Path.Combine(fixture.State, "movement-probe.json");
        string[] members = ["", "nested", "nested/leaf.bin", "top.bin"];
        var generations = new Dictionary<string, string>(StringComparer.Ordinal);
        var stagedVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        string stagedParent;
        byte[] receipt;
        using (var transaction = AtomicNative.Begin())
        {
            // TxF refuses directory rename while its transacted original handles are open.
            // Inspect first, then stage the rename and reopen/verify every member before commit.
            foreach (string relative in members)
            {
                using var item = AtomicNative.Open(Path.Combine(fixture.Tree, relative), transaction);
                generations.Add(relative, ProbeFileOperations.RevisionGeneration(item.Handle));
            }
            AtomicNative.Move(fixture.Tree, destination, transaction);
            var opened = new List<AtomicNative.Item>();
            try
            {
                foreach (string relative in members)
                {
                    var item = AtomicNative.Open(Path.Combine(destination, relative), transaction);
                    opened.Add(item);
                    Assert.AreEqual(generations[relative], ProbeFileOperations.RevisionGeneration(item.Handle));
                    stagedVersions.Add(relative, await item.RevisionAsync(CancellationToken.None));
                }
                using (var parentItem = AtomicNative.Open(parent, transaction))
                    stagedParent = await parentItem.RevisionAsync(CancellationToken.None);
                receipt = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Transaction = AtomicNative.Id(transaction),
                    Members = generations,
                    StagedVersions = stagedVersions,
                    StagedParent = stagedParent
                });
                AtomicNative.WriteReceipt(receiptPath, receipt, transaction);
                if (commit) AtomicNative.Commit(transaction);
            }
            finally { foreach (var item in opened) item.Dispose(); }
        }
        if (!commit)
        {
            await fixture.AssertOriginalsAsync();
            Assert.IsFalse(Directory.Exists(destination));
            Assert.IsFalse(File.Exists(receiptPath));
            return;
        }
        Assert.IsFalse(Directory.Exists(fixture.Tree));
        Assert.AreEqual("original leaf bytes", await File.ReadAllTextAsync(Path.Combine(destination, "nested", "leaf.bin")));
        Assert.AreEqual("original top bytes", await File.ReadAllTextAsync(Path.Combine(destination, "top.bin")));
        Assert.AreEqual("unrelated peer", await File.ReadAllTextAsync(fixture.Peer));
        CollectionAssert.AreEqual(receipt, await File.ReadAllBytesAsync(receiptPath));
        foreach (string relative in members)
        {
            string path = "moved" + (relative.Length == 0 ? "" : "/" + relative);
            Assert.AreEqual(stagedVersions[relative], await ProbeFileOperations.RevisionAsync(fixture.Paths, path, CancellationToken.None));
        }
        string? committedParent = await ProbeFileOperations.RevisionAsync(fixture.Paths, "", CancellationToken.None);
        Console.WriteLine($"NativeMovementFeasibility: members={members.Length}; atomicReceipt=True; stableIdentities=True; exactBytes=True; " +
            $"stagedParentMatchesCommitted={stagedParent == committedParent}; sdkAdmission=False; workerRpc=False; namespaceHistoryQualified=False.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeWholeTreeMoveRefusesAnExistingDestinationWithoutReplacingItsBytes(bool directory)
    {
        using var fixture = new Fixture();
        string destination = Path.Combine(Path.GetDirectoryName(fixture.Tree)!, "occupied");
        string occupant = directory ? Path.Combine(destination, "later.bin") : destination;
        if (directory) Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(occupant, "later occupant");
        using (var transaction = AtomicNative.Begin())
            Assert.ThrowsExactly<Win32Exception>(() => AtomicNative.Move(fixture.Tree, destination, transaction));
        await fixture.AssertOriginalsAsync();
        Assert.AreEqual("later occupant", await File.ReadAllTextAsync(occupant));
    }
}
