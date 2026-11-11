using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Local.Worker;

/// <summary>Original native observations for admission; never evidence that a child effect or parent retirement succeeded.</summary>
internal sealed record LocalDirectoryTreeEntry(string Path, bool IsDirectory, string NativeIdentity, string Revision);

internal sealed record LocalDirectoryTreeObservation(int Version, Guid AdmissionId, string RootKey,
    string Path, string ParentNativeIdentity, IReadOnlyList<LocalDirectoryTreeEntry> Entries, string Fingerprint);

internal static partial class LocalFileOperations
{
    /// <summary>Retains original tree bytes and native identity while preparing a bounded immutable observation.</summary>
    /// <remarks>Directory sharing does not exclude new children. The returned observation grants no mutation or revision-rebase authority.</remarks>
    internal static async Task<LocalDirectoryTreeObservation> ObserveDirectoryTreeAsync(LocalWorkerPaths paths,
        Guid admissionId, string rootKey, string relative, CancellationToken token)
    {
        if (admissionId == Guid.Empty) throw new InvalidDataException("DirectoryAdmissionRequired");
        new AdapterFileAddress(rootKey, relative).Validate();
        if (relative.Length == 0) throw new InvalidDataException("RootMutationForbidden");
        string source = paths.ResolveDirectory(relative);
        using var ancestors = new DirectoryLease(Path.GetDirectoryName(source)!);
        using var parent = ProtectedObject.TryOpen(Path.GetDirectoryName(source)!, mutate: false)
            ?? throw new DirectoryNotFoundException();
        var retained = new List<(string Path, ProtectedObject Item, Native.FileInformation Initial)>();
        try
        {
            long contentLength = 0;
            Capture(source, relative, 0);
            if (!retained[0].Item.IsDirectory) throw new InvalidDataException("OriginalDirectoryRequired");
            var entries = new List<LocalDirectoryTreeEntry>(retained.Count);
            foreach (var entry in retained)
            {
                token.ThrowIfCancellationRequested();
                if (!entry.Item.IsDirectory)
                {
                    contentLength = checked(contentLength + entry.Item.Stream!.Length);
                    if (entry.Item.Stream.Length > 32 * 1024 * 1024 || contentLength > 256 * 1024 * 1024)
                        throw new InvalidDataException("DirectoryObservationContentBoundExceeded");
                }
                string revision = await entry.Item.RevisionAsync(token).ConfigureAwait(false);
                RequireSame(entry.Initial, Native.Information(entry.Item.Handle));
                entries.Add(new(entry.Path, entry.Item.IsDirectory, Identity(entry.Initial), revision));
            }
            // Check every retained original after the complete walk. A disappeared,
            // replaced, metadata-changed or newly added member invalidates admission.
            var actual = new HashSet<string>(StringComparer.Ordinal);
            Visit(source, relative, actual, 0);
            if (!actual.SetEquals(entries.Select(entry => entry.Path))) throw new InvalidDataException("OriginalDirectoryInventoryChanged");
            foreach (var entry in retained) RequireSame(entry.Initial, Native.Information(entry.Item.Handle));
            string parentIdentity = Identity(Native.Information(parent.Handle));
            var ordered = entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
            string fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                admissionId,
                rootKey,
                path = relative,
                parentNativeIdentity = parentIdentity,
                entries = ordered
            })));
            return new(1, admissionId, rootKey, relative, parentIdentity, new ReadOnlyCollection<LocalDirectoryTreeEntry>(ordered), fingerprint);

            void Capture(string path, string name, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 64 || retained.Count >= 2048) throw new InvalidDataException("DirectoryObservationInventoryBoundExceeded");
                var item = ProtectedObject.TryOpen(path, mutate: false) ?? throw new FileNotFoundException("An original directory member disappeared.");
                var info = Native.Information(item.Handle); retained.Add((name, item, info));
                if (info.Links != 1 && !item.IsDirectory) throw new InvalidDataException("DirectoryObservationHardLinkUnsupported");
                if (!item.IsDirectory) return;
                foreach (string child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
                    Capture(child, name + "/" + System.IO.Path.GetFileName(child), depth + 1);
            }

            void Visit(string path, string name, HashSet<string> inventory, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 64 || inventory.Count >= 2048 || !inventory.Add(name)) throw new InvalidDataException("DirectoryObservationInventoryBoundExceeded");
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("ReparsePointForbidden");
                if (!Directory.Exists(path)) return;
                foreach (string child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
                    Visit(child, name + "/" + System.IO.Path.GetFileName(child), inventory, depth + 1);
            }
        }
        finally { foreach (var entry in retained.AsEnumerable().Reverse()) entry.Item.Dispose(); }
    }

    internal static void RequireSameOriginalTree(LocalDirectoryTreeObservation original, LocalDirectoryTreeObservation current)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(current);
        if (original.Version != 1 || current.Version != 1 || original.AdmissionId != current.AdmissionId ||
            original.RootKey != current.RootKey || original.Path != current.Path || original.ParentNativeIdentity != current.ParentNativeIdentity ||
            original.Fingerprint != current.Fingerprint || !original.Entries.SequenceEqual(current.Entries))
            throw new InvalidDataException("OriginalDirectoryObservationChanged");
    }

    private static string Identity(Native.FileInformation value)
    {
        string identity = string.Join(":", value.Volume.ToString("X8", CultureInfo.InvariantCulture),
            value.IndexHigh.ToString("X8", CultureInfo.InvariantCulture) + value.IndexLow.ToString("X8", CultureInfo.InvariantCulture),
            value.CreationTime.High.ToString("X8", CultureInfo.InvariantCulture) + value.CreationTime.Low.ToString("X8", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(identity)));
    }

    private static void RequireSame(Native.FileInformation before, Native.FileInformation after)
    {
        if (Identity(before) != Identity(after) || before.Attributes != after.Attributes || before.Links != after.Links ||
            before.SizeHigh != after.SizeHigh || before.SizeLow != after.SizeLow ||
            before.WriteTime.High != after.WriteTime.High || before.WriteTime.Low != after.WriteTime.Low)
            throw new InvalidDataException("OriginalDirectoryObservationChanged");
    }
}
