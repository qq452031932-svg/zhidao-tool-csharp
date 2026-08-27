# Managed Browser Persistence Review Fixes

**Goal:** Close the remaining managed-browser persistence review findings without weakening immutable execution snapshots or the approved cleanup/account-removal safety boundaries.

**Architecture:** Keep cleanup fencing scoped to the exact installation/session pair. Treat session lifecycle and the durable cleanup claim as database-enforced write fences. During v15 upgrade, classify every orphan attempt before deleting anything: only definitely-never-submitted, zero-evidence orphans are removable; all ambiguous or side-effect-bearing orphans abort the transaction. Account removal uses one fail-closed predicate for preview, confirmation manifest, and final immediate transaction, then deletes the full terminal closure and recomputes surviving counters.

## Task 1: Shared installation cleanup fencing

- [x] Add RED tests proving a retired session on an active shared installation can be claimed while another session remains usable.
- [x] Add RED tests proving lifecycle states `QuarantinePending`, `Quarantined`, `CleanupPending`, and `Deleted` permanently reject new managed transfer/share/standalone/attempt references after claim release.
- [x] Remove installation-lifecycle coupling from the exact-pair claim and add session-lifecycle guards to all managed owner/attempt insert paths.
- [x] Run `ManagedBrowserStateStoreTests` GREEN.

## Task 2: v15 orphan-attempt classification

- [x] Add RED upgrade tests for all four owner kinds, exact account/generation ownership, all six evidence tables, safe orphan cleanup, and high-risk transactional rollback.
- [x] Classify orphan attempts before replacement-table mutation. Allow deletion only for `DefinitelyNotSubmitted` plus zero scalar/child evidence; abort every ambiguous/high-risk case.
- [x] Delete only the fully classified safe closure and add RemoteSelection owner deletion fencing.
- [x] Run focused `MigrationTests` GREEN.

## Task 3: Share/standalone account-removal closure

- [x] Add RED tests for in-flight share, in-flight standalone, preview-to-confirmation races, unknown results, safe terminal closure, and exact counter repair.
- [x] Extend active-work checks and manifest materialization with one shared fail-closed terminal predicate for transfer-share/standalone owners and their attempts/evidence.
- [x] In the final immediate transaction, recompute the same predicate and token, delete only the safe attempt/evidence/owner closure, then recompute surviving batch/output counters without underflow.
- [x] Run focused `AccountRemovalStoreTests` GREEN.

## Task 4: Verification and handoff

- [x] Run the three changed test classes, Core tests, solution build, and `git diff --check`; skip the known long performance threshold.
- [x] Self-review migration atomicity, trigger coverage, fail-closed predicates, redaction, cancellation, and unrelated user changes.
- [ ] Commit the reviewed fix and report exact RED/GREEN evidence and remaining concerns.
