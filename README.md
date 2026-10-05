# MirrorPulse Local Directory Adapter

This is the official repository for the MirrorPulse Local Directory Adapter.

The repository contains the independently buildable Adapter SDK and a Local Directory protocol Worker. The Worker runs in its own process, uses the current-user Named Pipe protocol, confines all paths to the configured source directory, supports bounded range reads, conditional uploads, and transfer-cache staging, and publishes x64/ARM64 `.mpadapter` payloads.

Run `pwsh ./eng/verify.ps1` to validate the SDK and Worker. Releases are produced by the signed workflow after the organization signing secrets and release policy are configured.

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
