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

## D-11 Scaffold choices
- **Decision:** `global.json` pins SDK 8.0.426 (roll forward within 8.0.x). The worker is a web project so it can serve `/health`. Compose and Testcontainers use the locally installed `postgres:16-alpine` and `rabbitmq:3.13-management` images. Analyzers at `latest-recommended`; warnings stay warnings. The test template's `coverlet.collector` was removed because it isn't on the approved package list.
- **Options:** default SDK (10.x is installed and would win); a generic-host worker plus a separate health listener; `postgres:16` (not present locally).
- **Why:** the assessment requires .NET 8; one hosting model for both services; no image pulls during the timebox.
- **Trade-off:** the worker carries the ASP.NET Core runtime; no coverage collection.

## D-12 Cut slice 5's Docker tests
- **Decision:** drop the automated duplicate-delivery and Fault-consumer tests. Slice 5 keeps the retry configuration and the Fault consumer; the README documents a manual check instead.
- **Options:** keep them and cut elsewhere; cut them now; cut them at the end if time runs out.
- **Why:** slice 0 finished at 1:18, 33 m late (the plan review used the start of the window), leaving about 48 m more must-have work than time. These tests were next in the cut order.
- **Trade-off:** duplicate delivery and fault handling have no automated coverage. The slice 4 integration test still covers a second delivery of a completed job.

## D-13 One message per job (supersedes D-2; amends D-7)
- **Decision:** the API publishes one `CrawlJob { jobId }`; one consumer crawls the whole job. The BFS frontier is the job's `Pending` pages in the database, taken in `(depth, id)` order, so a redelivered message resumes where the previous attempt stopped. A `Fault<CrawlJob>` consumer marks the job Failed. The pages/edges schema, idempotent page writes and the API are unchanged.
- **Options:** keep one message per page; switch now; switch at the 2:55 checkpoint as D-2 allowed.
- **Why:** removes the per-page completion race, cross-message counters and most duplicate-delivery handling, saving about 30 m across slices 4–5. Switching now avoids building the per-page path in slices 2–3 and then discarding it.
- **Trade-off:** no parallelism across a job's pages; one slow site holds a consumer for the whole crawl. Idempotency is at job level (Completed/Failed jobs are skipped) plus the existing `WHERE status='Pending'` page writes. The messaging story in the write-up is simpler.

## D-14 Schedule after D-12 and D-13
- **Decision:** checkpoints reprojected from the real start of slice 1 (1:18): 2:40 slices 0–3, 3:20 end-to-end via curl, 3:55 UI, 4:00 freeze.
- **Why:** after D-12 and D-13, 180 m of must-have work remains against 162 m to freeze, so we are still about 20 m short.
- **Trade-off / open:** Docker (7) and robustness (5) don't fit as estimated. The next must-have cut is pending Jim's ruling.

## D-15 HTML parsing lives in Domain, with AngleSharp 1.8.5
- **Decision:** `HtmlLinkExtractor` (AngleSharp) sits in Domain beside `UrlNormalizer` and `LinkAnalysis`. AngleSharp is 1.8.5, the latest 1.x, because 1.1.2 has a moderate-severity advisory (GHSA-pgww-w46g-26qg).
- **Options:** parse in Infrastructure and pass hrefs to Domain; parse in Domain; regex extraction.
- **Why:** parsing a string is pure computation with no I/O, so it keeps the extraction rules (including `<base href>`) under fast unit tests next to the normalization rules. A real parser handles malformed HTML that regex wouldn't.
- **Trade-off:** Domain takes a third-party dependency (MIT, already pre-approved).
