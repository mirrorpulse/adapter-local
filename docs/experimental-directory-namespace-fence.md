# Experimental whole-batch namespace fence

This remains an isolated qualification tool. It does not add a Worker request,
advertise a production capability, publish a package, or qualify the complete
original-parent effect contract.

Version 1 of the disposable directory-retirement experiment is retained unchanged
as a control. It demonstrates that read-only parent observations leave a gap
between validation and commit: an independent transient child can escape the
limited directory USN/change-time checks.

Version 2 is a distinct, fingerprinted admission and receipt format. Before
reading the current original tree, it stages a transacted rename away and back.
The transaction retains that namespace until all original removals and their
receipt commit together. The exact original IDs, revisions, file USNs, complete
inventory, source-root identity and private-state identity must still validate.
It does not obtain a new baseline after deleting a child.

Ordinary Local operations and version 1 retain their existing leases. Version 2
uses separate ancestor handles that allow its own metadata writes while refusing
ancestor deletion/replacement; every ancestor is still checked for directory
kind and reparse points. The old leases remain held until those bindings have
been acquired. The first version-2 test exposed a sharing conflict caused by the
old read-sharing leases; the new lease is restricted to this isolated variant.

Nine additional actual cases cover:

- All four original effects and their durable receipt commit, followed by replay
  that preserves a later source tree and the original receipt bytes.
- Independent process child creation is refused at three points before commit.
- An injected pre-commit interruption restores every original byte, retains no
  accepted receipt, and permits the same immutable admission to retry.
- Real process death at three pre-commit points rolls back the source; a fresh
  process can complete the original admission.
- Real process death after commit loses the reply; a fresh process returns the
  original durable receipt without deleting a later tree at the same path.

All 59 earlier cases, including the version-1 counterexample and the ten parent
fence contenders, remain present. That qualification required 68 actual passes
and no skips. Dual-RID dry-run packaging and the existing 12 private-runtime
conformance cases continue to exercise the shipping Worker.

This proves the tested validation-to-commit boundary for an entire disposable
batch. It does not establish complete pre-fence history, every preexisting
handle/metadata contender, a live SDK wire contract, per-operation source
witnesses, or Host/journal acknowledgement safety. Those distinctions remain
required before enabling a live capability. Merging and releasing this draft
remain subject to human review.

## Version 3 volume history

The version 3 admission retains the existing NTFS journal identity, its next
USN before observing the tree, and the native reference of the original direct
parent. Retirement fences the original direct parent and source tree in the
same transaction. Under those fences it compares the original identities and
inventory, then reads the journal from the retained start through a bounded
end. The history check considers changes to the watched objects and children
of the watched directories. Journal loss, malformed records, an incomplete
range or a watched mutation stops retirement before any removal. The journal
reader compares file references and leaves record names undecoded.

A separate process can create and delete a sibling before retirement in the
version 2 control. Version 3 detects that history and preserves the original
tree. Under the version 3 fences, an independent sibling creation attempt is
refused after validation and after receipt staging. The tests also exercise a
direct-parent metadata handle, process death before commit and recovery of the
committed receipt while preserving a successor tree. The receipt records the
sampled journal end along with the original admission fingerprint.
The Local adapter gate requires all 92 cases, including malformed journal page
and unavailable original range cases.

The experiment still runs in disposable fixtures. A live Local Worker needs
wire admission and separately durable typed child-effect receipts, plus
end-to-end signed qualification with the Host.
