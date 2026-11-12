extern alias Probe;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using LocalAtomicDirectoryCheckpoint = Probe::MirrorPulse.Adapter.Local.Worker.LocalAtomicDirectoryCheckpoint;

namespace MirrorPulse.Adapter.Local.Worker.Tests;

public sealed partial class LocalAtomicDirectoryRetirementTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NamespaceFenceProtectsOriginalDirectoriesFromAPreexistingMetadataWriter(bool nested)
    {
        using var fixture = new Fixture();
        string directory = nested ? Path.Combine(fixture.Tree, "nested") : fixture.Tree;
        using var writer = MetadataWriter.Open(directory);
        if (writer.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());

        long controlTime = Directory.GetLastWriteTimeUtc(directory).AddMinutes(1).ToFileTimeUtc();
        if (!MetadataWriter.SetFileTime(writer, IntPtr.Zero, IntPtr.Zero, in controlTime))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        Assert.AreEqual(controlTime, Directory.GetLastWriteTimeUtc(directory).ToFileTimeUtc());
        var admission = await fixture.PrepareAsync(fenceParentNamespace: true);

        long contenderTime = controlTime + TimeSpan.FromMinutes(1).Ticks;
        bool attempted = false;
        bool wrote = false;
        int contenderError = 0;
        try
        {
            var receipt = await fixture.RetireAsync(admission, (point, _) =>
            {
                if (point == LocalAtomicDirectoryCheckpoint.AfterOriginalsLocked)
                {
                    attempted = true;
                    wrote = MetadataWriter.SetFileTime(writer, IntPtr.Zero, IntPtr.Zero, in contenderTime);
                    contenderError = wrote ? 0 : Marshal.GetLastPInvokeError();
                }
                return ValueTask.CompletedTask;
            });
            Console.WriteLine($"Retirement committed. Metadata attempt={attempted}, accepted={wrote}, native error={contenderError}.");
            Assert.IsTrue(attempted);
            Assert.IsFalse(wrote, "A writer opened before admission changed original metadata before the transaction committed.");
            Assert.IsTrue(contenderError is 5 or 32 or 6800);
            Assert.HasCount(4, receipt.Effects);
            Assert.IsTrue(File.Exists(fixture.Receipt(admission)));
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 5 or 32 or 6800)
        {
            Console.WriteLine($"Retirement refused with native error={error.NativeErrorCode}. Metadata attempt={attempted}, accepted={wrote}, native error={contenderError}.");
            await fixture.AssertOriginalsAsync();
            Assert.IsFalse(File.Exists(fixture.Receipt(admission)));
            Assert.AreEqual(wrote ? contenderTime : controlTime, Directory.GetLastWriteTimeUtc(directory).ToFileTimeUtc());
        }
    }

    private static class MetadataWriter
    {
        private const uint WriteAttributes = 0x100;
        private const uint BackupSemantics = 0x02000000;

        internal static SafeFileHandle Open(string directory) =>
            CreateFileW(directory, WriteAttributes, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, BackupSemantics, IntPtr.Zero);

        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share,
            IntPtr security, FileMode disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileTime(SafeFileHandle file, IntPtr creationTime, IntPtr accessTime, in long writeTime);
    }
}
