# License capacity acceptance

This is a redacted evidence template. Execute only against a disposable instance on
`127.0.0.1:18093`; production routing must remain absent.

## Fixed workload

- Before each measured phase, run a fixed 60-second steady-state warmup at that phase's rate through the same signed public and SQLite paths, using a separate deterministic nonce domain. Record only its aggregate report.
- Start CPU/RSS sampling only after the corresponding warmup completes, so RSS growth measures steady-state retention instead of rate-specific Go/SQLite cold-start allocation.

- 50 requests/second for 600 seconds, followed by 100 requests/second for 600 seconds
- 70% valid validation, 10% idempotent activation, 20% deliberate business rejection
- Health checked continuously during both phases
- Every load request uses a unique deterministic nonce and exercises the same SQLite
  replay-claim path as ordinary public traffic. The versioned production template fixes
  replay capacity at 131,072 entries, above the 90,000 entries required by both phases.
- Every public result is accepted only after strict envelope decoding, response-signature
  verification against the protected SPKI/key-ID files, request correlation, and exact
  decision/code/binding semantics. HTTP status alone is never evidence of success.

## Acceptance thresholds

The run fails if any threshold is exceeded:

- p95 latency > 250 ms
- unexpected errors >= 0.1%
- average CPU >= 70%
- free memory <= 1 GiB
- RSS growth >= 10%
- any SQLite `BUSY` or `LOCKED` event
- any health probe that is not HTTP 200

## Evidence rules

Record only aggregate counters, latency percentiles, CPU/RSS summaries, memory floor,
health continuity, and exit statuses. Do not record cards, machine hashes, nonces,
tokens, signatures, private keys, cookies, or response bodies. After the run, stop the
exact test PID, run `license-dbcheck`, require `quick_check=ok`, and remove only the
validated disposable data tree. If any gate fails, leave the public Nginx include absent.
The protected expected-count manifest must require non-empty workload tables and exact or
minimum aggregate counts; empty, missing, or referentially orphaned rows fail acceptance.

## Production-equivalent installed-binary evidence

Status: **PASSED on 2026-09-13 before public-route enablement**.

Source commit was `63c72139a02ece768a82bcc1b2bc8fa15c0b6f90`; the tested installed
license binary SHA-256 was
`624658db19833d60be0b2ebb2b8e60129fdd0bfd26d2ddaf47a8e1bea997afff`.
The disposable instance used the same binary and systemd security profile on
`127.0.0.1:18093`.

- 50 RPS / 600 s: 30,000 requests; 24,000 success; 6,000 expected business
  rejection; 0 unexpected; p50 5.74 ms; p95 9.10 ms; health 600/600; CPU 7.875%;
  RSS 13,612→14,392 KiB (+5.730%); memory floor 2,766,636 KiB; busy/locked 0/0.
- 100 RPS / 600 s: 60,000 requests; 48,000 success; 12,000 expected business
  rejection; 0 unexpected; p50 5.94 ms; p95 9.774 ms; health 600/600; CPU 19.244%;
  RSS 15,592→15,484 KiB (-0.693%); memory floor 2,781,872 KiB; busy/locked 0/0.

The same-commit checker returned `quick_check=ok`, schema version 3, and all aggregate
invariants passed. The public route was absent during measurement. A 459-byte final
manifest retained after a cleanup-script exit was preserved, not deleted, in a clearly
named residual directory for audit; no production data or unrelated server path was
removed.
