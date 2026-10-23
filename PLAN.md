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

**Message (one per page, at-least-once)**
```json
CrawlPage { "jobId": "guid", "pageId": 123, "url": "https://example.com/a", "depth": 1 }
```
MassTransit envelope supplies `messageId` and `correlationId` (= jobId). Contracts live in Infrastructure, shared by API and Worker.

**Data model**
- `jobs(id uuid pk, url, start_host, max_depth, max_pages, status, failure_reason, created_at, started_at, completed_at, pages_enqueued, pages_processed)`; index `(created_at desc, id)` for history.
- `pages(id bigserial pk, job_id fk, url, url_hash bytea, depth, parent_page_id null, status Pending|Fetched|Skipped|Failed, http_status, content_type, outgoing_links, same_domain_links, domain_link_ratio null, error, fetched_at)`; **unique `(job_id, url_hash)`** (hash avoids btree limits on long URLs); index `(job_id, parent_page_id)`.
- `edges(job_id, from_page_id, to_url, to_url_hash, is_same_domain)`; **pk `(from_page_id, to_url_hash)`**; index `(job_id)`. Stores every outgoing link, so "list of discovered outgoing links" comes from here.

**Worker step (idempotent)**
1. Load job + page. Job Canceled/Completed or page not `Pending` → ack and return (duplicate or stale delivery).
2. Job `Pending` → `Running` with conditional update (sets `started_at` once).
3. Fetch (resilience pipeline), check `text/html`, parse `<a href>`, normalize, classify, compute ratio. No DB writes yet.
4. One transaction, job row locked `FOR UPDATE`:
   edges `INSERT … ON CONFLICT DO NOTHING`; same-host children with `depth+1 ≤ max_depth` inserted as Pending pages `ON CONFLICT DO NOTHING RETURNING id`, capped by `max_pages - pages_enqueued`;
   page `UPDATE … SET status='Fetched' … WHERE status='Pending'` (0 rows → concurrent duplicate won → rollback);
   counters updated; `pages_processed == pages_enqueued` → job Completed.
5. After commit, publish `CrawlPage` for newly inserted children only.
   Gap: crash between commit and publish strands Pending pages. Fix is MassTransit's EF transactional outbox (slice 9, cuttable); otherwise a stated limitation.

**Retries / DLQ**
- HTTP: standard resilience handler: 10 s per attempt, 30 s total, 3 retries with exponential backoff + jitter on 5xx/408/429/`HttpRequestException`/timeouts. A final failure marks the page Failed and is a normal outcome, not a DLQ case. 4xx and non-HTML aren't retried.
- Message: `UseMessageRetry` exponential (3 tries, 1–10 s) for transient infra errors (`NpgsqlException.IsTransient`, `TimeoutException`, broker hiccups). Validation/deserialization errors are ignored by retry and go straight to the error queue.
- DLQ: MassTransit's `crawl-page_error` queue (poison + exhausted retries). Cuttable: a `Fault<CrawlPage>` consumer marks the page Failed and advances the counter so the job can still finish.

**API** (`/api`)
- `POST /jobs` `{url, maxDepth?}` → 202 `{jobId}` (validates absolute http(s) URL, 0 ≤ maxDepth ≤ 5)
- `GET /jobs/{id}` → status, timestamps, failure reason, progress (processed/enqueued/maxPages)
- `GET /jobs/{id}/tree` → nested nodes `{url, depth, status, domainLinkRatio, outgoingLinks[], children[]}` (one query for pages, one for edges, tree built in memory: no N+1)
- `GET /jobs?page=1&pageSize=20` → most recent first + total count
- `POST /jobs/{id}/cancel` (cuttable)
- `GET /health` on API and worker

## Slices (in order)

Checkpoints: **1:45** slices 1–3 done · **2:30** end-to-end crawl works via curl (if not, fall back: worker crawls the whole job from one message, log in DECISIONS.md) · **3:30** UI works · **4:00** code freeze.

| # | Slice | Tests | Must / Cuttable | Est. |
|---|---|---|---|---|
| 0 | Scaffold: solution, projects, `Directory.Build.props` (nullable, `AnalysisLevel latest-recommended`), `.editorconfig`, compose with postgres + rabbitmq only, README/DECISIONS skeletons | `dotnet build` clean; `docker compose up db rabbitmq` healthy | Must | 15 m |
| 1 | Domain: `UrlNormalizer`, link classification (scheme filter, same-host), `DomainLinkRatio`, link extraction from HTML string | Unit: relative (`../`, `/`, `?q`), fragment strip, `#top`→self, default port, host case, mailto/tel/javascript/ftp ignored, dedupe, zero links→0, mixed ratio, base href (if supported) | Must | 25 m |
| 2 | Persistence: DbContext, entities, unique keys/indexes above, initial migration, apply on startup | Migration builds; (cuttable) Testcontainers smoke | Must | 25 m |
| 3 | API: create/get/tree/history endpoints, validation, ProblemDetails errors, publish root `CrawlPage`, CORS for UI | `WebApplicationFactory` tests for validation (400) and 404 (in-memory test harness for MassTransit) | Must | 30 m |
| 4 | Worker: consumer + idempotent step + typed fetcher with resilience + completion | **Integration test with local HTML fixtures** via a stub `HttpMessageHandler` (no network): depth limit, dedupe, cycle, max pages, expected ratios | Must | 45 m |
| 5 | Event robustness: retry config, error queue, duplicate-delivery handling | Test: same `CrawlPage` handled twice → same page/edge row counts (Testcontainers Postgres; if cut, document as manual check) | Must (config) / Cuttable (container test) | 20 m |
| 6 | Observability: JSON console logs, `BeginScope(jobId, pageId, messageId)` in consumer and API, health checks (DB + broker) | Hit `/health` in smoke run | Must | 15 m |
| 7 | Docker: Dockerfiles for API and worker, full compose up, end-to-end smoke against a real small site | Manual smoke, recorded in README | Must | 20 m |
| 8 | React UI: Start, Job Details (poll every 1.5 s until terminal; progress bar; recursive tree with ratio), History (paged, clickable), loading/error states | Manual run; (cuttable) one Vitest for tree component | Must | 40 m |
| 9 | Transactional outbox (MassTransit EF outbox) | Covered by slice 4 test still passing | Cuttable | 15 m |
| 10 | Cancel job (API + worker skip + UI button) | API test: cancel → status Canceled; worker skips | Cuttable | 15 m |
| 11 | GitHub Actions: restore, build, unit tests | Workflow YAML lint | Cuttable (cheap, do if time) | 10 m |
| 12 | SSRF guard (block loopback/private/link-local targets), robots.txt, UI in compose, SSE progress | — | Cuttable | — |
| 13 | Docs: README (run, architecture, message schema, idempotency, retry/DLQ, limitations, built-first / cut / next, assumptions), DECISIONS.md final pass, summary | Read-through against rubric | **Must, protected 4:00–4:20** | 20 m |

Must-have total ≈ 3 h 35 m including docs. Cut order if late: 12 → 10 → 9 → 11 → container test in 5.

## Known limitations to state if not addressed
Exact-host domain match; no robots.txt; no SSRF guard; outbox gap (if 9 cut); no job-level watchdog for stuck jobs; JS-rendered links not seen; polling rather than push.

## Open questions: defaults in use (log each in DECISIONS.md when first relied on)
- Crawl scope: fetch same-host pages only; external links recorded and counted in the ratio, never fetched.
- Starting domain: exact host match, case-insensitive; `www.` and subdomains count as external.
- Ratio: unique normalized http(s) links per page; fragment-only links normalize to the page itself (internal).
  mailto/tel/javascript/other schemes excluded from both counts; zero links -> 0.
- Depth: start page = depth 0; default maxDepth 2; valid range 0–5. Pages at max depth are fetched, parsed and scored, but their links aren't enqueued.
- Max pages: default 200 per job (configurable), counts enqueued pages including the root.
- Tree: first-discovery (BFS) parent; edges table holds the full graph.
- Non-HTML page: Skipped, ratio null. HTTP failure after retries: Failed. Redirects followed (max 5), same-host check on final URL.
- Job Failed only if the root page fails or an unrecoverable error occurs; otherwise Completed with per-page statuses.
- Normalization: lowercase scheme/host, drop default port and fragment, empty path -> `/`, keep query and path case/trailing slash as-is.
- No auth. No robots.txt (limitation). UI runs via `npm run dev`, not in compose.
- Submission: zip including `.git`; summary at top of README plus a short email.

## Decisions taken before build
- D-1 Messaging: MassTransit 8.x on RabbitMQ (pinned < 9 for licensing).
- D-2 One `CrawlPage` message per page; fallback to one message per job if the 2:30 checkpoint is missed.
- D-3 Commit after Jim approves each slice's diff summary.
- D-4 xUnit plain asserts; no FluentAssertions.
