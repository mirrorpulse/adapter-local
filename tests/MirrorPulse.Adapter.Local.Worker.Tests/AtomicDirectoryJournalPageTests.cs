extern alias Probe;
using System.Buffers.Binary;
using ProbeAtomicDirectoryJournal = Probe::MirrorPulse.Adapter.Local.Worker.AtomicDirectoryJournal;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

[TestClass]
public sealed class AtomicDirectoryJournalPageTests
{
    private static readonly Guid WatchedParent = new("63429841-1234-01a0-0000-000000000000");
    private static readonly Guid WatchedObject = new("84323981-5684-4354-1234-880064320102");
    private static readonly Guid Other = new("28398132-1000-0000-0000-000000000000");
    private static readonly IReadOnlySet<Guid> Parents = new HashSet<Guid> { WatchedParent };
    private static readonly IReadOnlySet<Guid> Objects = new HashSet<Guid> { WatchedObject };

    [TestMethod]
    public void PageAcceptsUnrelatedChangesAndPureCloseAndStopsAtTheExclusiveEnd()
    {
        var page = Page(240, Record(2, 100, Other, Other, 0x100),
            Record(3, 149, WatchedObject, WatchedParent, 0x80000000),
            Record(2, 200, Other, WatchedParent, 0x200));
        Assert.AreEqual(240L, ProbeAtomicDirectoryJournal.RequireUnchangedPage(page, 100, 200, Objects, Parents));
    }

    [TestMethod]
    [DataRow(2, false)]
    [DataRow(3, true)]
    public void PageRejectsWatchedOriginalChangesAtTheInclusiveStart(int version, bool originalObject)
    {
        var record = originalObject ? Record(version, 100, WatchedObject, Other, 0x80000001)
            : Record(version, 100, Other, WatchedParent, 0x100);
        var rejection = Assert.ThrowsExactly<InvalidDataException>(() =>
            ProbeAtomicDirectoryJournal.RequireUnchangedPage(Page(200, record), 100, 200, Objects, Parents));
        Assert.AreEqual("OriginalAtomicDirectoryHistoryChanged", rejection.Message);
    }

    [TestMethod]
    [DataRow("short-header", "AtomicDirectoryJournalRecordInvalid")]
    [DataRow("unknown-version", "AtomicDirectoryJournalRecordInvalid")]
    [DataRow("length", "AtomicDirectoryJournalRecordInvalid")]
    [DataRow("repeated-usn", "AtomicDirectoryJournalCursorInvalid")]
    [DataRow("past-page", "AtomicDirectoryJournalCursorInvalid")]
    [DataRow("stalled", "AtomicDirectoryJournalCursorInvalid")]
    public void PageRejectsIncompleteOrUnorderedJournalEvidence(string corruption, string code)
    {
        var page = corruption switch
        {
            "short-header" => new byte[7],
            "unknown-version" => Page(200, Record(4, 120, Other, Other, 0x100)),
            "length" => Page(200, Record(2, 120, Other, Other, 0x100)[..59]),
            "repeated-usn" => Page(200, Record(3, 120, Other, Other, 0x100), Record(2, 120, Other, Other, 0x100)),
            "past-page" => Page(200, Record(2, 200, Other, Other, 0x100)),
            _ => Page(100),
        };
        var refusal = Assert.ThrowsExactly<InvalidDataException>(() =>
            ProbeAtomicDirectoryJournal.RequireUnchangedPage(page, 100, 200, Objects, Parents));
        Assert.AreEqual(code, refusal.Message);
    }

    private static byte[] Record(int version, long usn, Guid file, Guid parent, uint reason)
    {
        bool wide = version != 2;
        int identityBytes = wide ? 16 : 8;
        int usnOffset = wide ? 40 : 24;
        byte[] record = new byte[wide ? 76 : 60];
        BinaryPrimitives.WriteInt32LittleEndian(record, record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), (ushort)version);
        file.ToByteArray().AsSpan(0, identityBytes).CopyTo(record.AsSpan(8));
        parent.ToByteArray().AsSpan(0, identityBytes).CopyTo(record.AsSpan(8 + identityBytes));
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(usnOffset), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(usnOffset + 16), reason);
        return record;
    }

    private static byte[] Page(long next, params byte[][] records)
    {
        byte[] page = new byte[sizeof(long) + records.Sum(record => record.Length)];
        BinaryPrimitives.WriteInt64LittleEndian(page, next);
        int cursor = sizeof(long);
        foreach (var record in records)
        {
            record.CopyTo(page, cursor);
            cursor += record.Length;
        }
        return page;
    }
}
