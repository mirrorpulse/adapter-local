#if LOCAL_ATOMIC_DIRECTORY_CANDIDATE
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

[TestClass]
public sealed class LocalAtomicWorkerProcessTests
{
    [TestMethod]
    public async Task AtomicWorkerCommitsExactAdmittedOriginalsAndReplaysAfterProcessRestart()
    {
        using var fixture = new Fixture();
        AdapterAtomicDirectoryDeletionRequest request;
        AdapterAtomicDirectoryDeletionReceipt original;
        await using (var worker = await LocalWorkerSession.StartAtomicAsync(fixture.Root, fixture.Instance))
        {
            var admitted = await AdmitAsync(worker, await fixture.ReadRequestAsync(worker));
            request = DeleteRequest(admitted.Request, admitted.Admission, 'A');
            var invalidAdmission = admitted.Admission with
            {
                Operations = [admitted.Admission.Operations[0] with { OriginalGeneration = "another-generation" }, .. admitted.Admission.Operations.Skip(1)],
            };
            invalidAdmission = invalidAdmission with { AdmissionFingerprint = invalidAdmission.CalculateFingerprint() };
            var changed = DeleteRequest(admitted.Request, invalidAdmission, 'A');
            var refused = await worker.ExchangeAtomicAsync(AdapterAtomicDirectoryDeletionWire.RequestMessage, changed);
            AssertError(refused.Response, "LocalOriginalDirectoryAdmissionChanged");
            fixture.RequireOriginals();
            var exchange = await worker.ExchangeAtomicAsync(AdapterAtomicDirectoryDeletionWire.RequestMessage, request);
            original = AdapterAtomicDirectoryDeletionWire.ReadResponse(exchange.Request, exchange.Response, worker.AtomicCapabilities);
            Assert.HasCount(3, original.DeletedOriginals);
            Assert.IsFalse(Directory.Exists(fixture.Tree));
        }
        Directory.CreateDirectory(fixture.Tree);
        string successor = Path.Combine(fixture.Tree, "successor.bin");
        await File.WriteAllTextAsync(successor, "later bytes");
        await using (var worker = await LocalWorkerSession.StartAtomicAsync(fixture.Root, fixture.Instance))
        {
            var admitted = await AdmitAsync(worker, request.OriginalRequest);
            Assert.AreEqual(request.Admission.AdmissionFingerprint, admitted.Admission.AdmissionFingerprint);
            var exchange = await worker.ExchangeAtomicAsync(AdapterAtomicDirectoryDeletionWire.RequestMessage, request);
            var replayed = AdapterAtomicDirectoryDeletionWire.ReadResponse(exchange.Request, exchange.Response, worker.AtomicCapabilities);
            CollectionAssert.AreEqual(AdapterProtocolJson.Encode(original), AdapterProtocolJson.Encode(replayed));
            var changed = await worker.ExchangeAtomicAsync(AdapterAtomicDirectoryDeletionWire.RequestMessage,
                DeleteRequest(request.OriginalRequest, admitted.Admission, 'B'));
            AssertError(changed.Response, "OriginalAtomicDirectoryAuthorizationChanged");
            var owned = await worker.RequestAsync("Delete", request.OriginalRequest.Operations[0].Operation);
            AssertError(owned, "OriginalAtomicDirectoryOperationAlreadyOwned");
        }
        Assert.AreEqual("later bytes", await File.ReadAllTextAsync(successor));
        Assert.AreEqual("independent peer", await File.ReadAllTextAsync(Path.Combine(fixture.Source, "peer.txt")));
    }

    [TestMethod]
    [DataRow(false, true, true, "CapabilityUnavailable")]
    [DataRow(true, true, false, "CapabilityUnavailable")]
    [DataRow(true, false, true, "RootOffline")]
    public async Task AtomicWorkerRequiresGrantNegotiationAndActiveRoot(bool provideGrant, bool enabled, bool selectCapabilities, string expected)
    {
        using var fixture = new Fixture();
        var request = fixture.CreateRequest("an-original-revision", "an-original-revision", "an-original-revision");
        await using var worker = await LocalWorkerSession.StartAtomicAsync(fixture.Root, fixture.Instance,
            selectCapabilities, enabled, provideGrant);
        var exchange = await worker.ExchangeAtomicAsync(AdapterOriginalDirectoryWire.AdmissionRequestMessage, request);
        AssertError(exchange.Response, expected);
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.State));
        fixture.RequireOriginals();
    }

    [TestMethod]
    public async Task AtomicWorkerRefusesDeletionAfterTheRetainedAdmissionDisappears()
    {
        using var fixture = new Fixture();
        await using var worker = await LocalWorkerSession.StartAtomicAsync(fixture.Root, fixture.Instance);
        var admitted = await AdmitAsync(worker, await fixture.ReadRequestAsync(worker));
        File.Delete(Path.Combine(fixture.State, admitted.Request.AdmissionId.ToString("N") + ".admission.json"));
        var exchange = await worker.ExchangeAtomicAsync(AdapterAtomicDirectoryDeletionWire.RequestMessage,
            DeleteRequest(admitted.Request, admitted.Admission, 'A'));
        AssertError(exchange.Response, "RetainedOriginalDirectoryAdmissionRequired");
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.State));
        fixture.RequireOriginals();
    }

    [TestMethod]
    public async Task AtomicWorkerRecoversACommittedEffectWhenTheHostNeverReadsItsFirstReply()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        AdapterAtomicDirectoryDeletionRequest request;
        byte[] originalReceipt;
        await using (var worker = await LocalWorkerSession.StartAtomicAsync(fixture.Root, fixture.Instance))
        {
            var admitted = await AdmitAsync(worker, await fixture.ReadRequestAsync(worker));
            request = DeleteRequest(admitted.Request, admitted.Admission, 'A');
            await worker.SendAsync(AdapterAtomicDirectoryDeletionWire.RequestMessage, Guid.NewGuid(), request);
            string receiptPath = Path.Combine(fixture.State, admitted.Request.AdmissionId.ToString("N") + ".committed.json");
            while (!File.Exists(receiptPath)) await Task.Delay(20, deadline.Token);
            originalReceipt = await File.ReadAllBytesAsync(receiptPath, deadline.Token);
            Assert.IsFalse(Directory.Exists(fixture.Tree));
        }
        Directory.CreateDirectory(fixture.Tree);
        string successor = Path.Combine(fixture.Tree, "later.txt");
        await File.WriteAllTextAsync(successor, "later bytes", deadline.Token);
        await using var restarted = await LocalWorkerSession.StartAtomicAsync(fixture.Root, fixture.Instance);
        var exchange = await restarted.ExchangeAtomicAsync(AdapterAtomicDirectoryDeletionWire.RequestMessage, request);
        var recovered = AdapterAtomicDirectoryDeletionWire.ReadResponse(exchange.Request, exchange.Response, restarted.AtomicCapabilities);
        using var retained = System.Text.Json.JsonDocument.Parse(originalReceipt);
        Assert.AreEqual(retained.RootElement.GetProperty("transactionId").GetGuid(), recovered.ReceiptId);
        var evidence = AdapterProtocolJson.Decode<System.Text.Json.JsonElement>(Convert.FromBase64String(recovered.BackendEvidence));
        Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(originalReceipt)),
            evidence.GetProperty("nativeReceiptSha256").GetString());
        Assert.AreEqual("later bytes", await File.ReadAllTextAsync(successor, deadline.Token));
    }

    private static async Task<(AdapterDirectoryAdmissionRequest Request, AdapterDirectoryAdmission Admission)> AdmitAsync(
        LocalWorkerSession worker, AdapterDirectoryAdmissionRequest request)
    {
        var exchange = await worker.ExchangeAtomicAsync(AdapterOriginalDirectoryWire.AdmissionRequestMessage, request);
        return (request, AdapterOriginalDirectoryWire.ReadAdmissionResponse(exchange.Request, exchange.Response, worker.AtomicCapabilities));
    }

    private static AdapterAtomicDirectoryDeletionRequest DeleteRequest(AdapterDirectoryAdmissionRequest request,
        AdapterDirectoryAdmission admission, char manifest)
    {
        var deletion = new AdapterAtomicDirectoryDeletionRequest(1, request, admission, new string(manifest, 64), string.Empty);
        return deletion with { RequestFingerprint = deletion.CalculateFingerprint() };
    }

    private static void AssertError(AdapterControlFrame response, string expected)
    {
        Assert.AreEqual("OperationError", response.MessageType);
        Assert.AreEqual(expected, response.Payload.GetProperty("code").GetString());
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mp-local-atomic-" + Guid.NewGuid().ToString("N"));
        internal Guid Instance { get; } = Guid.NewGuid();
        internal string Source => Path.Combine(Root, "source");
        internal string State => Path.Combine(Root, "state");
        internal string Tree => Path.Combine(Source, "tree");

        internal Fixture()
        {
            Directory.CreateDirectory(Tree);
            Directory.CreateDirectory(State);
            File.WriteAllText(Path.Combine(Root, ".mp-atomic-test-fixture"), Path.GetFileName(Root));
            File.WriteAllText(Path.Combine(Tree, "first.txt"), "first original");
            File.WriteAllText(Path.Combine(Tree, "second.txt"), "second original");
            File.WriteAllText(Path.Combine(Source, "peer.txt"), "independent peer");
        }

        internal async Task<AdapterDirectoryAdmissionRequest> ReadRequestAsync(LocalWorkerSession worker)
        {
            async Task<string> ReadRevisionAsync(string path)
            {
                var response = await worker.RequestAsync("Stat", new { rootKey = "docs", path });
                Assert.AreEqual("StatResult", response.MessageType);
                return response.Payload.GetProperty("revision").GetString()!;
            }
            return CreateRequest(await ReadRevisionAsync("tree/first.txt"), await ReadRevisionAsync("tree/second.txt"),
                await ReadRevisionAsync("tree"));
        }

        internal AdapterDirectoryAdmissionRequest CreateRequest(string first, string second, string tree)
        {
            var request = new AdapterDirectoryAdmissionRequest(1, Instance, Guid.NewGuid(),
                [Operation("tree/second.txt", second), Operation("tree/first.txt", first), Operation("tree", tree, isDirectory: true)], string.Empty);
            return request with { RequestFingerprint = request.CalculateFingerprint() };
        }

        private static AdapterOriginalDirectoryOperation Operation(string path, string revision, bool isDirectory = false) =>
            new("Delete", new(Guid.NewGuid(), "docs", path, Preconditions: new(revision), IsDirectory: isDirectory));

        internal void RequireOriginals()
        {
            Assert.AreEqual("first original", File.ReadAllText(Path.Combine(Tree, "first.txt")));
            Assert.AreEqual("second original", File.ReadAllText(Path.Combine(Tree, "second.txt")));
        }

        public void Dispose()
        {
            string full = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup escaped its original parent.");
            Directory.Delete(full, recursive: true);
        }
    }
}
#endif
