# Original directory admission and remaining atomic effect boundary

This branch adds bounded read-only observations of an original Local directory
tree. It does not enable recursive deletion, advertise
`original-directory-effects-v1`, change the pinned SDK, or weaken existing
conditional mutations. The companion SDK change defines public original witness,
owned-child transition and final dependency-chain types. A released SDK and a
qualified atomic backend are still required before enabling that contract.

`ObserveDirectoryTreeAsync` takes an explicit root key, admission ID and non-root
relative path. It retains no-follow handles for the original ancestor chain,
parent and every observed member during capture. The immutable sorted inventory
contains path/kind, a fingerprint of observed volume/file/creation identity and
the original directory revision or complete file-content revision. Capture is
bounded to2048 members,64 levels,32MiB per file and256MiB total file content.
It refuses file hard links and reparse points and rechecks every retained native
object plus the complete inventory before returning. Comparison rejects changes
to the admission, root, parent, native object, bytes, original revision or child
inventory. No timestamp refresh can turn a changed observation into the old one.

These native facts are admission observations. The file/creation identity
fingerprint is not, by itself, proof that an arbitrary historical path has not
been replaced or that a generation cannot be reused. A second equal observation
does not exclude a mutation after it returns. In particular, holding a directory
handle with ordinary sharing rules does not establish exclusive child-name
ownership. A current empty listing cannot prove that original children were
removed through their owned operations.

## Remaining implementation before capability negotiation

1. Bind an admitted original generation to an actual backend atomic/exclusion
   mechanism, and qualify original parent plus direct-child mutation together.
   Return no accepted transition from a child mutation followed by a fresh stat.
2. Persist exact original operation/request and parent-transition receipts through
   the same documented durable effect boundary. The Host must explicitly grant a
   persistent location; the existing transfer cache is not a persistent state
   grant. Recover lost replies and new Worker processes without adopting a later
   generation. Unknown effects remain ambiguous.
3. Validate the complete sealed direct-child dependency set and terminal owned
   transition under the final nonrecursive parent mutation. A new child, failed
   child, unrelated removal or replacement must refuse execution and ACK.
4. Consume the reviewed released SDK, add exact request/reply negotiation and
   preserve old-v2/file behavior. Unimplemented and unsupported backend versions
   remain explicit; no new capability is implied by these observation helpers.
5. Add independent process contenders and crash/reply/restart cuts on x64/ARM64,
   followed by a real organization-signed package test through the Host. The
   current observation tests do not establish signed recursive acceptance.

Windows directory oplocks alone are insufficient: their enumeration-change break
is advisory and does not wait for acknowledgement. See Microsoft's
[FSCTL_REQUEST_OPLOCK contract](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_request_oplock).
Transactional NTFS provides a possible local-only experimental atomic boundary,
but [its support limits](https://learn.microsoft.com/en-us/windows/win32/fileio/when-to-use-transactional-ntfs)
exclude non-NTFS and network volumes, and Microsoft
[does not recommend new dependencies on it](https://learn.microsoft.com/en-us/windows/win32/fileio/about-transactional-ntfs).
This branch therefore does not substitute either mechanism for unproven backend
guarantees. Any chosen implementation needs explicit qualification and review.

The seven added tests retain every original Worker process case. They check
unchanged observations and refusal after content change, child addition/removal,
same-path tree replacement, foreign admission and foreign root. Neither those
tests nor the SDK shape validators close the full recursive-directory feature.
