using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Adapter.Local.Worker;
using MirrorPulse.Adapter.Sdk;

if (args.Length > 0 && args[0] == "--parent-fence") return await LocalAtomicParentFenceProbe.RunAsync(args[1..]);
if (args.Length < 2 || args[0] is not ("--retire" or "--retire-authorized" or "--create-extra" or "--delete-original" or
    "--overwrite-original" or "--create-transient-peer")) return 2;
string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
string name = Path.GetFileName(root); const string prefix = "mp-local-atomic-";
if (!string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
    !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
    !Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return 2;
string marker = Path.Combine(root, ".mp-atomic-test-fixture");
if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
    new FileInfo(marker).Length > 256 || await File.ReadAllTextAsync(marker) != name) return 2;
string source = Path.Combine(root, "source"), state = Path.Combine(root, "state");
foreach (string directory in new[] { source, state })
    if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return 2;
try
{
    if (args[0] == "--create-extra") { await File.WriteAllTextAsync(Path.Combine(source, "tree", "unowned.bin"), "independent unowned content"); return 0; }
    if (args[0] == "--create-transient-peer")
    {
        string peer = Path.Combine(source, "unowned-transient-peer.bin");
        await using (var created = new FileStream(peer, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await created.WriteAsync(new byte[] { 1, 2, 3 });
        File.Delete(peer);
        return 0;
    }
    if (args[0] == "--delete-original") { File.Delete(Path.Combine(source, "tree", "nested", "leaf.bin")); return 0; }
    if (args[0] == "--overwrite-original") { await File.WriteAllTextAsync(Path.Combine(source, "tree", "top.bin"), "independent changed content"); return 0; }
    if (args.Length is < 3 or > 4 || !Guid.TryParseExact(args[2], "N", out Guid admissionId)) return 2;
    string admissionPath = Path.Combine(state, admissionId.ToString("N") + ".admission.json");
    if (!File.Exists(admissionPath) || (File.GetAttributes(admissionPath) & FileAttributes.ReparsePoint) != 0 ||
        new FileInfo(admissionPath).Length is 0 or > 16 * 1024 * 1024) return 2;
    var admission = AdapterProtocolJson.Decode<LocalAtomicDirectoryAdmission>(await File.ReadAllBytesAsync(admissionPath));
    LocalAtomicDirectoryAuthorization? authorization = null;
    if (args[0] == "--retire-authorized")
    {
        string authorizationPath = Path.Combine(state, admissionId.ToString("N") + ".authorization.json");
        string requestPath = Path.Combine(state, admissionId.ToString("N") + ".authorization-request.json");
        if (File.Exists(requestPath)) authorizationPath = requestPath;
        if (!File.Exists(authorizationPath) || (File.GetAttributes(authorizationPath) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(authorizationPath).Length is 0 or > 65536) return 2;
        authorization = AdapterProtocolJson.Decode<LocalAtomicDirectoryAuthorization>(await File.ReadAllBytesAsync(authorizationPath));
    }
    LocalAtomicDirectoryCheckpoint? cut = args.Length == 4 ? Enum.Parse<LocalAtomicDirectoryCheckpoint>(args[3], ignoreCase: false) : null;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var receipt = await LocalFileOperations.RetireAtomicDirectoryAsync(new(source), admission, state, true, (point, _) =>
    {
        if (point == cut) Environment.Exit(73); // Deliberately bypass all managed finally blocks and lose the return.
        return ValueTask.CompletedTask;
    }, deadline.Token, authorization);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        admissionId = receipt.AdmissionId,
        effects = receipt.Effects.Count,
        qualifiedForOriginalDirectoryEffects = MirrorPulse.Adapter.Local.DirectoryEffectsProbe.ProbeMarker.QualifiedForOriginalDirectoryEffects,
        receiptSha256 = Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(receipt)))
    }));
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or JsonException or ArgumentException)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        errorType = error.GetType().Name,
        nativeError = error is Win32Exception native ? native.NativeErrorCode : (int?)null,
        hresult = error.HResult
    }));
    return 91;
}

namespace MirrorPulse.Adapter.Local.DirectoryEffectsProbe
{
    /// <summary>The disposable transaction experiment does not qualify the original-parent history contract.</summary>
    public static class ProbeMarker { public const bool QualifiedForOriginalDirectoryEffects = false; }
}
