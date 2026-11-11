using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace MirrorPulse.Adapter.Local.Worker;

/// <summary>Optional experimental NTFS transaction primitives; never used by ordinary Local operations.</summary>
internal static class AtomicDirectoryNative
{
    internal sealed class Item : IDisposable
    {
        private readonly FileStream? _stream;
        private readonly FileInformation _information;
        internal Item(SafeFileHandle handle)
        {
            Handle = handle;
            try
            {
                _information = Information(handle);
                if ((_information.Attributes & 0x400) != 0) throw new InvalidDataException("ReparsePointForbidden");
                if (!IsDirectory) _stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
            }
            catch { handle.Dispose(); throw; }
        }
        internal SafeFileHandle Handle { get; }
        internal bool IsDirectory => (_information.Attributes & 0x10) != 0;
        internal async Task<string> RevisionAsync(CancellationToken token)
        {
            string time = ((ulong)_information.WriteTime.High << 32 | _information.WriteTime.Low).ToString(CultureInfo.InvariantCulture);
            if (IsDirectory) return "directory:" + time;
            _stream!.Position = 0;
            return _stream.Length.ToString(CultureInfo.InvariantCulture) + ":" + time + ":" +
                Convert.ToHexString(await SHA256.HashDataAsync(_stream, token).ConfigureAwait(false));
        }
        public void Dispose() { _stream?.Dispose(); Handle.Dispose(); }
    }
    internal static SafeFileHandle Begin()
    {
        var handle = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 30000, "MirrorPulse original directory batch");
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error);
    }
    internal static Item Open(string path, SafeFileHandle transaction)
    {
        var handle = CreateFileTransactedW(path, 0xC0010000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero, transaction, IntPtr.Zero, IntPtr.Zero);
        if (!handle.IsInvalid) return new(handle);
        int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error);
    }
    internal static long ReadFileUsn(SafeFileHandle handle)
    {
        byte[] buffer = new byte[4096];
        if (!DeviceIoControl(handle, 0x900eb, [2, 0, 3, 0], 4, buffer, buffer.Length, out int returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        ushort version = BitConverter.ToUInt16(buffer, 4);
        if (version is not (2 or 3) || returned < (version == 2 ? 60 : 76) || BitConverter.ToUInt32(buffer, 0) > returned)
            throw new NotSupportedException("OriginalFileJournalRecordUnsupported");
        return BitConverter.ToInt64(buffer, version == 2 ? 24 : 40);
    }
    internal static long ReadChangeTime(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 0, out FileBasicInformation information, 40))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return information.ChangeTime;
    }
    internal static Guid Id(SafeFileHandle transaction)
    {
        if (!GetTransactionId(transaction, out Guid id)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return id;
    }
    internal static void Remove(string path, bool directory, SafeFileHandle transaction)
    {
        bool removed = directory ? RemoveDirectoryTransactedW(path, transaction) : DeleteFileTransactedW(path, transaction);
        if (!removed) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    internal static void FenceNamespace(string source, string temporary, SafeFileHandle transaction)
    {
        if (!MoveFileTransactedW(source, temporary, IntPtr.Zero, IntPtr.Zero, 0, transaction) ||
            !MoveFileTransactedW(temporary, source, IntPtr.Zero, IntPtr.Zero, 0, transaction))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    internal static void WriteReceipt(string path, byte[] payload, SafeFileHandle transaction)
    {
        using var handle = CreateFileTransactedW(path, 0x40000000, 0, IntPtr.Zero, 1, 0x00200000, IntPtr.Zero, transaction, IntPtr.Zero, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using var stream = new FileStream(handle, FileAccess.Write); stream.Write(payload); stream.Flush(true);
    }
    internal static void Commit(SafeFileHandle transaction)
    {
        if (!CommitTransaction(transaction)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    private static FileInformation Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return information;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { internal uint Low; internal uint High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        internal long CreationTime; internal long AccessTime; internal long WriteTime; internal long ChangeTime;
        internal uint Attributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes; internal FileTime CreationTime; internal FileTime AccessTime; internal FileTime WriteTime;
        internal uint Volume; internal uint SizeHigh; internal uint SizeLow; internal uint Links; internal uint IndexHigh; internal uint IndexLow;
    }
    [DllImport("ktmw32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateTransaction(IntPtr security, IntPtr id, uint options, uint isolation, uint flags, uint timeout, string description);
    [DllImport("ktmw32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTransactionId(SafeFileHandle transaction, out Guid id);
    [DllImport("ktmw32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CommitTransaction(SafeFileHandle transaction);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileTransactedW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template, SafeFileHandle transaction, IntPtr miniVersion, IntPtr extended);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteFileTransactedW(string path, SafeFileHandle transaction);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveDirectoryTransactedW(string path, SafeFileHandle transaction);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MoveFileTransactedW(string source, string target, IntPtr progress, IntPtr data, uint flags, SafeFileHandle transaction);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength, byte[] output, int outputLength, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileBasicInformation information, int size);
}
