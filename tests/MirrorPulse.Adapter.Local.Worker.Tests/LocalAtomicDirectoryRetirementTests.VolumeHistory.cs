extern alias Probe;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Adapter.Sdk;
using ProbeLocalAtomicDirectoryCheckpoint = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryCheckpoint;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

public sealed partial class LocalAtomicDirectoryRetirementTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task VolumeHistoryDistinguishesTransientSiblingBeforeTheOriginalParentFence(bool verifyVolumeHistory)
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync(fenceParentNamespace: true, verifyVolumeHistory: verifyVolumeHistory);
        var independent = await RunProbeAsync("--create-transient-peer", fixture, admission);
        Assert.AreEqual(0, independent.ExitCode, independent.Output);
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.Tree)!, "unowned-transient-peer.bin")));

        if (verifyVolumeHistory)
        {
            var refused = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RetireAsync(admission));
            Assert.AreEqual("OriginalAtomicDirectoryHistoryChanged", refused.Message);
            await fixture.AssertOriginalsAsync();
            Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
        }
        else
        {
            var version2 = await fixture.RetireAsync(admission);
            Assert.AreEqual(2, version2.Version);
            Assert.IsFalse(Directory.Exists(fixture.Tree));
            Assert.IsTrue(File.Exists(fixture.Receipt(admission)));
        }
    }

    [TestMethod]
    [DataRow(nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterOriginalsLocked))]
    [DataRow(nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterReceiptStaged))]
    public async Task VolumeHistoryFenceExcludesIndependentSiblingsThroughCommitAndReplaysTheSameReceipt(string checkpoint)
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync(verifyVolumeHistory: true);
        Assert.AreEqual(3, admission.Version);
        Assert.IsNotNull(admission.History);
        string outside = Path.Combine(fixture.State, "unrelated-history");
        await File.WriteAllTextAsync(outside, "owned state outside the original parent");
        File.Delete(outside);
        bool refused = false;
        var receipt = await fixture.RetireAsync(admission, async (point, _) =>
        {
            if (point.ToString() != checkpoint) return;
            var contender = await RunProbeAsync("--create-transient-peer", fixture, admission);
            Assert.AreEqual(91, contender.ExitCode, contender.Output);
            using var result = JsonDocument.Parse(contender.Output);
            Assert.AreEqual(6800, result.RootElement.GetProperty("hresult").GetInt32() & 0xffff);
            refused = true;
        });
        Assert.IsTrue(refused);
        Assert.AreEqual(3, receipt.Version);
        Assert.IsGreaterThanOrEqualTo(admission.History.Start.NextUsn, receipt.HistoryVerifiedThrough!.Value);
        Assert.IsFalse(Directory.Exists(fixture.Tree));
        Assert.AreEqual("unrelated peer", await File.ReadAllTextAsync(fixture.Peer));

        byte[] original = await File.ReadAllBytesAsync(fixture.Receipt(admission));
        Directory.CreateDirectory(fixture.Tree);
        string successor = Path.Combine(fixture.Tree, "later.bin");
        await File.WriteAllTextAsync(successor, "successor remains");
        var replayed = await RunProbeAsync("--retire", fixture, admission);
        Assert.AreEqual(0, replayed.ExitCode, replayed.Output);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(fixture.Receipt(admission)));
        Assert.AreEqual("successor remains", await File.ReadAllTextAsync(successor));
    }

    [TestMethod]
    [DataRow(nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterOriginalsLocked))]
    [DataRow(nameof(ProbeLocalAtomicDirectoryCheckpoint.AfterReceiptStaged))]
    public async Task VolumeHistoryRetainsTheOriginalRangeAfterWorkerProcessDeathBeforeCommit(string checkpoint)
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync(verifyVolumeHistory: true);
        var first = await RunProbeAsync("--retire", fixture, admission, checkpoint);
        Assert.AreEqual(73, first.ExitCode, first.Output);
        await fixture.AssertOriginalsAsync();
        Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
        var recovered = await RunProbeAsync("--retire", fixture, admission);
        Assert.AreEqual(0, recovered.ExitCode, recovered.Output);
        Assert.IsFalse(Directory.Exists(fixture.Tree));
        Assert.IsTrue(File.Exists(fixture.Receipt(admission)));
    }

    [TestMethod]
    public async Task VolumeHistoryFenceProtectsTheDirectParentFromAPreexistingMetadataWriter()
    {
        using var fixture = new Fixture();
        string parent = Path.GetDirectoryName(fixture.Tree)!;
        using var writer = MetadataWriter.Open(parent);
        if (writer.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        long original = Directory.GetLastWriteTimeUtc(parent).AddMinutes(1).ToFileTimeUtc();
        if (!MetadataWriter.SetFileTime(writer, IntPtr.Zero, IntPtr.Zero, in original))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var admission = await fixture.PrepareAsync(verifyVolumeHistory: true);

        bool attempted = false;
        long contender = original + TimeSpan.FromMinutes(1).Ticks;
        try
        {
            var receipt = await fixture.RetireAsync(admission, (point, _) =>
            {
                if (point != ProbeLocalAtomicDirectoryCheckpoint.AfterOriginalsLocked) return ValueTask.CompletedTask;
                attempted = true;
                bool wrote = MetadataWriter.SetFileTime(writer, IntPtr.Zero, IntPtr.Zero, in contender);
                int nativeError = Marshal.GetLastPInvokeError();
                Assert.IsFalse(wrote);
                Assert.IsTrue(nativeError is 5 or 32 or 6800);
                return ValueTask.CompletedTask;
            });
            Assert.IsTrue(attempted);
            Assert.IsTrue(File.Exists(fixture.Receipt(admission)));
            Assert.AreEqual(3, receipt.Version);
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 5 or 32 or 6800)
        {
            Assert.IsFalse(attempted);
            await fixture.AssertOriginalsAsync();
            Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
            Assert.AreEqual(original, Directory.GetLastWriteTimeUtc(parent).ToFileTimeUtc());
        }
    }

    [TestMethod]
    [DataRow("journal")]
    [DataRow("future-cursor")]
    public async Task VolumeHistoryCannotRebaseASealedButUnavailableOriginalRange(string change)
    {
        using var fixture = new Fixture();
        var admission = await fixture.PrepareAsync(verifyVolumeHistory: true);
        Assert.IsNotNull(admission.History);
        var start = admission.History.Start;
        var unavailable = change == "journal"
            ? start with { JournalId = start.JournalId ^ 1UL }
            : start with { NextUsn = long.MaxValue };
        var retained = admission with { Fingerprint = string.Empty, History = admission.History with { Start = unavailable } };
        retained = retained with { Fingerprint = Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(retained))) };
        string state = Path.Combine(fixture.State, retained.AdmissionId.ToString("N") + ".admission.json");
        await File.WriteAllBytesAsync(state, AdapterProtocolJson.Encode(retained));

        var refusal = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RetireAsync(retained));
        Assert.AreEqual("AtomicDirectoryJournalRangeLost", refusal.Message);
        await fixture.AssertOriginalsAsync();
        Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
    }
}
