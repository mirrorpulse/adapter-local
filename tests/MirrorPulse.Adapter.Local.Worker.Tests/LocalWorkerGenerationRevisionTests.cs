using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

[TestClass]
public sealed class LocalWorkerGenerationRevisionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OriginalConditionalDeleteCannotDeleteALaterObjectWithIdenticalBytesAndTimestamp(bool directory)
    {
        await using var session = await LocalWorkerSession.StartAsync();
        string path = Path.Combine(session.Root, "left", "subject");
        string retained = Path.Combine(session.Root, "left", "retained-subject");
        byte[] bytes = "same original and successor content"u8.ToArray();
        DateTime timestamp = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        if (directory) { Directory.CreateDirectory(path); Directory.SetLastWriteTimeUtc(path, timestamp); }
        else { await File.WriteAllBytesAsync(path, bytes); File.SetLastWriteTimeUtc(path, timestamp); }
        AdapterControlFrame original = await session.RequestAsync("Stat", new { rootKey = "left", path = "subject" });
        string expected = original.Payload.GetProperty("revision").GetString()!;
        if (directory)
        {
            Directory.Move(path, retained);
            Directory.CreateDirectory(path); Directory.SetLastWriteTimeUtc(path, timestamp);
        }
        else
        {
            File.Move(path, retained);
            await File.WriteAllBytesAsync(path, bytes); File.SetLastWriteTimeUtc(path, timestamp);
        }
        AdapterControlFrame result = await session.RequestAsync("Delete", new AdapterOperationRequest(Guid.NewGuid(), "left", "subject",
            Preconditions: new(expected), IsDirectory: directory));
        string? outcome = result.Payload.TryGetProperty("code", out var code) ? code.GetString() : result.MessageType;
        Assert.AreEqual("RemoteConflict", outcome, "The original generation must refuse the actual later object: " + result.Payload.GetRawText());
        Assert.IsTrue(directory ? Directory.Exists(path) && Directory.Exists(retained) : File.Exists(path) && File.Exists(retained));
        if (!directory)
        {
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(retained));
        }
        AdapterControlFrame current = await session.RequestAsync("Stat", new { rootKey = "left", path = "subject" });
        Assert.AreNotEqual(expected, current.Payload.GetProperty("revision").GetString());
    }
}
