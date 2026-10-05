namespace MirrorPulse.Adapter.Local.Worker;

/// <summary>Confines Worker paths to the source directory selected by the user.</summary>
public sealed class LocalWorkerPaths
{
    private readonly string _root;
    private readonly string _prefix;

    public LocalWorkerPaths(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        _root = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException(_root);
        }

        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A reparse-point source directory is not allowed.");
        }

        _prefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
    }

    public string Root => _root;

    public string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (relativePath.Length > 4096 || Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\0') || relativePath.StartsWith('\\') ||
            relativePath.StartsWith('/'))
        {
            throw new InvalidDataException("The local Worker path must be bounded and relative.");
        }

        string[] segments = relativePath.Replace('/', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.EndsWith('.') || segment.EndsWith(' ') || IsDeviceName(segment) || IsTransferName(segment)))
        {
            throw new InvalidDataException("The local Worker path contains an unsafe segment.");
        }

        string path = Path.GetFullPath(Path.Combine(_root, Path.Combine(segments)));
        if (!path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The local Worker path escaped the source directory.");
        }

        string current = _root;
        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The local Worker cannot traverse a reparse point.");
            }
        }

        return path;
    }

    public string ResolveDirectory(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return _root;
        }

        return Resolve(relativePath);
    }

    private static bool IsDeviceName(string segment)
    {
        string name = segment.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
                name[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }

    internal static bool IsTransferName(string segment) => segment.StartsWith(".mp-upload-", StringComparison.OrdinalIgnoreCase) ||
        segment.StartsWith(".mp-recovery-", StringComparison.OrdinalIgnoreCase);
}
