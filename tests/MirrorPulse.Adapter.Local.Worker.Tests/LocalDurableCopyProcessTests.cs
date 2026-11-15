using MirrorPulse.Adapter.Local.Worker;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

[TestClass]
public sealed class LocalDurableCopyProcessTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnUnreadCopyResultRetainsItsRevisionAcrossActualWorkerProcesses(bool directory)
    {
        using var fixture = new Fixture();
        Guid operationId = Guid.NewGuid();
        await using (var worker = await LocalWorkerSession.StartWithStateAsync(fixture.Root, fixture.Instance, selectDurableCopy: true))
        {
            await SendCopyAsync(worker, operationId, directory, "start");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(fixture.Receipt(operationId))) await Task.Delay(15, deadline.Token);
        }
        var paths = new LocalWorkerPaths(fixture.Source);
        string? accepted = await LocalFileOperations.RevisionAsync(paths, "copied", CancellationToken.None);
        Assert.IsNotNull(accepted);
        byte[] originalReceipt = await File.ReadAllBytesAsync(fixture.Receipt(operationId));
        if (directory) Directory.Move(fixture.Target, fixture.Target + ".previous");
        else File.Move(fixture.Target, fixture.Target + ".previous");
        await File.WriteAllBytesAsync(fixture.Target, [16, 32, 64]);

        await using var restarted = await LocalWorkerSession.StartWithStateAsync(fixture.Root, fixture.Instance, selectDurableCopy: true);
        Guid replyId = await SendCopyAsync(restarted, operationId, directory, "resume");
        var result = await restarted.ReadAsync();
        Assert.AreEqual(replyId, result.RequestId);
        Assert.IsTrue(result.IsResponse);
        Assert.AreEqual(directory ? "MutationComplete" : "UploadComplete", result.MessageType);
        Assert.AreEqual(operationId, result.Payload.GetProperty("operationId").GetGuid());
        Assert.AreEqual(accepted, result.Payload.GetProperty("revision").GetString());
        CollectionAssert.AreEqual(originalReceipt, await File.ReadAllBytesAsync(fixture.Receipt(operationId)));
        CollectionAssert.AreEqual(new byte[] { 16, 32, 64 }, await File.ReadAllBytesAsync(fixture.Target));
        var conflicting = await restarted.RequestAsync("CreateDirectory", new { operationId, rootKey = "docs", path = "other", mustBeAbsent = true });
        Assert.AreEqual("OperationError", conflicting.MessageType);
        Assert.AreEqual("OperationBindingMismatch", conflicting.Payload.GetProperty("code").GetString());
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Source, "other")));
    }

    [TestMethod]
    public async Task ANewWorkerCannotRecoverAnUnknownOperationOrUseAnUnnegotiatedMode()
    {
        using var fixture = new Fixture();
        var request = new { operationId = Guid.NewGuid(), rootKey = "docs", path = "copied", mustBeAbsent = true, durableDirectoryCopy = "resume" };
        await using (var ordinary = await LocalWorkerSession.StartWithStateAsync(fixture.Root, fixture.Instance))
        {
            var unavailable = await ordinary.RequestAsync("CreateDirectory", request);
            Assert.AreEqual("OperationError", unavailable.MessageType);
            Assert.AreEqual("CapabilityUnavailable", unavailable.Payload.GetProperty("code").GetString());
        }
        await using var selected = await LocalWorkerSession.StartWithStateAsync(fixture.Root, fixture.Instance, selectDurableCopy: true);
        var unknown = await selected.RequestAsync("CreateDirectory", request);
        Assert.AreEqual("OperationError", unknown.MessageType);
        Assert.AreEqual("MutationOutcomeAmbiguous", unknown.Payload.GetProperty("code").GetString());
        Assert.IsFalse(Directory.Exists(fixture.Target));
    }

    private static async Task<Guid> SendCopyAsync(LocalWorkerSession worker, Guid operationId, bool directory, string mode)
    {
        Guid request = Guid.NewGuid();
        if (directory)
        {
            await worker.SendAsync("CreateDirectory", request, new
            { operationId, rootKey = "docs", path = "copied", mustBeAbsent = true, durableDirectoryCopy = mode });
        }
        else
        {
            Guid streamId = Guid.NewGuid();
            byte[] content = [1, 2, 3, 5, 8];
            await worker.SendAsync("Upload", request, new
            {
                operationId,
                rootKey = "docs",
                path = "copied",
                streamId,
                length = content.Length,
                preconditions = new { destinationMustBeAbsent = true },
                durableDirectoryCopy = mode
            });
            var ready = await worker.ReadAsync();
            Assert.AreEqual("UploadReady", ready.MessageType);
            Assert.AreEqual(request, ready.RequestId);
            await worker.SendChunkAsync(request, streamId, "docs", 0, content, last: true);
        }
        return request;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mp-local-copy-wire-" + Guid.NewGuid().ToString("N"));
        internal Guid Instance { get; } = Guid.NewGuid();
        internal string Source => Path.Combine(Root, "source");
        internal string Target => Path.Combine(Source, "copied");
        internal string Receipt(Guid operationId) =>
            Path.Combine(Root, "state", "directory-copies", operationId.ToString("N") + ".accepted.json");

        public void Dispose()
        {
            string full = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("mp-local-copy-wire-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its original temporary root.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
