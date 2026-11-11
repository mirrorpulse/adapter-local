# Conditional revisions identify the original object

The earlier Local revision consisted of directory last-write time, or file
length, last-write time and SHA-256. A different object could occupy the same
path with all those values restored. An actual Worker `Delete` using the old
revision then returned `MutationComplete` and removed that later object.

The regression uses the real current-user Named Pipe and Worker. It captures an
original revision, moves that original to a retained fixture name, creates a
different object at the original path, restores the original timestamp and, for
files, writes identical complete bytes. Keeping the original alive also ensures
the successor has a distinct object identity. Both file and empty-directory
cases reproduced the old acceptance.

Revisions now include the full 64-bit volume serial number and 128-bit file ID
obtained with `GetFileInformationByHandleEx(FileIdInfo)` from the same protected
native handle as the existing metadata/content observation. The
[Windows identity contract](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info)
defines this pair for comparing object identity. They
are tagged `native-v2`. All conditional reads, uploads, moves and deletes use the
same producer; the isolated transaction tool shares the producer as well. An
unavailable zero file ID produces an explicit error rather than a fabricated
generation. A later object with identical bytes and time has a different token,
so the actual old conditional deletion returns `RemoteConflict` and preserves
both objects.

The wire revision remains opaque; protocol version and field shapes do not
change. A retained legacy token is deliberately not treated as a current
generation. Upgrades must keep the existing conflict/reconciliation path for
such pending work; this change does not silently rebase it to a newly observed
object. The token does not claim complete write history or a monotonically
increasing event counter.

The full gate retains the 68 existing test identities and adds both actual
Worker generation cases, requiring 70 passes without skips. The packaged
private-runtime conformance runner retains its original twelve cases and adds
the same two cases, requiring fourteen on each RID. The experimental directory
effect capability remains disabled; its live SDK/Host contract is separate.
