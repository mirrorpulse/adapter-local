extern alias Probe;
using System.Diagnostics;
using System.Text.Json;
using LocalAtomicDirectoryAdmission = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryAdmission;
using LocalAtomicDirectoryCheckpoint = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryCheckpoint;
using LocalAtomicDirectoryEffect = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryEffect;
using LocalAtomicDirectoryEntry = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryEntry;
using LocalAtomicDirectoryReceipt = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryReceipt;
using ProbeFileOperations = Probe::MirrorPulse.Adapter.Local.Worker.LocalFileOperations;
using ProbeMarker = Probe::MirrorPulse.Adapter.Local.DirectoryEffectsProbe.ProbeMarker;
using ProbeWorkerPaths = Probe::MirrorPulse.Adapter.Local.Worker.LocalWorkerPaths;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

public sealed partial class LocalAtomicDirectoryRetirementTests
{
    [TestMethod]
    [DataRow(nameof(LocalAtomicDirectoryCheckpoint.AfterOriginalsLocked))]
    [DataRow(nameof(LocalAtomicDirectoryCheckpoint.AfterFirstChildStaged))]
    [DataRow(nameof(LocalAtomicDirectoryCheckpoint.AfterParentStaged))]
    [DataRow(nameof(LocalAtomicDirectoryCheckpoint.AfterReceiptStaged))]
    public async Task ActualProcessExitBeforeCommitRollsBackAndANewProcessCompletesTheOriginalBatch(string cut)
    {
        using var fixture = new Fixture(); var admission = await fixture.PrepareAsync();
        var stopped = await RunProbeAsync("--retire", fixture, admission, cut);
        Assert.AreEqual(73, stopped.ExitCode);
        await fixture.AssertOriginalsAsync(); Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
        var resumed = await RunProbeAsync("--retire", fixture, admission);
        Assert.AreEqual(0, resumed.ExitCode, resumed.Output);
        Assert.IsFalse(Directory.Exists(fixture.Tree)); Assert.IsTrue(File.Exists(fixture.Receipt(admission)));
        Assert.AreEqual("unrelated peer", await File.ReadAllTextAsync(fixture.Peer));
    }

    [TestMethod]
    public async Task ActualProcessExitAfterCommitReplaysTheSameReceiptAndPreservesALaterGeneration()
    {
        using var fixture = new Fixture(); var admission = await fixture.PrepareAsync();
        var stopped = await RunProbeAsync("--retire", fixture, admission, nameof(LocalAtomicDirectoryCheckpoint.AfterCommit));
        Assert.AreEqual(73, stopped.ExitCode); Assert.IsFalse(Directory.Exists(fixture.Tree));
        string original = await File.ReadAllTextAsync(fixture.Receipt(admission));
        Directory.CreateDirectory(fixture.Tree); await File.WriteAllTextAsync(Path.Combine(fixture.Tree, "new.bin"), "new independent generation");
        var replayed = await RunProbeAsync("--retire", fixture, admission); Assert.AreEqual(0, replayed.ExitCode, replayed.Output);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Receipt(admission)));
        Assert.AreEqual("new independent generation", await File.ReadAllTextAsync(Path.Combine(fixture.Tree, "new.bin")));
        using var result = JsonDocument.Parse(replayed.Output);
        Assert.AreEqual(4, result.RootElement.GetProperty("effects").GetInt32());
        Assert.AreEqual(admission.AdmissionId, result.RootElement.GetProperty("admissionId").GetGuid());
    }

    [TestMethod]
    [DataRow("--delete-original")]
    [DataRow("--overwrite-original")]
    public async Task IndependentProcessCannotMutateAnOriginalAfterTheWholeTransactionLocksIt(string action)
    {
        using var fixture = new Fixture(); var admission = await fixture.PrepareAsync(); bool refused = false;
        var receipt = await fixture.RetireAsync(admission, async (point, _) =>
        {
            if (point != LocalAtomicDirectoryCheckpoint.AfterOriginalsLocked) return;
            var contender = await RunProbeAsync(action, fixture, admission);
            Assert.AreEqual(91, contender.ExitCode); refused = true;
        });
        Assert.IsTrue(refused); Assert.HasCount(4, receipt.Effects); Assert.IsFalse(Directory.Exists(fixture.Tree));
        Assert.AreEqual("unrelated peer", await File.ReadAllTextAsync(fixture.Peer));
    }

    [TestMethod]
    public async Task IndependentChildCreationBeforeParentRetirementRefusesTheEntireBatch()
    {
        using var fixture = new Fixture(); var admission = await fixture.PrepareAsync(); bool created = false;
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => fixture.RetireAsync(admission, async (point, _) =>
        {
            if (point != LocalAtomicDirectoryCheckpoint.AfterFirstChildStaged) return;
            var contender = await RunProbeAsync("--create-extra", fixture, admission);
            Assert.AreEqual(0, contender.ExitCode, contender.Output); created = true;
        }));
        Assert.IsTrue(created); await fixture.AssertOriginalsAsync();
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Tree, "unowned.bin")));
        Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
    }

    [TestMethod]
    public async Task TransientUnownedChildExposesAnUnqualifiedParentHistoryDespiteMatchingFileUsn()
    {
        using var fixture = new Fixture(); var admission = await fixture.PrepareAsync();
        var receipt = await fixture.RetireAsync(admission, async (point, _) =>
        {
            if (point != LocalAtomicDirectoryCheckpoint.AfterOriginalsLocked) return;
            string path = Path.Combine(fixture.Tree, "unowned-transient.bin");
            await File.WriteAllTextAsync(path, "unowned transient mutation", CancellationToken.None); File.Delete(path);
        });
        Assert.HasCount(4, receipt.Effects);
        Assert.IsNull(typeof(global::MirrorPulse.Adapter.Local.Worker.LocalWorkerPaths).Assembly.GetType("MirrorPulse.Adapter.Local.Worker.AtomicDirectoryNative"));
        Assert.IsFalse(Directory.Exists(fixture.Tree));
    }

    [TestMethod]
    public async Task IndependentProcessCannotAddAChildAfterTheOriginalParentWasStagedForRetirement()
    {
        using var fixture = new Fixture(); var admission = await fixture.PrepareAsync(); bool refused = false;
        var receipt = await fixture.RetireAsync(admission, async (point, _) =>
        {
            if (point != LocalAtomicDirectoryCheckpoint.AfterParentStaged) return;
            var contender = await RunProbeAsync("--create-extra", fixture, admission);
            Assert.AreEqual(91, contender.ExitCode); refused = true;
        });
        Assert.IsTrue(refused); Assert.HasCount(4, receipt.Effects); Assert.IsFalse(Directory.Exists(fixture.Tree));
    }

    private static async Task<(int ExitCode, string Output)> RunProbeAsync(string action, Fixture fixture,
        LocalAtomicDirectoryAdmission admission, string? cut = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string value in new[] { typeof(ProbeMarker).Assembly.Location, action, fixture.DirectoryPath, admission.AdmissionId.ToString("N") })
            start.ArgumentList.Add(value);
        if (cut is not null) start.ArgumentList.Add(cut);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Original process probe could not start.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(deadline.Token); string text = await output; await error;
            Assert.IsLessThan(65536, text.Length); return (process.ExitCode, text.Trim());
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
    }
}
