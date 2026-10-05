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
real Worker process tests against disposable source directories. Provider release
workflow migration is in progress; existing v1 releases remain unchanged.

`eng/verify-release.ps1` builds one self-contained x64/ARM64 package, checks the
included runtime and notices, signs it with an ephemeral test key, then runs the
native signed Worker against all 12 Local conformance cases. Its environment has
no `dotnet` on `PATH`, an unavailable `DOTNET_ROOT`, and a checked `coreclr.dll`
module path inside the extracted package. This proves private runtime use without
altering the machine's installed runtimes. Test signing does not establish the
official publisher trust required by the protected release workflow.

Licensed under Apache-2.0. See [LICENSE](LICENSE).

## Release governance

The release scripts and pinned staged workflow follow the template at commit
544c594. Version/tag inputs enter scripts through environment data and are
validated before paths or builds are created. Build has no signing secrets;
signing uses the `adapter-signing` environment; publishing alone has write
permission and uses `adapter-release`. Manual dispatch defaults to a verified
signed artifact without publishing a tag or Release.

Run `pwsh ./eng/verify-release.ps1` for hostile input rejection and a dual-RID
package signed with a disposable in-memory key. Production keys are read only
from signing-step environment variables. No private key file is read or exported.
The embedded inventory is verified before upload; MirrorPulse independently
verifies publisher trust at installation.

The repository owner must configure environment reviewers, trusted branch/tag
rules and signing-secret scope. YAML environment names alone do not enforce those
protections. Existing organization secrets remain compatible until that migration.
The current framework-dependent v1 runtime is retained by this release change.

The release workflow also verifies the newly signed candidate using MirrorPulse
16c6742 and real Local/WebDAV/SMB/FTP/SFTP Host/Worker fixtures on a disposable
runner. It records both source commits and the candidate package hash. Publishing
requires that protocol gate; signed dry-run assets remain unpublished.
