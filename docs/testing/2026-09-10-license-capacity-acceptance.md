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

## Staging evidence

Status: **PENDING AUTHORIZED LINUX STAGING**.

Do not convert this status to pass from local or simulated runs. The authorized operator
must record the deployment-bundle source commit and binary hashes, both aggregate phase
reports, CPU average, RSS start/end/growth, memory floor, health totals, busy/locked
deltas, database-check aggregate output, and cleanup/public-route assertions. All values
must come from the installed binary's exact `MainPID`; no request fixture or secret value
may be copied into this document.
