# PLAN — Job-based web crawler

Status: APPROVED (2026-10-23). Timebox: build 12:30–4:00 ET, docs/zip 4:00–4:20.

## Approach

**Stack:** .NET 8, ASP.NET Core minimal APIs, EF Core 8 + Npgsql (PostgreSQL 16), MassTransit 8.x on RabbitMQ,
`Microsoft.Extensions.Http.Resilience`, AngleSharp (HTML parsing), xUnit. UI: Vite + React + TypeScript, react-router.

**Solution layout**
```
src/Crawler.Domain          UrlNormalizer, LinkClassifier, DomainLinkRatio, JobStatus/PageStatus, TreeBuilder (pure, no I/O)
src/Crawler.Infrastructure  EF DbContext + migrations, repositories/upserts, HTML fetcher (typed HttpClient), message contracts
src/Crawler.Api             minimal API endpoints, publishes CrawlPage, /health
src/Crawler.Worker          MassTransit consumer, crawl step orchestration, /health
tests/Crawler.UnitTests     domain tests
tests/Crawler.IntegrationTests  fixture-site crawl test (no network), optional Testcontainers idempotency test
web/                        React app
docker-compose.yml, README.md, DECISIONS.md, .github/workflows/ci.yml
```

**Message (one per job, at-least-once; D-13 supersedes one-per-page)**
```json
CrawlJob { "jobId": "guid" }
```
MassTransit envelope supplies `messageId` and `correlationId` (= jobId). Contracts live in Infrastructure, shared by API and Worker. The API inserts the job and its root page (Pending, depth 0) in one transaction, then publishes.

**Data model**
- `jobs(id uuid pk, url, start_host, max_depth, max_pages, status, failure_reason, created_at, started_at, completed_at, pages_enqueued, pages_processed)`; index `(created_at desc, id)` for history.
- `pages(id bigserial pk, job_id fk, url, url_hash bytea, depth, parent_page_id null, status Pending|Fetched|Skipped|Failed, final_url null, http_status, content_type, outgoing_links, same_domain_links, domain_link_ratio null, error, fetched_at)`; **unique `(job_id, url_hash)`** (hash avoids btree limits on long URLs); index `(job_id, parent_page_id)`.
- `edges(job_id, from_page_id, to_url, to_url_hash, is_same_domain)`; **pk `(from_page_id, to_url_hash)`**; index `(job_id)`. Stores every outgoing link, so "list of discovered outgoing links" comes from here.

**Worker (one consumer per job; the BFS frontier lives in the database, so redelivery resumes)**
1. Load job. Completed/Failed/Canceled → ack and return (duplicate delivery).
2. Pending → Running with a conditional update (sets `started_at` once). Running → resume (redelivery after a crash).
3. Loop: take the next `Pending` page for the job ordered by `(depth, id)`; none left → job Completed.
4. Fetch (resilience pipeline, manual redirects), check `text/html`, parse `<a href>`, normalize, classify, compute ratio. No DB writes yet.
5. One transaction, job row locked `FOR UPDATE`:
   edges `INSERT … ON CONFLICT DO NOTHING`; same-host children with `depth+1 ≤ max_depth` inserted as Pending pages `ON CONFLICT DO NOTHING`, capped by `max_pages - pages_enqueued`;
   page `UPDATE … WHERE status='Pending'` (0 rows → a concurrent duplicate delivery won → rollback);
   counters updated. Back to 3.
   Root page Failed → job Failed with the page's error as `failure_reason`.
6. Gap: a crash between the API's insert and its publish leaves the job Pending with no message. Fix is the EF transactional outbox on the API (slice 9, cuttable); otherwise a stated limitation.

**Retries / DLQ**
- HTTP: standard resilience handler: 10 s per attempt, 20 s total, 3 retries with exponential backoff + jitter on 5xx/408/429/`HttpRequestException`/timeouts. 4xx and non-HTML aren't retried.
- The fetcher never throws for HTTP outcomes: it returns `FetchResult` (Fetched | Skipped | Failed + reason), catching `HttpRequestException`, timeout and broken-circuit exceptions once the pipeline is exhausted. A Failed page is a normal outcome, not a DLQ case, so HTTP retries never multiply with message retries (D-9).
- Redirects: `AllowAutoRedirect = false`; the fetcher follows up to 5 hops itself, resolving and normalizing `Location`, checking same-host on every hop. Off-host hop → Skipped (`redirect off-host`, not fetched); > 5 hops or a loop → Failed. Page keeps its requested URL; `final_url` records where it landed (D-8).
- Message: `UseMessageRetry` exponential (3 tries, 1–10 s) only for transient infra errors (`NpgsqlException { IsTransient: true }`, or a `DbUpdateException` wrapping one). Everything else goes straight to the error queue.
- DLQ: MassTransit's `crawl-job_error` queue (poison + exhausted retries). **Must:** a `Fault<CrawlJob>` consumer marks the job Failed (`WHERE status IN ('Pending','Running')`, reason from the fault), so a dead-lettered job can't stay Running (D-7 as amended by D-13).

**API** (`/api`)
- `POST /jobs` `{url, maxDepth?}` → 202 `{jobId}` (validates absolute http(s) URL, 0 ≤ maxDepth ≤ 5)
- `GET /jobs/{id}` → status, timestamps, failure reason, progress (processed/enqueued/maxPages)
- `GET /jobs/{id}/tree` → nested nodes `{url, depth, status, domainLinkRatio, outgoingLinks[], children[]}` (one query for pages, one for edges, tree built in memory: no N+1)
- `GET /jobs?page=1&pageSize=20` → most recent first + total count
- `POST /jobs/{id}/cancel` (cuttable)
- `GET /health` on API and worker

## Slices (in order)

**Build order (D-10): 0 → 1 → 2 → 3 → 4 → 8 → 7 → 5.** Slice 6 is folded into 3 and 4.

Checkpoints (D-14, projected from slice 0 finishing at 1:18 and the estimates below): **2:40** slices 0–3 done · **3:20** end-to-end crawl works via curl · **3:55** UI works · **4:00** code freeze. Docker (7) and robustness (5) do not fit before freeze as estimated; see the timebox note below.

Postgres-backed tests (D-6): Testcontainers on the locally installed `postgres:16` image (no pull), one container per run via an xUnit collection fixture, migrate once, TRUNCATE between tests, `[Trait("Category","Docker")]`. The fast loop keeps domain, fetcher (stub handler) and tree-builder tests.

| # | Slice | Tests | Must / Cuttable | Est. |
|---|---|---|---|---|
| 0 ✅ | Scaffold: solution, projects, `Directory.Build.props` (nullable, `AnalysisLevel latest-recommended`), `.editorconfig`, compose with postgres + rabbitmq only, README/DECISIONS skeletons | `dotnet build` clean; `docker compose up db rabbitmq` healthy | Must | 15 m |
| 1 ✅ | Domain: `UrlNormalizer`, link classification (scheme filter, same-host), `DomainLinkRatio`, link extraction from HTML string | Unit: relative (`../`, `/`, `?q`), fragment strip, `#top`→self, default port, host case, mailto/tel/javascript/ftp ignored, dedupe, zero links→0, mixed ratio, base href (if supported) | Must | 25 m |
| 2 ✅ | Persistence: DbContext, entities, unique keys/indexes above, initial migration, apply on startup | Migration builds (applied for real by the slice 4 Docker test) | Must | 20 m |
| 3 | API: create/get/tree/history endpoints, validation, ProblemDetails errors, insert job + root page and publish `CrawlJob`, CORS for UI. Also (from slice 6): JSON console logs, request `BeginScope(jobId)`, `/health` with DB + broker checks | `WebApplicationFactory` tests for validation (400) and 404 (in-memory test harness for MassTransit) | Must | 35 m |
| 4 | Worker: `CrawlJob` consumer running the DB-frontier BFS loop + typed fetcher (resilience, manual redirects, `FetchResult`) + completion. Also (from slice 6): consumer `BeginScope(jobId, messageId)` with a per-page `pageId` scope, worker `/health` | Fast: fetcher with stub handler: same-host 301 followed, off-host 301 Skipped, redirect loop Failed, exhausted 5xx → Failed (no throw). Docker: **integration test with local HTML fixtures** via a stub `HttpMessageHandler` (no network) on Testcontainers Postgres: depth limit, dedupe, cycle, max pages, expected ratios, second delivery of a Completed job is a no-op | Must | 40 m |
| 5 | Event robustness: infra-only retry config, error queue, `Fault<CrawlJob>` consumer. Docker tests cut (D-12): documented as a manual check | — | Must | 10 m |
| 6 | ~~Observability~~: folded into slices 3 and 4 (D-10) | `/health` hit in the slice 7 smoke run | — | 0 m |
| 7 | Docker: one multi-stage Dockerfile shared by API and worker (build arg picks the project), full compose up, end-to-end smoke against a real small site | Manual smoke, recorded in README; `/health` on API and worker | Must | 15 m |
| 8 | React UI: Start, Job Details (poll every 1.5 s until terminal; progress as "processed / enqueued" text; recursive tree with ratio), History (paged, clickable), loading/error states | Manual run; (cuttable) one Vitest for tree component | Must | 35 m |
| 9 | Transactional outbox on the API (MassTransit EF outbox) | Covered by slice 4 test still passing | Cuttable | 15 m |
| 10 | Cancel job (API + worker skip + UI button) | API test: cancel → status Canceled; worker skips | Cuttable | 15 m |
| 11 | GitHub Actions: restore, build, unit tests | Workflow YAML lint | Cuttable (cheap, do if time) | 10 m |
| 12 | SSRF guard (block loopback/private/link-local targets), robots.txt, UI in compose, SSE progress | — | Cuttable | — |
| 13 | Docs: README (run, architecture, message schema, idempotency, retry/DLQ, limitations, built-first / cut / next, assumptions), DECISIONS.md final pass, summary | Read-through against rubric | **Must, protected 4:00–4:20** | 20 m |

Must-have build total (slices 0–8) = 195 m after D-12/D-13, plus 20 m docs. Slice 0 finished at 1:18 (33 m late), leaving 162 m to freeze for 180 m of remaining must-have work: **about 20 m still short; next cut pending Jim's ruling (D-14).** (The original 3 h 35 m figure was a miscount; the original slices summed to 235 m.)
Cut order if late: 12 → 10 → 9 → 11 → (next must-have cut: pending, D-14).

## Known limitations to state if not addressed
Exact-host domain match; no robots.txt; no SSRF guard; outbox gap (if 9 cut); one consumer per job, so no parallelism across a job's pages (D-13); no watchdog for a job whose worker crashed without redelivery; two URLs redirecting to the same page are both crawled; JS-rendered links not seen; polling rather than push; duplicate delivery and Fault handling verified manually, not by automated test (D-12).

## Open questions: defaults in use (log each in DECISIONS.md when first relied on)
- Crawl scope: fetch same-host pages only; external links recorded and counted in the ratio, never fetched.
- Starting domain: exact host match, case-insensitive; `www.` and subdomains count as external.
- Ratio: unique normalized http(s) links per page; fragment-only links normalize to the page itself (internal).
  mailto/tel/javascript/other schemes excluded from both counts; zero links -> 0.
- Depth: start page = depth 0; default maxDepth 2; valid range 0–5. Pages at max depth are fetched, parsed and scored, but their links aren't enqueued.
- Max pages: default 200 per job (configurable), counts enqueued pages including the root.
- Tree: first-discovery (BFS) parent; edges table holds the full graph.
- Non-HTML page: Skipped, ratio null. HTTP failure after retries: Failed. Redirects followed manually (max 5), same-host check on every hop.
- Job Failed only if the root page fails or an unrecoverable error occurs; otherwise Completed with per-page statuses.
- Normalization: lowercase scheme/host, drop default port and fragment, empty path -> `/`, keep query and path case/trailing slash as-is.
- No auth. No robots.txt (limitation). UI runs via `npm run dev`, not in compose.
- Submission: zip including `.git`; summary at top of README plus a short email.

## Decisions taken before build
- D-1 Messaging: MassTransit 8.x on RabbitMQ (pinned < 9 for licensing).
- D-2 One `CrawlPage` message per page; fallback to one message per job if the 2:30 checkpoint is missed.
- D-3 Commit after Jim approves each slice's diff summary.
- D-4 xUnit plain asserts; no FluentAssertions.
- D-5 to D-14 (plan review and schedule cuts, 2026-10-23): see DECISIONS.md.
