using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

[TestClass]
public sealed class LocalDirectoryObservationTests
{
    [TestMethod]
    [DataRow("unchanged")]
    [DataRow("content")]
    [DataRow("added-child")]
    [DataRow("removed-child")]
    [DataRow("replacement")]
    [DataRow("foreign-admission")]
    [DataRow("foreign-root")]
    public async Task OriginalTreeObservationNeverAdoptsAnotherInventoryOrGeneration(string scenario)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-local-tree", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "source", "tree", "nested"));
        try
        {
            string source = Path.Combine(directory, "source"); string child = Path.Combine(source, "tree", "nested", "child.bin");
            await File.WriteAllBytesAsync(child, [1, 2, 3]);
            var paths = new LocalWorkerPaths(source); Guid admission = Guid.NewGuid();
            var first = await LocalFileOperations.ObserveDirectoryTreeAsync(paths, admission, "docs", "tree", CancellationToken.None);
            Assert.HasCount(3, first.Entries); Assert.HasCount(3, first.Entries.Select(entry => entry.NativeIdentity).Distinct().ToArray());
            if (scenario == "content") await File.WriteAllBytesAsync(child, [4, 5, 6]);
            if (scenario == "added-child") await File.WriteAllBytesAsync(Path.Combine(source, "tree", "peer.bin"), [7]);
            if (scenario == "removed-child") File.Delete(child);
            if (scenario == "replacement")
            {
                Directory.Move(Path.Combine(source, "tree"), Path.Combine(source, "old-tree"));
                Directory.CreateDirectory(Path.Combine(source, "tree", "nested"));
                await File.WriteAllBytesAsync(child, [1, 2, 3]);
            }
            var next = await LocalFileOperations.ObserveDirectoryTreeAsync(paths, scenario == "foreign-admission" ? Guid.NewGuid() : admission,
                scenario == "foreign-root" ? "archive" : "docs", "tree", CancellationToken.None);
            if (scenario == "unchanged") LocalFileOperations.RequireSameOriginalTree(first, next);
            else Assert.ThrowsExactly<InvalidDataException>(() => LocalFileOperations.RequireSameOriginalTree(first, next));
            Assert.IsFalse(AdapterHandshake.V2Capabilities.Contains("original-directory-effects-v1"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
