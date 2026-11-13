using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

[TestClass]
public sealed class LocalDurableDirectoryMoveProcessTests
{
    [TestMethod]
    public async Task AnExistingHostThatDoesNotSelectDurableMoveKeepsStandardDirectoryMove()
    {
        using var fixture = new Fixture();
        await using var worker = await LocalWorkerSession.StartWithStateAsync(fixture.Root, fixture.Instance);
        var operation = await Fixture.RequestAsync(worker);
        var response = await worker.RequestAsync("Move", operation);
        Assert.AreEqual("MutationComplete", response.MessageType);
        Assert.IsFalse(File.Exists(fixture.Receipt(operation)));
        await fixture.AssertMovedAsync();
    }

    [TestMethod]
    public async Task TheDurableRequestCannotFallBackOnASessionThatDidNotSelectIt()
    {
        using var fixture = new Fixture();
        await using var worker = await LocalWorkerSession.StartWithStateAsync(fixture.Root, fixture.Instance, selectDurableMove: false);
        var operation = await Fixture.RequestAsync(worker);
        var response = await worker.RequestAsync("Move", Durable(operation));
        Assert.AreEqual("OperationError", response.MessageType);
        Assert.AreEqual("CapabilityUnavailable", response.Payload.GetProperty("code").GetString());
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Source, "moved")));
        Assert.IsFalse(File.Exists(fixture.Receipt(operation)));
    }

    [TestMethod]
    public async Task UnreadFirstReplyRecoversAcrossWorkerAndCacheOwnersWithoutTouchingALaterSource()
    {
        using var fixture = new Fixture();
        AdapterOperationRequest operation;
        await using (var first = await LocalWorkerSession.StartWithStateAsync(fixture.Root, fixture.Instance))
        {
            operation = await Fixture.RequestAsync(first);
            await first.SendAsync("Move", Guid.NewGuid(), Durable(operation));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(fixture.Receipt(operation))) await Task.Delay(20, deadline.Token);
            // Deliberately do not read the accepted response before releasing the actual Worker and pipe.
        }
        await fixture.AssertMovedAsync();
        byte[] receipt = await File.ReadAllBytesAsync(fixture.Receipt(operation));
        Directory.CreateDirectory(fixture.Tree);
        await File.WriteAllTextAsync(Path.Combine(fixture.Tree, "later.bin"), "later source generation");
        string cache = Path.Combine(fixture.Root, "transfers");
        if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        await using var second = await LocalWorkerSession.StartWithStateAsync(fixture.Root, fixture.Instance);
        var response = await second.RequestAsync("Move", Durable(operation));
        Assert.AreEqual("MutationComplete", response.MessageType);
        Assert.AreEqual(operation.Preconditions!.ExpectedRevision, response.Payload.GetProperty("revision").GetString());
        CollectionAssert.AreEqual(receipt, await File.ReadAllBytesAsync(fixture.Receipt(operation)));
        Assert.AreEqual("later source generation", await File.ReadAllTextAsync(Path.Combine(fixture.Tree, "later.bin")));
        Assert.AreEqual("original bytes", await File.ReadAllTextAsync(Path.Combine(fixture.Source, "moved", "nested", "leaf.bin")));
    }

    [TestMethod]
    public async Task RestartedWorkerCannotReuseAMoveIdForAnotherDestination()
    {
        using var fixture = new Fixture();
        AdapterOperationRequest operation;
        await using (var first = await LocalWorkerSession.StartWithStateAsync(fixture.Root, fixture.Instance))
        {
            operation = await Fixture.RequestAsync(first);
            Assert.AreEqual("MutationComplete", (await first.RequestAsync("Move", Durable(operation))).MessageType);
        }
        await using var second = await LocalWorkerSession.StartWithStateAsync(fixture.Root, fixture.Instance);
        var response = await second.RequestAsync("Move", Durable(operation with { DestinationPath = "different" }));
        Assert.AreEqual("OperationError", response.MessageType);
        Assert.AreEqual("OperationBindingMismatch", response.Payload.GetProperty("code").GetString());
        await fixture.AssertMovedAsync();
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Source, "different")));
    }

    [TestMethod]
    [DataRow("Delete")]
    [DataRow("CreateDirectory")]
    public async Task ACommittedMoveIdCannotBeReusedByAnotherMutationAfterWorkerRestart(string type)
    {
        using var fixture = new Fixture();
        AdapterOperationRequest operation;
        await using (var first = await LocalWorkerSession.StartWithStateAsync(fixture.Root, fixture.Instance))
        {
            operation = await Fixture.RequestAsync(first);
            Assert.AreEqual("MutationComplete", (await first.RequestAsync("Move", Durable(operation))).MessageType);
        }
        await using var second = await LocalWorkerSession.StartWithStateAsync(fixture.Root, fixture.Instance);
        object request = type == "CreateDirectory"
            ? new AdapterCreateDirectoryRequest(operation.OperationId, "docs", "other")
            : new AdapterOperationRequest(operation.OperationId, "docs", "moved", Preconditions: operation.Preconditions, IsDirectory: true);
        var response = await second.RequestAsync(type, request);
        Assert.AreEqual("OperationError", response.MessageType);
        Assert.AreEqual("OperationBindingMismatch", response.Payload.GetProperty("code").GetString());
        await fixture.AssertMovedAsync();
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Source, "other")));
    }

    [TestMethod]
    public async Task AStateGrantInsideTheSourceIsRefusedBeforeAnyDirectoryEffect()
    {
        using var fixture = new Fixture();
        await using var worker = await LocalWorkerSession.StartWithStateAsync(fixture.Root, fixture.Instance, Path.Combine(fixture.Source, "state"));
        var operation = await Fixture.RequestAsync(worker);
        var response = await worker.RequestAsync("Move", Durable(operation));
        Assert.AreEqual("OperationError", response.MessageType);
        Assert.AreEqual("DurableDirectoryMoveStateMustBePrivate", response.Payload.GetProperty("code").GetString());
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Source, "moved")));
        Assert.AreEqual("original bytes", await File.ReadAllTextAsync(Path.Combine(fixture.Tree, "nested", "leaf.bin")));
    }

    private static object Durable(AdapterOperationRequest operation) => new
    {
        operation.OperationId,
        operation.RootKey,
        operation.Path,
        operation.DestinationRootKey,
        operation.DestinationPath,
        operation.Preconditions,
        operation.IsDirectory,
        durableDirectoryMove = true,
    };

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mp-local-durable-wire-" + Guid.NewGuid().ToString("N"));
        internal Guid Instance { get; } = Guid.NewGuid();
        internal string Source => Path.Combine(Root, "source");
        internal string Tree => Path.Combine(Source, "tree");
        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Tree, "nested"));
            File.WriteAllText(Path.Combine(Tree, "nested", "leaf.bin"), "original bytes");
        }
        internal static async Task<AdapterOperationRequest> RequestAsync(LocalWorkerSession worker)
        {
            var stat = await worker.RequestAsync("Stat", new { rootKey = "docs", path = "tree" });
            Assert.AreEqual("StatResult", stat.MessageType);
            return new(Guid.NewGuid(), "docs", "tree", "docs", "moved", new(stat.Payload.GetProperty("revision").GetString(), true), true);
        }
        internal string Receipt(AdapterOperationRequest operation) => Path.Combine(Root, "state", "directory-moves", operation.OperationId.ToString("N") + ".accepted.json");
        internal async Task AssertMovedAsync()
        {
            Assert.IsFalse(Directory.Exists(Tree));
            Assert.AreEqual("original bytes", await File.ReadAllTextAsync(Path.Combine(Source, "moved", "nested", "leaf.bin")));
        }
        public void Dispose()
        {
            string full = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("mp-local-durable-wire-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its original temporary root.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
