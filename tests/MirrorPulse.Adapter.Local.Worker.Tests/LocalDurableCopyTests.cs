using System.Security.Cryptography;
using MirrorPulse.Adapter.Local.Worker;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

[TestClass]
public sealed class LocalDurableCopyTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LostReturnReplaysOriginalReceiptAndLeavesAReplacementUntouched(bool directory)
    {
        using var fixture = new Fixture(directory);
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.PublishAsync(recovering: false,
            LocalDurableCopyCheckpoint.AfterCommit));
        Assert.IsTrue(File.Exists(fixture.Receipt));
        Assert.IsFalse(File.Exists(fixture.Intent));
        if (!directory) CollectionAssert.AreEqual(Fixture.OriginalBytes, await File.ReadAllBytesAsync(fixture.Target));
        string? accepted = await LocalFileOperations.RevisionAsync(fixture.Paths, fixture.Request.Path, CancellationToken.None);
        Assert.IsNotNull(accepted);
        if (directory) Directory.Move(fixture.Target, fixture.Target + ".previous");
        else File.Move(fixture.Target, fixture.Target + ".previous");
        await File.WriteAllBytesAsync(fixture.Target, [9, 8, 7]);

        byte[] receipt = await File.ReadAllBytesAsync(fixture.Receipt);
        Assert.AreEqual(accepted, await fixture.PublishAsync(recovering: true));
        CollectionAssert.AreEqual(receipt, await File.ReadAllBytesAsync(fixture.Receipt));
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(fixture.Target));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PrecommitInterruptionLeavesOnlyTheOriginalIntentAndPermitsRecovery(bool directory)
    {
        using var fixture = new Fixture(directory);
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.PublishAsync(recovering: false,
            LocalDurableCopyCheckpoint.AfterReceiptStaged));
        Assert.IsTrue(File.Exists(fixture.Intent));
        Assert.IsFalse(File.Exists(fixture.Receipt));
        Assert.IsFalse(File.Exists(fixture.Target) || Directory.Exists(fixture.Target));
        await fixture.PublishAsync(recovering: true);
        Assert.IsFalse(File.Exists(fixture.Intent));
        Assert.IsTrue(File.Exists(fixture.Receipt));
        if (directory) Assert.IsTrue(Directory.Exists(fixture.Target));
        else CollectionAssert.AreEqual(Fixture.OriginalBytes, await File.ReadAllBytesAsync(fixture.Target));
    }

    [TestMethod]
    public async Task RecoveryWithoutOriginalEvidenceDoesNotAdoptOrCreateTargets()
    {
        using var fixture = new Fixture(directory: false);
        var missing = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.PublishAsync(recovering: true));
        Assert.AreEqual("MutationOutcomeAmbiguous", missing.Message);
        Assert.IsFalse(File.Exists(fixture.Target));
        await fixture.PublishAsync(recovering: false);
        File.Delete(fixture.Receipt);
        File.Delete(fixture.Target);
        missing = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.PublishAsync(recovering: true));
        Assert.AreEqual("MutationOutcomeAmbiguous", missing.Message);
        Assert.IsFalse(File.Exists(fixture.Target));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mp-local-durable-copy-" + Guid.NewGuid().ToString("N"));
        internal static readonly byte[] OriginalBytes = [1, 2, 3, 5, 8, 13];
        internal Guid Instance { get; } = Guid.NewGuid();
        internal LocalWorkerPaths Paths { get; }
        internal AdapterOperationRequest Request { get; }
        internal string State => Path.Combine(_root, "state");
        internal string Target => Path.Combine(Paths.Root, Request.Path);
        internal string Intent => Path.Combine(State, "directory-copies", Request.OperationId.ToString("N") + ".pending.json");
        internal string Receipt => Path.Combine(State, "directory-copies", Request.OperationId.ToString("N") + ".accepted.json");

        internal Fixture(bool directory)
        {
            Directory.CreateDirectory(State);
            string backend = Path.Combine(_root, "backend");
            Directory.CreateDirectory(backend);
            Paths = new(backend);
            Request = new(Guid.NewGuid(), "docs", "copied", Preconditions: new(null, true), IsDirectory: directory);
        }

        internal async Task<string> PublishAsync(bool recovering, LocalDurableCopyCheckpoint? cut = null)
        {
            using var data = new MemoryStream(OriginalBytes, writable: false);
            LocalDurableCopyContent? content = Request.IsDirectory ? null :
                new(data, data.Length, Convert.ToHexString(SHA256.HashData(OriginalBytes)));
            return await LocalFileOperations.PublishDurableCopyAsync(Instance, Paths, Request, State, content, recovering,
                (point, _) => point == cut ? ValueTask.FromException(new IOException("Fixture interruption.")) : ValueTask.CompletedTask);
        }

        public void Dispose()
        {
            string full = Path.GetFullPath(_root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("mp-local-durable-copy-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its original temporary root.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
