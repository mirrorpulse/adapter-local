using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Local.Worker;

/// <summary>Maps each configured root to its authorized source without probing offline roots.</summary>
public sealed class LocalWorkerRoots
{
    private readonly Dictionary<string, LocalWorkerPaths?> _roots = new(StringComparer.Ordinal);

    public LocalWorkerRoots(IEnumerable<AdapterRootBinding> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        foreach (var root in roots)
        {
            if (_roots.Count >= 64 || string.IsNullOrWhiteSpace(root.RootKey) || root.RootKey.Length > 256 ||
                root.RootKey.Any(char.IsControl) || root.Configuration is null || _roots.ContainsKey(root.RootKey))
            {
                throw new InvalidDataException("InvalidRoots");
            }

            LocalWorkerPaths? paths = null;
            if (root.Enabled)
            {
                if (!root.Configuration.TryGetValue("sourceDirectory", out string? source) ||
                    string.IsNullOrWhiteSpace(source))
                {
                    throw new InvalidDataException("SourceDirectoryRequired");
                }

                paths = new LocalWorkerPaths(source);
            }

            _roots.Add(root.RootKey, paths);
        }

        if (_roots.Count == 0)
        {
            throw new InvalidDataException("InvalidRoots");
        }
    }

    public LocalWorkerPaths GetPaths(string rootKey)
    {
        ArgumentNullException.ThrowIfNull(rootKey);
        if (!_roots.TryGetValue(rootKey, out LocalWorkerPaths? paths))
        {
            throw new InvalidDataException("UnknownRoot");
        }

        return paths ?? throw new InvalidDataException("RootOffline");
    }
}
