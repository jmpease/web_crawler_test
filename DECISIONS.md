# Decisions

A numbered log of design decisions. Entries are never rewritten; a later entry supersedes an earlier one.

## D-1 Messaging: MassTransit 8.x on RabbitMQ
- **Decision:** MassTransit 8.x on RabbitMQ, pinned below 9.
- **Options:** MassTransit 8; raw RabbitMQ.Client; MassTransit 9.
- **Why:** retries, error queues, fault events and an EF outbox come built in. Version 9 has a commercial license.
- **Trade-off:** we stay on the 8.x line.

## D-2 One message per page
- **Decision:** one `CrawlPage` message per page. If the 2:30 checkpoint (now 2:50, see D-5) is missed, fall back to one message per job.
- **Options:** per page; per job.
- **Why:** per-page messages allow parallel workers, per-page retries and exercise idempotency.
- **Trade-off:** more moving parts (counters, completion detection, duplicate deliveries).

## D-3 Commit cadence
- **Decision:** commit after Jim approves each slice's diff summary.
- **Why:** reviewable history; one slice per commit.
- **Trade-off:** pauses between slices.

## D-4 Test assertions
- **Decision:** xUnit with plain asserts; no FluentAssertions.
- **Why:** FluentAssertions 8+ is commercially licensed; plain asserts are enough.
- **Trade-off:** slightly less readable failure messages.

## D-5 Checkpoints rebaselined to the estimates
- **Decision:** checkpoints at 2:00 (slices 0–3) and 2:50 (end-to-end via curl). If 2:00 is missed, cut 12 and 10 right away. Slice 2's database smoke test is dropped (slice 4's Docker test applies the migration).
- **Options:** keep 1:45/2:30 and start cutting early; cut estimates; move checkpoints.
- **Why:** the original checkpoints were about 20 minutes ahead of the slice estimates, so they would be missed even if every estimate held.
- **Trade-off:** later warning of slippage. Open: must-have slices total 245 minutes against a 210-minute build window; the UI checkpoint waits on Jim's ruling.

## D-6 Postgres-backed tests use Testcontainers on the local image
- **Decision:** worker and duplicate-delivery tests run against Testcontainers Postgres using the locally installed `postgres:16` image. One container per run (xUnit collection fixture), migrate once, TRUNCATE between tests, tagged `Category=Docker`.
- **Options:** EF in-memory provider; SQLite; Testcontainers.
- **Why:** the worker step depends on `ON CONFLICT`, `FOR UPDATE` and `RETURNING`, which only real Postgres supports. Docker Desktop is available.
- **Trade-off:** these tests sit outside the fast loop and run before each commit, not after every change. Slice 5's duplicate-delivery test is no longer cuttable.

## D-7 Fault consumer is a must-have
- **Decision:** a `Fault<CrawlPage>` consumer marks a dead-lettered page Failed through the same "finish page" transaction (`WHERE status='Pending'`, increments `pages_processed`, completion check).
- **Options:** leave it cuttable; add a job watchdog; Fault consumer.
- **Why:** completion requires processed == enqueued, so one poison message would otherwise leave its job Running forever. It is about 10 minutes of work.
- **Trade-off:** a crashed worker (not a fault) can still strand a page; no watchdog remains a stated limitation.

## D-8 Redirects followed manually
- **Decision:** `AllowAutoRedirect = false`. The fetcher follows up to 5 hops, normalizing each `Location` and checking same-host on every hop. Off-host hop → Skipped without fetching; too many hops or a loop → Failed. The page keeps its requested URL; `final_url` records the destination.
- **Options:** automatic redirects with a check on the final URL; manual redirects.
- **Why:** automatic redirects fetch off-host pages before any check can run, which breaks the same-host rule and widens SSRF exposure.
- **Trade-off:** a little more fetcher code. Two URLs redirecting to the same page are both crawled (stated limitation).

## D-9 Fetcher returns results; message retry is infrastructure-only
- **Decision:** the fetcher returns `FetchResult` (Fetched | Skipped | Failed + reason) and never throws for HTTP outcomes. `UseMessageRetry` handles only transient Postgres errors. HTTP total timeout lowered from 30 s to 20 s.
- **Options:** let HTTP exceptions reach message retry; separate the layers.
- **Why:** stacking 30 s of HTTP retries inside 3 message retries could hold a consumer for about 90 s on one bad page.
- **Trade-off:** a page whose host is down is marked Failed after about 20 s with no second attempt via the broker.

## D-10 Trim scope and build the UI before Docker and robustness (supersedes D-5's checkpoints)
- **Decision:** fold slice 6 (log scopes, health checks) into slices 3 and 4; show UI progress as "processed / enqueued" text; use one multi-stage Dockerfile for API and worker. Build order 0 → 4, then 8 (UI), 7 (Docker), 5 (robustness). Checkpoints: 2:05 slices 0–3, 2:55 end-to-end via curl, 3:30 UI, 3:45 compose up, 4:00 freeze.
- **Options:** (A) trim scope only; (B) reorder only; (C) both. Jim chose C.
- **Why:** must-have slices totalled 245 m against a 210 m window. Trimming brings it to 225 m; reordering puts the visible deliverable and the required compose check ahead of the remaining overrun.
- **Trade-off:** slice 5 runs last with about 15 of its 30 m. Its retry config and Fault consumer go in first; its Docker tests are the first must-have to cut, documented as a manual check. A slip in slices 0–4 now pushes the UI past 3:30.
