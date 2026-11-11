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
fence contenders, remain present. The full gate now requires 68 actual passes
and no skips. Dual-RID dry-run packaging and the existing 12 private-runtime
conformance cases continue to exercise the shipping Worker.

This proves the tested validation-to-commit boundary for an entire disposable
batch. It does not establish complete pre-fence history, every preexisting
handle/metadata contender, a live SDK wire contract, per-operation source
witnesses, or Host/journal acknowledgement safety. Those distinctions remain
required before enabling a live capability. Merging and releasing this draft
remain subject to human review.
