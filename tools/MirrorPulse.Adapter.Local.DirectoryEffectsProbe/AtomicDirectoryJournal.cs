using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MirrorPulse.Adapter.Local.Worker;

internal sealed record AtomicDirectoryJournalCheckpoint(Guid VolumeId, ulong JournalId, long NextUsn);

/// <summary>Reads a bounded interval of an existing NTFS journal and compares only original file references.</summary>
internal sealed class AtomicDirectoryJournal : IDisposable
{
    private const uint QueryUsnJournal = 0x000900f4;
    private const uint ReadUnprivilegedUsnJournal = 0x000903ab;
    private const uint CloseReason = 0x80000000;
    private const int PageSize = 64 * 1024;
    private const int MaximumPages = 64;
    private const string VolumePrefix = @"\\?\Volume";
    private readonly SafeFileHandle _handle;
    private readonly Guid _volumeId;

    private AtomicDirectoryJournal(SafeFileHandle handle, Guid volumeId)
    {
        _handle = handle;
        _volumeId = volumeId;
    }

    internal static AtomicDirectoryJournal Open(string fixturePath)
    {
        var mount = new char[1024];
        var name = new char[128];
        if (!GetVolumePathNameW(fixturePath, mount, mount.Length) ||
            !GetVolumeNameForVolumeMountPointW(ReadVolumePath(mount), name, name.Length))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        string volume = ReadVolumePath(name);
        if (!volume.StartsWith(VolumePrefix, StringComparison.OrdinalIgnoreCase) || !volume.EndsWith('\\') ||
            !Guid.TryParseExact(volume.AsSpan(VolumePrefix.Length, volume.Length - VolumePrefix.Length - 1), "B", out var id) ||
            id == Guid.Empty)
            throw new InvalidDataException("AtomicDirectoryJournalVolumeInvalid");

        // The unprivileged control expects an ordinary directory handle to the volume GUID root.
        var handle = CreateFileW(volume, 0x80000000, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (!handle.IsInvalid) return new(handle, id);
        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    private static string ReadVolumePath(char[] buffer)
    {
        int length = Array.IndexOf(buffer, '\0');
        if (length <= 0) throw new InvalidDataException("AtomicDirectoryJournalVolumeInvalid");
        return new string(buffer, 0, length);
    }

    internal AtomicDirectoryJournalCheckpoint Capture()
    {
        var bounds = Query();
        return new(_volumeId, bounds.Id, bounds.Next);
    }

    internal long RequireUnchanged(AtomicDirectoryJournalCheckpoint since, IReadOnlySet<Guid> objects,
        IReadOnlySet<Guid> parents, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bounds = Query();
        RequireOriginalRange(since, bounds);
        long cursor = since.NextUsn;
        byte[] request = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), uint.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(32), since.JournalId);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(40), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(42), 3);
        byte[] page = new byte[PageSize];
        for (int count = 0; cursor < bounds.Next; count++)
        {
            token.ThrowIfCancellationRequested();
            if (count == MaximumPages) throw new InvalidDataException("AtomicDirectoryJournalRangeExceeded");
            BinaryPrimitives.WriteInt64LittleEndian(request, cursor);
            if (!DeviceIoControl(_handle, ReadUnprivilegedUsnJournal, request, request.Length, page, page.Length,
                out int returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (returned < 0 || returned > page.Length) throw new InvalidDataException("AtomicDirectoryJournalRecordInvalid");
            cursor = RequireUnchangedPage(page.AsSpan(0, returned), cursor, bounds.Next, objects, parents);
        }
        var after = Query();
        RequireOriginalRange(since, after);
        if (after.Next < bounds.Next) throw new InvalidDataException("AtomicDirectoryJournalRangeLost");
        return bounds.Next;
    }

    internal static long RequireUnchangedPage(ReadOnlySpan<byte> page, long cursor, long end,
        IReadOnlySet<Guid> objects, IReadOnlySet<Guid> parents)
    {
        if (page.Length < sizeof(long)) throw new InvalidDataException("AtomicDirectoryJournalRecordInvalid");
        if (cursor < 0 || end <= cursor) throw new InvalidDataException("AtomicDirectoryJournalCursorInvalid");
        long next = BinaryPrimitives.ReadInt64LittleEndian(page);
        if (next <= cursor) throw new InvalidDataException("AtomicDirectoryJournalCursorInvalid");
        long previous = cursor - 1;
        var remaining = page[sizeof(long)..];
        while (!remaining.IsEmpty)
        {
            if (remaining.Length < 8) throw new InvalidDataException("AtomicDirectoryJournalRecordInvalid");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(remaining);
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(remaining[4..]);
            if (version is not (2 or 3) || length < (version == 2 ? 60 : 76) || length > remaining.Length)
                throw new InvalidDataException("AtomicDirectoryJournalRecordInvalid");

            var record = remaining[..(int)length];
            int usnOffset = version == 2 ? 24 : 40;
            long usn = BinaryPrimitives.ReadInt64LittleEndian(record[usnOffset..]);
            if (usn <= previous || usn >= next) throw new InvalidDataException("AtomicDirectoryJournalCursorInvalid");
            previous = usn;
            uint reason = BinaryPrimitives.ReadUInt32LittleEndian(record[(usnOffset + 16)..]);
            // File names and other fields are never decoded, copied to application records, or logged.
            if (usn < end && (reason & ~CloseReason) != 0 &&
                (objects.Contains(ReadFileReference(record[8..], version)) ||
                 parents.Contains(ReadFileReference(record[(version == 2 ? 16 : 24)..], version))))
                throw new InvalidDataException("OriginalAtomicDirectoryHistoryChanged");
            remaining = remaining[(int)length..];
        }
        return next;
    }

    private static Guid ReadFileReference(ReadOnlySpan<byte> record, ushort version)
    {
        if (version == 3) return new Guid(record[..16]);
        Span<byte> reference = stackalloc byte[16];
        reference.Clear();
        record[..8].CopyTo(reference);
        return new Guid(reference);
    }

    private (ulong Id, long First, long Lowest, long Next) Query()
    {
        byte[] journal = new byte[128];
        if (!DeviceIoControl(_handle, QueryUsnJournal, [], 0, journal, journal.Length, out int returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (returned is < 56 or > 128) throw new InvalidDataException("AtomicDirectoryJournalBoundsInvalid");
        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(journal);
        long first = BinaryPrimitives.ReadInt64LittleEndian(journal.AsSpan(8));
        long next = BinaryPrimitives.ReadInt64LittleEndian(journal.AsSpan(16));
        long lowest = BinaryPrimitives.ReadInt64LittleEndian(journal.AsSpan(24));
        if (id == 0 || first < 0 || lowest < 0 || next <= 0 || first > next || lowest > next)
            throw new InvalidDataException("AtomicDirectoryJournalBoundsInvalid");
        return (id, first, lowest, next);
    }

    private void RequireOriginalRange(AtomicDirectoryJournalCheckpoint since, (ulong Id, long First, long Lowest, long Next) bounds)
    {
        if (since.VolumeId != _volumeId || since.JournalId != bounds.Id || since.NextUsn < bounds.First ||
            since.NextUsn < bounds.Lowest || since.NextUsn > bounds.Next)
            throw new InvalidDataException("AtomicDirectoryJournalRangeLost");
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string path, [Out] char[] mount, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mount, [Out] char[] volume, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputLength,
        byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
