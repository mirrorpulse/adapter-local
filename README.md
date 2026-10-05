# MirrorPulse Local Directory Adapter

This is the official repository for the MirrorPulse Local Directory Adapter.

The Worker consumes the fixed, hash-verified `MirrorPulse.Adapter.Sdk` 0.2.1
release package. The Host supplies a `sourceDirectory` for each authorized root;
disabled roots stay offline without probing their source paths. File IDs,
pagination cursors, range reads, and uploads are scoped by the root key. A single
SDK reader handles control messages and bounded binary chunks, including upload
cancellation and immediate transfer lease cleanup.

The Worker supports listing, stat, range reads, conditional uploads, directory
creation, same-volume moves, file deletion, and empty-directory deletion. Moves
never replace existing destinations. Nonempty directories require explicit
child operations; the Worker does not recursively delete unaccepted children.
The Worker does not receive a persistent state directory.

## Conditional mutation and recovery

Before a mutation, the Worker opens the source and its parent chain without
following reparse points and retains ordinary Windows sharing locks. Upload
replacement moves the exact accepted object into a temporary recovery name,
then publishes the complete candidate without overwriting a new destination.
If publication fails, rollback also refuses to overwrite competing content.
An interrupted replacement retains the original `.mp-recovery-<operation-id>`
file and returns `MutationOutcomeAmbiguous` with `recoveryRelativePath`; replay
is blocked until the Host or user resolves that copy. Successful operations
remove temporary copies immediately. `.mp-upload-` and `.mp-recovery-` names are
reserved and excluded from source enumeration.

These guards rely on ordinary Windows file sharing. They are not a filesystem
compare-and-swap primitive or a security sandbox against another privileged
process. Unsupported cross-volume moves and native operations are rejected.
Session replay receipts are bounded and kept in memory; after Worker restart,
the Host must reconcile an unacknowledged mutation rather than infer success.

Native contracts: [CreateFile](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew),
[file rename information](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info),
and [handle-based mutation](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle).

Run `pwsh ./eng/verify.ps1` for locked restore, Release build, formatting, and
all 12 real Worker process tests against disposable source directories. Source
and pull request CI run this profile natively on x64 and ARM64, then verify the
signed self-contained payload; each job archives its test and runtime evidence.
The Worker references the fixed published SDK package rather than a source copy.
Provider release workflow migration is in progress; existing v1 releases remain
unchanged.

`eng/verify-release.ps1` builds one self-contained x64/ARM64 package, checks the
included runtime and notices, signs it with an ephemeral test key, then runs the
native signed Worker against all 12 Local conformance cases. Its environment has
no `dotnet` on `PATH`, an unavailable `DOTNET_ROOT`, and a checked `coreclr.dll`
module path inside the extracted package. This proves private runtime use without
altering the machine's installed runtimes. Test signing does not establish the
official publisher trust required by the protected release workflow.

Licensed under Apache-2.0. See [LICENSE](LICENSE).

## Release governance

The shared controller follows `adapter-template` 4b36f41. A reviewed and merged
same-repository `develop` to `main` pull request produces a stable `X.Y.Z` release;
exactly one `breaking`, `feature`, or `fix` label selects the increment. A manual
`develop` run can publish `X.Y.Z-preview.N` only with `publish=true` and exact
`PUBLISH` confirmation. Previews do not replace GitHub's latest stable release.
Existing tags and published assets are immutable.

Build has no signing credentials. The `adapter-signing` environment must restrict
execution to `main` and `develop`; the signing step alone receives organization
certificate secrets in memory. Stable publication requires the configured human
approval, including the restriction against self approval. Environment names in
YAML alone do not establish these protections.

Both native runners verify the same frozen signed package and hash. The release
gate uses the Local filesystem profile, then production installation, catalog,
Host and CfSharp demand-provider operations at MirrorPulse 99287cf. Actual
publication candidates must pass the product's official publisher trust anchor;
dry runs use an explicitly disposable test key and do not publish a tag or release.
The package is not rebuilt after signing or approval.

`eng/adapter-sdk.lock.json` pins the original SDK 0.2.1 NuGet package from its
GitHub release. `eng/sdk-conformance.lock.json` pins the specification and native
conformance assets from the same source and verifies their lengths and hashes.
That SDK runner implements a memory-source profile; the Local profile is built
against the fixed SDK and exercises real disposable filesystem sources instead.
No latest SDK source checkout or unpublished SDK rebuild participates in this gate.

Run `pwsh ./eng/verify.ps1` and `pwsh ./eng/verify-release.ps1` for source and
signed payload checks. Existing v1 releases remain unchanged; release evidence
records the selected version, source SHA, payload hash, native runtime and trust
mode so a tested preview cannot be confused with an older stable package.
