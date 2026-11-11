using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MirrorPulse.Adapter.Local.Worker;

/// <summary>Characterizes parent namespace fencing only inside freshly owned disposable fixtures; never a production capability.</summary>
internal static class LocalAtomicParentFenceProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-parent-fence-probe"));
        if (args.Length == 3 && args[0] == "--child")
        {
            if (args[2] is not ("direct-path" or "nested-path" or "relative-root" or "relative-nested" or "preopened-leaf")) return 2;
            string fixture = RequireOwned(args[1]), mode = args[2], source = Path.Combine(fixture, "source");
            using SafeFileHandle? directory = mode.StartsWith("relative-", StringComparison.Ordinal) ? Native.CreateFileW(
                mode == "relative-root" ? source : Path.Combine(source, "nested"), 0x80000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero) : null;
            if (directory is { IsInvalid: true }) throw new Win32Exception(Marshal.GetLastPInvokeError());
            using FileStream? existing = mode == "preopened-leaf" ? new FileStream(Path.Combine(source, "original.bin"),
                FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete) : null;
            Console.WriteLine("READY");
            if (Console.ReadLine() != "GO") return 3;
            bool created = false, removed = false, written = false; int? error = null;
            try
            {
                if (existing is not null) { existing.Write([9, 8, 7]); existing.Flush(true); written = true; }
                else if (directory is not null)
                {
                    using (var file = Native.CreateRelativeTransient(directory))
                    using (var stream = new FileStream(file, FileAccess.Write)) { stream.Write([1, 2, 3]); stream.Flush(true); created = true; }
                    removed = true; // FILE_DELETE_ON_CLOSE; independently checked after rollback below.
                }
                else
                {
                    string child = mode == "nested-path" ? Path.Combine(source, "nested", "transient") : Path.Combine(source, "transient");
                    using (var stream = new FileStream(child, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { stream.Write([1, 2, 3]); stream.Flush(true); created = true; }
                    File.Delete(child); removed = true;
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or Win32Exception)
            { error = failure is Win32Exception native ? native.NativeErrorCode : failure.HResult; }
            Console.WriteLine(JsonSerializer.Serialize(new { created, removed, written, error })); return 0;
        }
        if (args.Length != 3 || args[0] != "--run" || args[1] is not ("read-only" or "rename-back") || args[2] is not ("direct-path" or "nested-path" or "relative-root" or "relative-nested" or "preopened-leaf")) return 2;
        Directory.CreateDirectory(prefix);
        if ((File.GetAttributes(prefix) & FileAttributes.ReparsePoint) != 0) throw new IOException("The probe prefix cannot be a reparse point.");
        foreach (string operation in new[] { args[1] })
            foreach (string mode in new[] { args[2] })
            {
                string fixture = Path.Combine(prefix, Guid.NewGuid().ToString("N"));
                if (Directory.Exists(fixture)) throw new IOException("Require a fresh GUID fixture.");
                Directory.CreateDirectory(Path.Combine(fixture, "source", "nested"));
                File.WriteAllBytes(Path.Combine(fixture, "source", "original.bin"), [1, 2, 3]);
                File.WriteAllText(Path.Combine(fixture, ".owned-probe"), Path.GetFileName(fixture));
                try
                {
                    string source = Path.Combine(RequireOwned(fixture), "source"), destination = Path.Combine(fixture, "renamed");
                    if (Path.GetDirectoryName(Path.GetFullPath(source)) != fixture || Path.GetDirectoryName(Path.GetFullPath(destination)) != fixture)
                        throw new IOException("Require both fixed transaction endpoints inside the newly owned fixture.");
                    var start = new ProcessStartInfo("dotnet")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    foreach (string argument in new[] { Assembly.GetExecutingAssembly().Location, "--parent-fence", "--child", fixture, mode }) start.ArgumentList.Add(argument);
                    using var actor = Process.Start(start) ?? throw new IOException("Owned contender did not start.");
                    Task<string> errors = actor.StandardError.ReadToEndAsync();
                    try
                    {
                        if (await actor.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) != "READY")
                            throw new IOException("Contender did not acquire its original handle: " + await errors);
                        using var transaction = AtomicDirectoryNative.Begin();
                        using var parent = AtomicDirectoryNative.Open(source, transaction);
                        int? stagingError = null;
                        try { if (operation == "rename-back") { Move(source, destination, transaction); Move(destination, source, transaction); } }
                        catch (Win32Exception failure) { stagingError = failure.NativeErrorCode; }
                        await actor.StandardInput.WriteLineAsync("GO"); await actor.StandardInput.FlushAsync();
                        bool completedBeforeRollback = await Task.WhenAny(actor.WaitForExitAsync(), Task.Delay(3000)) is var first && first.IsCompleted && actor.HasExited;
                        parent.Dispose(); transaction.Dispose(); // Never commit the staged namespace changes.
                        if (!completedBeforeRollback) await actor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        if (actor.ExitCode != 0) throw new IOException("Owned contender failed: " + await errors);
                        using var result = JsonDocument.Parse((await actor.StandardOutput.ReadToEndAsync()).Trim());
                        Console.WriteLine(JsonSerializer.Serialize(new
                        {
                            operation,
                            mode,
                            stagingError,
                            completedBeforeRollback,
                            externalCreated = result.RootElement.GetProperty("created").GetBoolean(),
                            externalRemoved = result.RootElement.GetProperty("removed").GetBoolean(),
                            externalWritten = result.RootElement.GetProperty("written").GetBoolean(),
                            error = result.RootElement.GetProperty("error"),
                            originalBytesChanged = !File.ReadAllBytes(Path.Combine(source, "original.bin")).AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }),
                            transientAbsentAfterRollback = !Directory.EnumerateFiles(source, "transient", SearchOption.AllDirectories).Any(),
                            sourceRestoredAfterRollback = Directory.Exists(source),
                            destinationAbsentAfterRollback = !Directory.Exists(destination),
                            transactionCommitted = false,
                            productionQualificationClaimed = false
                        }));
                    }
                    finally
                    {
                        if (!actor.HasExited)
                        {
                            try { await actor.StandardInput.WriteLineAsync("GO"); await actor.StandardInput.FlushAsync(); await actor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                            catch { if (!actor.HasExited) actor.Kill(entireProcessTree: true); await actor.WaitForExitAsync(); }
                        }
                    }
                }
                finally { _ = RequireOwned(fixture); Directory.Delete(fixture, recursive: true); }
            }
        return 0;
        string RequireOwned(string candidate)
        {
            string path = Path.GetFullPath(candidate), id = Path.GetFileName(path);
            if (Path.GetDirectoryName(path) != prefix || !Guid.TryParseExact(id, "N", out _) ||
                (File.GetAttributes(prefix) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Require the exact newly owned GUID fixture.");
            foreach (string owned in new[] { Path.Combine(path, "source"), Path.Combine(path, "source", "nested"),
                Path.Combine(path, "source", "original.bin"), Path.Combine(path, ".owned-probe") })
                if ((File.GetAttributes(owned) & FileAttributes.ReparsePoint) != 0) throw new IOException("A probe endpoint cannot be a reparse point.");
            string marker = Path.Combine(path, ".owned-probe");
            if (new FileInfo(marker).Length != 32 || File.ReadAllText(marker) != id) throw new IOException("The exact GUID marker is required.");
            return path;
        }
    }
    static void Move(string source, string destination, SafeFileHandle transaction)
    {
        if (!Native.MoveFileTransactedW(source, destination, IntPtr.Zero, IntPtr.Zero, 0, transaction)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)] struct ObjectAttributes { public uint Length; public IntPtr RootDirectory, ObjectName; public uint Attributes; public IntPtr Descriptor, Quality; }
        [StructLayout(LayoutKind.Sequential)] struct IoStatus { public IntPtr Status; public UIntPtr Information; }
        internal static SafeFileHandle CreateRelativeTransient(SafeFileHandle directory)
        {
            if (Marshal.SizeOf<ObjectAttributes>() != 48 || Marshal.SizeOf<UnicodeString>() != 16 || Marshal.SizeOf<IoStatus>() != 16)
                throw new InvalidDataException("Require the checked 64-bit native layouts.");
            IntPtr text = Marshal.StringToHGlobalUni("transient"), name = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            try
            {
                Marshal.StructureToPtr(new UnicodeString { Length = 18, MaximumLength = 20, Buffer = text }, name, false);
                var attributes = new ObjectAttributes { Length = 48, RootDirectory = directory.DangerousGetHandle(), ObjectName = name, Attributes = 0x40 };
                // Only a fixed new owned file relative to the contender's preopened GUID fixture.
                // Synchronous write plus DELETE_ON_CLOSE never opens or replaces an existing name.
                int status = NtCreateFile(out SafeFileHandle handle, 0x40110000, ref attributes, out _, IntPtr.Zero, 0x80, 7, 2, 0x201060, IntPtr.Zero, 0);
                GC.KeepAlive(directory);
                if (status >= 0) return handle;
                handle.Dispose(); throw new Win32Exception((int)RtlNtStatusToDosError(status));
            }
            finally { Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(text); }
        }
        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MoveFileTransactedW(string existing, string destination, IntPtr progress, IntPtr data, uint flags, SafeFileHandle transaction);
        [DllImport("ntdll.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatus status, IntPtr allocationSize,
            uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
        [DllImport("ntdll.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern uint RtlNtStatusToDosError(int status);
    }

}
