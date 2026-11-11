extern alias Probe;
using System.Diagnostics;
using System.Text.Json;
using ProbeMarker = Probe::MirrorPulse.Adapter.Local.DirectoryEffectsProbe.ProbeMarker;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

[TestClass]
public sealed class LocalAtomicParentFenceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("read-only", "direct-path")]
    [DataRow("read-only", "nested-path")]
    [DataRow("read-only", "relative-root")]
    [DataRow("read-only", "relative-nested")]
    [DataRow("read-only", "preopened-leaf")]
    [DataRow("rename-back", "direct-path")]
    [DataRow("rename-back", "nested-path")]
    [DataRow("rename-back", "relative-root")]
    [DataRow("rename-back", "relative-nested")]
    [DataRow("rename-back", "preopened-leaf")]
    public async Task ActualParentFenceDistinguishesProtectedNamespaceFromPreexistingContenderHandles(string operation, string mode)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string value in new[] { typeof(ProbeMarker).Assembly.Location, "--parent-fence", "--run", operation, mode }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The owned parent-fence probe did not start.");
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(deadline.Token);
            string text = await output, error = await errors;
            Assert.AreEqual(0, process.ExitCode, error);
            TestContext.WriteLine(text.Trim());
            using var document = JsonDocument.Parse(text.Trim());
            JsonElement result = document.RootElement;
            Assert.AreEqual(operation, result.GetProperty("operation").GetString());
            Assert.AreEqual(mode, result.GetProperty("mode").GetString());
            Assert.IsTrue(result.GetProperty("completedBeforeRollback").GetBoolean());
            Assert.IsTrue(result.GetProperty("sourceRestoredAfterRollback").GetBoolean());
            Assert.IsTrue(result.GetProperty("destinationAbsentAfterRollback").GetBoolean());
            Assert.IsTrue(result.GetProperty("transientAbsentAfterRollback").GetBoolean());
            Assert.IsFalse(result.GetProperty("transactionCommitted").GetBoolean());
            Assert.IsFalse(result.GetProperty("productionQualificationClaimed").GetBoolean());
            Assert.IsFalse((bool)typeof(ProbeMarker).GetField(nameof(ProbeMarker.QualifiedForOriginalDirectoryEffects))!.GetRawConstantValue()!);
            Assert.IsNull(typeof(global::MirrorPulse.Adapter.Local.Worker.LocalWorkerPaths).Assembly.GetType(
                "MirrorPulse.Adapter.Local.Worker.LocalAtomicParentFenceProbe"));
            if (operation == "read-only")
            {
                Assert.AreEqual(JsonValueKind.Null, result.GetProperty("stagingError").ValueKind);
                Assert.AreEqual(JsonValueKind.Null, result.GetProperty("error").ValueKind);
                Assert.AreEqual(mode != "preopened-leaf", result.GetProperty("externalCreated").GetBoolean());
                Assert.AreEqual(mode != "preopened-leaf", result.GetProperty("externalRemoved").GetBoolean());
                Assert.AreEqual(mode == "preopened-leaf", result.GetProperty("externalWritten").GetBoolean());
                Assert.AreEqual(mode == "preopened-leaf", result.GetProperty("originalBytesChanged").GetBoolean());
            }
            else if (mode is "relative-nested" or "preopened-leaf")
            {
                // An existing descendant owner prevents admission. Its later write is
                // preserved; a failed staging operation must never authorize an effect.
                Assert.AreEqual(5, result.GetProperty("stagingError").GetInt32());
                Assert.AreEqual(JsonValueKind.Null, result.GetProperty("error").ValueKind);
                Assert.AreEqual(mode == "relative-nested", result.GetProperty("externalCreated").GetBoolean());
                Assert.AreEqual(mode == "relative-nested", result.GetProperty("externalRemoved").GetBoolean());
                Assert.AreEqual(mode == "preopened-leaf", result.GetProperty("externalWritten").GetBoolean());
                Assert.AreEqual(mode == "preopened-leaf", result.GetProperty("originalBytesChanged").GetBoolean());
            }
            else
            {
                Assert.AreEqual(JsonValueKind.Null, result.GetProperty("stagingError").ValueKind);
                int nativeError = result.GetProperty("error").GetInt32();
                Assert.AreEqual(6800, nativeError < 0 ? nativeError & 0xffff : nativeError);
                Assert.IsFalse(result.GetProperty("externalCreated").GetBoolean());
                Assert.IsFalse(result.GetProperty("externalRemoved").GetBoolean());
                Assert.IsFalse(result.GetProperty("externalWritten").GetBoolean());
                Assert.IsFalse(result.GetProperty("originalBytesChanged").GetBoolean());
            }
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
}
