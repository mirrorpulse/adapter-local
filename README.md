# MirrorPulse Local Directory Adapter

This is the official repository for the MirrorPulse Local Directory Adapter.

The Worker consumes the fixed, hash-verified `MirrorPulse.Adapter.Sdk` 0.2.1
release package. The Host supplies a `sourceDirectory` for each authorized root;
disabled roots stay offline without probing their source paths. File IDs,
pagination cursors, range reads, and uploads are scoped by the root key. A single
SDK reader handles control messages and bounded binary chunks, including upload
cancellation and immediate transfer lease cleanup.

This development checkpoint supports listing, stat, range reads, and creation of
new files. Existing destinations are preserved. Conditional replacement and
directory/move/delete operations are being implemented before the v2 release.
The Worker does not receive a persistent state directory.

Run `pwsh ./eng/verify.ps1` for locked restore, Release build, formatting, and
real Worker process tests against disposable source directories. Provider release
workflow migration is in progress; existing v1 releases remain unchanged.

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
