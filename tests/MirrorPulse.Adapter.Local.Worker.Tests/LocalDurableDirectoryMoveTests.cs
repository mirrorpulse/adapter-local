using MirrorPulse.Adapter.Local.Worker;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

[TestClass]
public sealed class LocalDurableDirectoryMoveTests
{
    [TestMethod]
    public async Task AnAcceptedReceiptRemainsReadableAfterBothOriginalParentsAreGone()
    {
        using var fixture = new Fixture();
        string sourceParent = Path.Combine(fixture.Source, "old-parent");
        string destinationParent = Path.Combine(fixture.Source, "new-parent");
        Directory.CreateDirectory(sourceParent);
        Directory.CreateDirectory(destinationParent);
        Directory.Move(fixture.Tree, Path.Combine(sourceParent, "tree"));
        var request = new AdapterOperationRequest(Guid.NewGuid(), "docs", "old-parent/tree", "docs", "new-parent/moved",
            new(await LocalFileOperations.RevisionAsync(fixture.Paths, "old-parent/tree", CancellationToken.None), true), true);
        string revision = await fixture.MoveAsync(request);
        Directory.Delete(sourceParent);
        Directory.Move(destinationParent, Path.Combine(fixture.Source, "later-parent"));
        Assert.AreEqual(revision, await fixture.MoveAsync(request));
        Assert.IsFalse(Directory.Exists(sourceParent));
        Assert.IsFalse(Directory.Exists(destinationParent));
        Assert.AreEqual("original bytes", await File.ReadAllTextAsync(Path.Combine(fixture.Source, "later-parent", "moved", "nested", "leaf.bin")));
    }

    [TestMethod]
    public async Task CommittedContainerMoveReplaysItsReceiptAndPreservesLaterSourceAndTargetGenerations()
    {
        using var fixture = new Fixture();
        var request = await fixture.RequestAsync();
        string accepted = await fixture.MoveAsync(request);
        Assert.AreEqual(request.Preconditions!.ExpectedRevision, accepted);
        await fixture.AssertMovedAsync("original bytes");
        byte[] receipt = await File.ReadAllBytesAsync(fixture.Receipt(request));
        Directory.Move(fixture.Destination, Path.Combine(fixture.Source, "earlier-result"));
        Directory.CreateDirectory(fixture.Tree);
        Directory.CreateDirectory(fixture.Destination);
        await File.WriteAllTextAsync(Path.Combine(fixture.Tree, "later.bin"), "later source");
        await File.WriteAllTextAsync(Path.Combine(fixture.Destination, "later.bin"), "later target");
        Assert.AreEqual(accepted, await fixture.MoveAsync(request));
        CollectionAssert.AreEqual(receipt, await File.ReadAllBytesAsync(fixture.Receipt(request)));
        Assert.AreEqual("later source", await File.ReadAllTextAsync(Path.Combine(fixture.Tree, "later.bin")));
        Assert.AreEqual("later target", await File.ReadAllTextAsync(Path.Combine(fixture.Destination, "later.bin")));
    }

    [TestMethod]
    [DataRow(nameof(LocalDirectoryMoveCheckpoint.AfterSourceChecked))]
    [DataRow(nameof(LocalDirectoryMoveCheckpoint.AfterNativeStaged))]
    [DataRow(nameof(LocalDirectoryMoveCheckpoint.AfterReceiptStaged))]
    public async Task BeforeCommitFailureRollsBackTheContainerAndDoesNotAcceptAReceipt(string stage)
    {
        using var fixture = new Fixture();
        var request = await fixture.RequestAsync();
        var cut = Enum.Parse<LocalDirectoryMoveCheckpoint>(stage);
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.MoveAsync(request,
            (point, _) => point == cut ? ValueTask.FromException(new IOException("Synthetic precommit interruption.")) : ValueTask.CompletedTask));
        Assert.IsFalse(Directory.Exists(fixture.Destination));
        Assert.IsFalse(File.Exists(fixture.Receipt(request)));
        Assert.AreEqual("original bytes", await File.ReadAllTextAsync(fixture.Leaf));
        await fixture.MoveAsync(request);
        await fixture.AssertMovedAsync("original bytes");
    }

    [TestMethod]
    public async Task LostReturnAfterCommitRetainsTheOriginalReceiptForAReopenedCaller()
    {
        using var fixture = new Fixture();
        var request = await fixture.RequestAsync();
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.MoveAsync(request,
            (point, _) => point == LocalDirectoryMoveCheckpoint.AfterCommit
                ? ValueTask.FromException(new IOException("Synthetic lost successful return.")) : ValueTask.CompletedTask));
        await fixture.AssertMovedAsync("original bytes");
        byte[] receipt = await File.ReadAllBytesAsync(fixture.Receipt(request));
        Assert.AreEqual(request.Preconditions!.ExpectedRevision, await fixture.MoveAsync(request));
        CollectionAssert.AreEqual(receipt, await File.ReadAllBytesAsync(fixture.Receipt(request)));
    }

    [TestMethod]
    public async Task LostReceiptAndARenameBackCannotReexecuteTheOldIntentAgainstALaterNamespaceGeneration()
    {
        using var fixture = new Fixture();
        var request = await fixture.RequestAsync();
        await fixture.MoveAsync(request);
        Directory.Move(fixture.Destination, fixture.Tree);
        Assert.AreEqual(request.Preconditions!.ExpectedRevision,
            await LocalFileOperations.RevisionAsync(fixture.Paths, "tree", CancellationToken.None));
        File.Delete(fixture.Receipt(request));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.MoveAsync(request));
        Assert.AreEqual("original bytes", await File.ReadAllTextAsync(fixture.Leaf));
        Assert.IsFalse(Directory.Exists(fixture.Destination));
    }

    [TestMethod]
    public async Task ContainerMovePreservesALaterInPlaceChildWriteInsteadOfSnapshottingOldContent()
    {
        using var fixture = new Fixture();
        var request = await fixture.RequestAsync();
        await fixture.MoveAsync(request, async (point, token) =>
        {
            if (point == LocalDirectoryMoveCheckpoint.AfterSourceChecked)
                await File.WriteAllTextAsync(fixture.Leaf, "later child content", token);
        });
        await fixture.AssertMovedAsync("later child content");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExistingDestinationRemainsIntactAndNoReceiptIsAccepted(bool directory)
    {
        using var fixture = new Fixture();
        var request = await fixture.RequestAsync();
        string occupant = directory ? Path.Combine(fixture.Destination, "later.bin") : fixture.Destination;
        if (directory) Directory.CreateDirectory(fixture.Destination);
        await File.WriteAllTextAsync(occupant, "unowned destination");
        await Assert.ThrowsExactlyAsync<NativeFileException>(() => fixture.MoveAsync(request));
        Assert.IsFalse(File.Exists(fixture.Receipt(request)));
        Assert.AreEqual("original bytes", await File.ReadAllTextAsync(fixture.Leaf));
        Assert.AreEqual("unowned destination", await File.ReadAllTextAsync(occupant));
    }

    [TestMethod]
    [DataRow("destination")]
    [DataRow("revision")]
    [DataRow("instance")]
    [DataRow("receipt")]
    public async Task AnAcceptedReceiptCannotBeReboundToAnotherRequestOrChangedPayload(string damage)
    {
        using var fixture = new Fixture();
        var request = await fixture.RequestAsync();
        await fixture.MoveAsync(request);
        if (damage == "destination") request = request with { DestinationPath = "different" };
        if (damage == "revision") request = request with { Preconditions = request.Preconditions! with { ExpectedRevision = "other" } };
        if (damage == "instance") fixture.Instance = Guid.NewGuid();
        if (damage == "receipt") await File.WriteAllTextAsync(fixture.Receipt(request), "{}");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.MoveAsync(request));
        await fixture.AssertMovedAsync("original bytes");
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Source, "different")));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mp-local-durable-move-" + Guid.NewGuid().ToString("N"));
        internal string Source { get; }
        internal string State { get; }
        internal string Tree => Path.Combine(Source, "tree");
        internal string Destination => Path.Combine(Source, "moved");
        internal string Leaf => Path.Combine(Tree, "nested", "leaf.bin");
        internal Guid Instance { get; set; } = Guid.NewGuid();
        internal LocalWorkerPaths Paths { get; }
        internal Fixture()
        {
            Source = Path.Combine(_root, "source"); State = Path.Combine(_root, "state");
            Directory.CreateDirectory(Path.Combine(Tree, "nested")); Directory.CreateDirectory(State);
            File.WriteAllText(Leaf, "original bytes");
            Paths = new(Source);
        }
        internal async Task<AdapterOperationRequest> RequestAsync() => new(Guid.NewGuid(), "docs", "tree", "docs", "moved",
            new(await LocalFileOperations.RevisionAsync(Paths, "tree", CancellationToken.None), true), true);
        internal Task<string> MoveAsync(AdapterOperationRequest operation,
            Func<LocalDirectoryMoveCheckpoint, CancellationToken, ValueTask>? checkpoint = null) =>
            LocalFileOperations.MoveDirectoryDurablyAsync(Instance, Paths, Paths, operation, State, checkpoint);
        internal string Receipt(AdapterOperationRequest operation) =>
            Path.Combine(State, "directory-moves", operation.OperationId.ToString("N") + ".accepted.json");
        internal async Task AssertMovedAsync(string expected)
        {
            Assert.IsFalse(Directory.Exists(Tree));
            Assert.AreEqual(expected, await File.ReadAllTextAsync(Path.Combine(Destination, "nested", "leaf.bin")));
        }
        public void Dispose()
        {
            string full = Path.GetFullPath(_root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("mp-local-durable-move-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its original temporary root.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
