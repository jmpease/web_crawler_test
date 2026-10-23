# Job-based web crawler

## Summary
A user submits a URL in a React UI. The **Crawl API** stores a job and publishes one `CrawlJob` message to RabbitMQ. A separate **Crawl Worker** crawls the start host breadth-first (default depth 2, at most 200 pages), computes each page's **Domain Link Ratio** (same-host links / all unique http(s) links, 0 when there are none), and stores jobs, pages and edges in PostgreSQL. The UI shows the job ID, status, progress and page counts while the job runs, the page tree with each page's ratio and links, and a paged history.

What's built: .NET 8 minimal API and worker, EF Core on PostgreSQL, MassTransit 8 on RabbitMQ with retry for transient database errors, a dead-letter queue and a fault consumer that fails dead-lettered jobs, an HTTP fetcher with timeouts, retries and manually checked redirects, structured JSON logs carrying the job id, `/health` on both services, unit tests for normalization and ratios, a crawl test over local HTML fixtures on real Postgres (Testcontainers), and one `docker compose up --build` for everything except the UI.

Quick start: `docker compose up --build`, then `cd web && npm ci && npm run dev` and open `http://localhost:5173`. Details under [Run](#run).

This was built in one sitting on 2026-10-23 inside the assessment's 4-hour timebox (12:30–4:20 pm ET, including these docs), using Claude Code as a pair programmer. `PLAN.md` holds the plan and slice order; `DECISIONS.md` logs every design decision with the options considered and the trade-off.

## Architecture
```
React UI ──HTTP──> Crawl API ──CrawlJob──> RabbitMQ ──> Crawl Worker ──HTTP──> target site
                      │                                     │
                      └──────────────> PostgreSQL <─────────┘
```
- **Projects:** `Crawler.Domain` (URL normalization, link extraction with AngleSharp, ratio, tree building; no I/O, no EF or MassTransit), `Crawler.Infrastructure` (EF Core context and migrations, the worker's store, HTTP fetcher, message contract and bus setup), `Crawler.Api`, `Crawler.Worker`. The API and the worker are separate processes and containers built from one Dockerfile.
- **One message per job (D-13).** The API publishes a single `CrawlJob`; one consumer crawls the whole job. The breadth-first frontier lives in the database as the job's `Pending` pages, not in the broker, so a redelivered message resumes where the last attempt stopped and there's no per-page completion race to manage. The cost is no parallelism within a job.
- **Who writes what:** the API inserts the job and its root page (depth 0, `Pending`) in one transaction before publishing, so the worker never creates jobs and the root page exists before any message does. The worker writes everything the crawl learns: page outcomes, new child pages, edges, the job's status, timestamps and counters. The fault consumer (in the worker) is the only other writer, and it only moves a Pending or Running job to Failed. Both services share one database. That couples them through the schema, which is acceptable at this size and keeps job creation and the crawl's writes transactional.
- **HTTP failures are data, not exceptions (D-9).** The fetcher returns Fetched, Skipped or Failed with a reason, so a dead site produces Failed pages, never message retries or dead letters.
- **Message contract in one place:** `Crawler.Infrastructure/Messaging/CrawlJob.cs`, referenced by both services.

## Data model and SQL
- `jobs(id uuid, url, start_host, max_depth, max_pages, status, failure_reason, created_at, started_at, completed_at, pages_enqueued, pages_processed)`. Index `(created_at DESC, id)` serves the history query's order and paging.
- `pages(id bigserial, job_id, url, url_hash, depth, parent_page_id, status, final_url, http_status, content_type, outgoing_links, same_domain_links, domain_link_ratio, error, fetched_at)`. **Unique `(job_id, url_hash)`**: a normalized URL is stored and crawled once per job. `url_hash` is the SHA-256 of the normalized URL, which keeps the unique index small however long the URL. Index `(job_id, parent_page_id)`.
- `edges(job_id, from_page_id, to_url, to_url_hash, is_same_domain)`, **primary key `(from_page_id, to_url_hash)`**, index `(job_id)`. Every unique outgoing link of every fetched page, internal and external.
- **Tree:** each page appears once, under the page that first discovered it (`pages.parent_page_id`, the BFS parent). Edges point at URLs rather than page rows because most targets (external links, links past the depth or page limit) never become pages; join `edges.to_url_hash = pages.url_hash` within a job to get page-to-page relations. `GET /api/jobs/{id}/tree` reads the pages in one query and the edges in another and builds the tree in memory (no N+1).
- The input URL is stored **normalized** (`HTTPS://Example.COM:443` becomes `https://example.com/`).

## Idempotency
Delivery is at-least-once, so the worker must tolerate duplicates:
- A message for a job that is already Completed, Failed or Canceled is acknowledged and ignored. Pending → Running is a conditional update that sets `started_at` only once.
- Each page's result is written with `UPDATE … WHERE status = 'Pending'`. If another delivery saved it first, zero rows change and the whole transaction (edges, children, counters) rolls back.
- Child pages are inserted with `INSERT … WHERE NOT EXISTS … ON CONFLICT (job_id, url_hash) DO NOTHING` while the job row is locked `FOR UPDATE`, so the max-pages cap and counters stay exact. Edges use `ON CONFLICT DO NOTHING` on their primary key.
- Finishing the job is `WHERE status = 'Running'`; the fault consumer's update is `WHERE status IN ('Pending', 'Running')`.
- Tested: a second delivery of a completed job changes no rows and makes no requests. Concurrent duplicate deliveries rely on the same conditions but have no automated test.

## Worker
- One consumer per job (endpoint `crawl-job`). The frontier is the job's `Pending` pages in the database, taken shallowest first in discovery order, so a redelivered message resumes where the last attempt stopped. A message for a job that is already Completed, Failed or Canceled is acknowledged and ignored.
- Each page is fetched, then its outcome, outgoing links (`edges`) and new same-host child pages are written in one transaction with the job row locked (`FOR UPDATE`). The page update is conditional on `status = 'Pending'`, so a concurrent duplicate delivery can't count a page twice. Children are enqueued only while `depth + 1 ≤ maxDepth` and the job is under `maxPages`.
- Page statuses: `Fetched` (HTML, links parsed), `Skipped` (not HTML, body over 2 MB, or a redirect off the start host), `Failed` (HTTP error after retries, network error, timeout, redirect loop or more than 5 redirects). The job is `Failed` if the start page wasn't Fetched (it failed, wasn't HTML, or redirected off-host), with the reason "Start page failed: …" or "Start page skipped: …"; otherwise `Completed`, with per-page statuses in the tree.
- Fetching: descriptive `User-Agent`, no automatic redirects (each hop is checked against the start host before it is requested), and the standard resilience handler per host: 10 s per attempt, 20 s total, 3 retries with exponential backoff (from 500 ms) and jitter on 5xx, 408, 429, network errors and timeouts. 4xx and non-HTML responses aren't retried. The body is read under a separate 10 s timeout. HTTP failures become page results, never exceptions, so they don't trigger message retries (D-9).

## Retries and dead-lettering
- **What counts as transient:** only database errors Npgsql marks transient (lost connection, timeout, serialization failure, too many connections, and similar), found anywhere in the exception chain (EF wraps them in `DbUpdateException` from `SaveChanges`, and in an `InvalidOperationException` "likely due to a transient failure" elsewhere), plus a `SocketException` from failing to resolve the database host, which Npgsql doesn't wrap. HTTP problems are not exceptions here; they become page results (see above).
- **Message retry:** on both worker endpoints, 5 in-process retries with exponential backoff from 1 s to 30 s (about a minute in all). A retry reruns the consumer, which resumes the job from its Pending pages, so pages already saved are not fetched again.
- **Dead letter queue:** a non-transient exception, or a sixth transient failure, moves the message to `crawl-job_error` and MassTransit publishes `Fault<CrawlJob>`. The fault consumer (endpoint `crawl-job-fault`) marks the job Failed with `failure_reason` = "Crawl aborted by an unrecoverable worker error: <type>: <message>". It only touches a Pending or Running job, so a late or duplicate fault can't overwrite a Completed one. Messages in `crawl-job_error` stay there for inspection; nothing replays them automatically. Move one back to `crawl-job` from the RabbitMQ management UI (http://localhost:15672) to retry the job; it resumes where it stopped.
- **Worker shutdown:** stopping the worker mid-crawl cancels the consumer, and MassTransit returns the message to `crawl-job` rather than dead-lettering it. The next worker resumes the job.

## API
All errors are ProblemDetails (`application/problem+json`); validation errors are keyed by field name. Enums are serialized as strings.

| Method and path | Result |
|---|---|
| `POST /api/jobs` `{"url": "https://example.com", "maxDepth": 2}` | `202` `{"jobId"}` with `Location: /api/jobs/{id}`. `url` must be an absolute http(s) URL (max 2048 chars); `maxDepth` is optional, 0–5, default 2. Inserts the job and its root page (depth 0, `Pending`) in one transaction, then publishes `CrawlJob`. |
| `GET /api/jobs/{id}` | Status, timestamps, failure reason, progress (`pagesProcessed` / `pagesEnqueued`, `maxPages`), `maxPagesReached`, and `pageCounts` by outcome (`pending`, `fetched`, `skipped`, `failed`); `404` if unknown. History rows carry the same fields with `pageCounts` null. |
| `GET /api/jobs/{id}/tree` | `{jobId, status, root}`; each node has `url, depth, status, domainLinkRatio, error, outgoingLinks[{url, isSameDomain}], children[]`. Children are in discovery order; links sorted by URL. |
| `GET /api/jobs?page=1&pageSize=20` | `{items, page, pageSize, total}`, most recent first; `pageSize` 1–100. |
| `GET /health` | `200 Healthy` when PostgreSQL and the RabbitMQ bus are reachable, otherwise `503`. |

## UI
Vite + React + TypeScript in `web/`, no UI framework. Routes: `/` starts a crawl (URL and max depth; validation errors from the API appear under each field), `/jobs/{id}` shows the job, `/history?page=N` lists jobs 20 per page, most recent first; a row opens its job.

The job page polls `GET /api/jobs/{id}` every 1.5 s until the job is Completed, Failed or Canceled, and refetches the tree only when the processed count or status changes. It shows the job ID, URL, status, progress ("processed / enqueued", plus a note when the page limit was reached), page counts by outcome, any failure reason and the created, started and completed times. Each tree node shows its status, URL, ratio (a dash when the page wasn't scored), depth, any error, and a collapsible list of outgoing links marked internal or external. A failed poll is shown and retried on the next tick; an unknown job id stops polling with a not-found message.

## Message schema
```json
CrawlJob { "jobId": "guid" }
```
- `jobId`: the job to crawl. Everything else (URL, start host, limits) is read from the `jobs` row, so the message can't disagree with the database.

Published to the `Crawler.Infrastructure.Messaging:CrawlJob` exchange and consumed from the `crawl-job` queue. The MassTransit envelope adds `messageId` (unique per publish) and `correlationId` (= `jobId`, used in every log scope), plus `messageType`, `sentTime` and host details. Delivery is at-least-once.

When a `CrawlJob` is dead-lettered, MassTransit publishes `Fault<CrawlJob>` (`faultedMessageId`, `exceptions[]` with type and message, and the original `message`), consumed from `crawl-job-fault`.

## Logging
JSON console logs with scopes. Every request under `/api/jobs/{id}`, and job creation, logs inside a scope carrying `correlationId` = job id. In the worker, every log line for a message carries `correlationId` (job id) and `messageId`, and per-page lines add `pageId`. Page URLs are logged; page bodies never are. The worker logs Polly (retry pipeline) at Warning and above, so retries and failures show but successful attempts don't.

## Prerequisites
- .NET 8 SDK (pinned by `global.json`)
- Docker Desktop

## Run
Everything except the UI in Docker:
```
docker compose up --build
```
The API is on `http://localhost:5129` and the worker's `/health` on `http://localhost:5250`. Both images are built from the one `Dockerfile`; the `PROJECT` build arg picks `Crawler.Api` or `Crawler.Worker`. The containers run in the `Production` environment and get their connection settings from compose, which builds them from the credential variables under Configuration. Then start the UI as below; it calls the API on the same port either way.

Or run the API and worker from source, with only the infrastructure in Docker:
```
docker compose up -d db rabbitmq
dotnet run --project src/Crawler.Api
dotnet run --project src/Crawler.Worker
```
Then the UI (Node 20.19+ or 22.12+):
```
cd web
npm ci
npm run dev
```
Open `http://localhost:5173`. The UI calls the API at `http://localhost:5129`; set `VITE_API_URL` to point it elsewhere (and add that origin to `Cors__AllowedOrigins`).

PostgreSQL listens on `localhost:5432`, RabbitMQ on `localhost:5672` (management UI on `http://localhost:15672`). The API listens on `http://localhost:5129`, the worker's `/health` on `http://localhost:5250`. Start the API first: it applies migrations.

The API applies pending EF Core migrations when it starts; the worker never migrates (D-16).

## Migrations
`dotnet-ef` is a repo-local tool:
```
dotnet tool restore
dotnet ef migrations add <Name> --project src/Crawler.Infrastructure --startup-project src/Crawler.Infrastructure --output-dir Persistence/Migrations
```
A design-time factory supplies the context, so no host is needed. It uses `ConnectionStrings__Crawler` if set, otherwise the compose defaults.

## Test
Fast tests (no Docker):
```
dotnet test --filter Category!=Docker
```
All tests, including Testcontainers-based ones (needs Docker running and the `postgres:16-alpine` image). Ryuk, Testcontainers' cleanup container, is disabled so no image is pulled; the fixture disposes its container itself:
```
TESTCONTAINERS_RYUK_DISABLED=true dotnet test
```
API tests host the API with `WebApplicationFactory` and MassTransit's in-memory test harness in place of RabbitMQ. Validation tests need no database; persistence, 404, history, tree and health tests run on Testcontainers Postgres (`Category=Docker`).

UI checks: `npm run build` in `web/` type-checks (`tsc`) and bundles; `npm test` runs Vitest. The one UI test renders the page tree to static markup (no DOM library) and checks nesting, ratios, link counts, errors and statuses.

Manual smoke run (2026-10-23, `docker compose down -v` then `docker compose up --build`): API and worker `/health` both `200 Healthy`. `https://example.com` at depth 1 finished Completed with 1 page Fetched; that page currently has no links, so its ratio is 0. `https://books.toscrape.com` at depth 1 finished Completed in about 4 s, with 74 of 74 pages Fetched and a root ratio of 1. Worker log lines carry `correlationId`, `messageId` and `pageId`, and no HTML appears in the logs.

Manual dead-letter check (2026-10-23, compose stack): a Pending job inserted with no root page and a hand-published `CrawlJob` (through the management API) failed with `InvalidOperationException` at the end of the crawl. The message went straight to `crawl-job_error` with no retries, and the fault consumer marked the job Failed with that exception as the reason; its log line carried `correlationId`, `messageId` and `faultedMessageId`. Stopping the worker 3 s into a depth-2 crawl of `https://books.toscrape.com` left the job Running at 113 / 200 with its message back on `crawl-job` and nothing added to the error queue. After `docker compose start worker` it finished Completed at 200 / 200.

Acceptance run (2026-10-23, fresh clone, `docker compose up --build`): all four services up; API and worker `/health` 200. A job with only `url` got maxDepth 2 and finished Completed at 200 / 200 pages (`maxPagesReached: true`), every tree node with a ratio. An invalid URL and `maxDepth: -1` each returned 400 ProblemDetails. `https://no-such-host.invalid` ended Failed ("Start page failed: Request failed: Name or service not known") with `completedAt` set. History pages of size 2 were newest first and disjoint. The history query uses `ix_jobs_created_at_id`. A malformed message published to the `CrawlJob` exchange landed in `crawl-job_error` and the worker stayed healthy. The job id appeared as `correlationId` in both API and worker logs; no HTML in the logs.

Database outage check: stopping PostgreSQL for 5 s during a crawl first exposed a bug: EF reported the dropped connection as an `InvalidOperationException`, which the retry filter didn't recognize, so the job was dead-lettered and, with the fault consumer failing the same way, left Running (D-28). The clean-clone rerun then found a second gap: while the `db` container is down its hostname doesn't resolve, and Npgsql throws that `SocketException` unwrapped, so the third attempt dead-lettered the job (D-29). With both fixes and the longer retry window, two runs with PostgreSQL stopped for 8 s each logged `R-RETRY` entries and finished Completed with nothing dead-lettered.

No automated test touches the network. `StubSite` is an `HttpMessageHandler` standing in for a website. Fetcher tests (fast) cover redirects, retries and content types through the real resilience pipeline. Crawl tests (`Category=Docker`) run `JobCrawler` over the local fixture site in `tests/Crawler.IntegrationTests/Fixtures/site` on real Postgres. They check BFS order, the depth limit, dedupe, a cycle, max pages, ratios and stored links, a failed start page, and that a second delivery of a completed job changes nothing. A Docker test checks that a dead-lettered job is marked Failed but a finished one is left alone, and fast tests check which exceptions count as transient.

## Configuration
Compose reads dev-only credentials from environment variables, with defaults:

| Variable | Default |
|---|---|
| `POSTGRES_DB` | `crawler` |
| `POSTGRES_USER` | `crawler` |
| `POSTGRES_PASSWORD` | `crawler_dev` |
| `RABBITMQ_USER` | `crawler` |
| `RABBITMQ_PASSWORD` | `crawler_dev` |

| Setting (env var form) | Default | Used by |
|---|---|---|
| `ConnectionStrings__Crawler` | compose database (Development only) | API, worker |
| `RabbitMq__Host`, `__Port`, `__VirtualHost`, `__Username`, `__Password` | `localhost`, `5672`, `/`, `guest`, `guest` (Development: compose credentials) | API, worker |
| `Fetcher__UserAgent` | `JobCrawler/1.0 (job-based web crawler; same-host only)` | worker |
| `Fetcher__MaxBodyBytes` | `2097152` (2 MB) | worker |
| `Fetcher__BodyTimeout` | `00:00:10` | worker |
| `Fetcher__RetryBaseDelay` | `00:00:00.5` | worker |
| `Jobs__MaxPages` | `200` | API (stored on each job) |
| `Cors__AllowedOrigins__0` | `http://localhost:5173` (Vite dev server) | API |
| `Database__MigrateOnStartup` | `true` | API |
| `VITE_API_URL` (dev server or build environment) | `http://localhost:5129` | UI |

`appsettings.Development.json` points at the compose database and broker with the dev credentials above.

## Assumptions and trade-offs
Where the spec leaves a choice open, this is what the build does:
- **Crawl scope:** only pages on the start host are fetched. External links are recorded (edges) and counted in the ratio, never fetched.
- **Starting domain:** the exact host of the start URL, case-insensitive. `www.` and other subdomains count as external.
- **Ratio counting:** unique normalized http(s) links per page. A fragment-only link (`#top`) or a link to the page itself counts as an internal link to that page. `mailto:`, `tel:`, `javascript:`, `ftp:`, `data:` and other schemes are excluded from both counts. No links gives 0. `<base href>` is honored.
- **Depth:** the start page is depth 0; `maxDepth` defaults to 2 and must be 0–5. Pages at `maxDepth` are fetched, parsed and scored, and their links are stored, but not enqueued.
- **Max pages:** default 200 per job (`Jobs__MaxPages`), counting enqueued pages including the root. Links found after the cap are still stored as edges.
- **Tree:** each page appears once, under its first-discovery (BFS) parent; cycles and shared children don't repeat. The edges table holds the full graph.
- **Non-HTML, failures, redirects:** detection uses the response `Content-Type` (`text/html`, `application/xhtml+xml`). Non-HTML pages and bodies over 2 MB are Skipped with no ratio. HTTP errors after retries, network errors and timeouts are Failed with the reason. Redirects are followed by the worker, up to 5 hops, checking the start host on every hop; an off-host redirect is Skipped and not requested. A page keeps its requested URL and records `finalUrl`.
- **Job outcome:** Failed if the start page wasn't fetched (failed, non-HTML, or redirected off-host) or the message was dead-lettered; otherwise Completed, with per-page statuses showing any failures below the root (D-25).
- **Normalization:** lowercase scheme and host, drop the default port, user info and fragment, empty path becomes `/`; path case, trailing slash and query string are kept as-is.
- **.NET 8**, as the assessment requires. Its support ends on 2026-11-10, so moving to .NET 10 (LTS) is the first item under next steps.
- **UI** runs with `npm run dev`, not in compose (the spec's compose list is API, worker, database and broker).
- **Out of scope:** authentication, robots.txt and SSRF protection (see limitations).

## Known limitations
- **Publish-after-commit gap:** the API commits the job, then publishes. A crash between the two leaves a Pending job with no message. MassTransit's EF transactional outbox would close it; it was cut for time (slice 9).
- **No SSRF guard:** a user can point the worker at internal or private addresses (localhost, 10.x, cloud metadata). Must be fixed before this faces untrusted users.
- **No robots.txt**, and exact-host matching only (`example.com` and `www.example.com` are different sites). A start URL that redirects to another host fails with a reason naming that host.
- **No cancel:** the `Canceled` status exists and is honored by the worker and UI as terminal, but nothing sets it (slice 10 cut).
- **One consumer per job:** no parallelism within a job, and one slow site holds a consumer for its whole crawl. A crawl longer than RabbitMQ's 30-minute delivery-acknowledgement timeout is redelivered and resumes.
- **Database outages longer than about a minute** exhaust the in-process retries and dead-letter the job. If the database is still down, the fault consumer can't mark it Failed either and the job stays Running, with the fault in `crawl-job-fault_error`. Dead-lettered messages are not replayed automatically. There is no watchdog for jobs stuck Running.
- **Duplicate pages via redirects:** two URLs that redirect to the same page are both crawled.
- **Static HTML only:** links added by JavaScript aren't seen.
- **API after a database restart:** the first query on a stale pooled connection fails with a 500; the next request succeeds. The UI's next poll recovers on its own.
- **Polling:** the UI polls every 1.5 s rather than receiving pushes (SSE or SignalR).
- **Test coverage gaps:** the consumers' MassTransit wiring, concurrent duplicate deliveries and the dead-letter path are checked by hand (see Test), not by automated tests. No CI pipeline.

## Built first, cut, and next
**Built first, and why:** the domain rules (normalization, link extraction, ratio) with unit tests, because correctness is the largest share of the grade and everything else depends on them. Then the schema with its unique keys, so idempotency is enforced by the database rather than by code paths; then the API and the worker, to get one crawl working end to end; then the UI, because the user flow is a core requirement; then Docker; then retries and dead-lettering, which the database-held frontier made cheap. A final pass against the spec added the visible job ID, failing jobs whose start page couldn't be fetched, and page counts in the job summary.

**Cut, and why** (in this order, as time ran short):
- One message per page (D-2) became one message per job (D-13): same idempotent schema, much less coordination code.
- Automated tests for duplicate delivery and the fault consumer (D-12), replaced by documented manual checks; one store-level fault test came back later.
- The transactional outbox (slice 9): the gap is narrow and documented above.
- Cancel (slice 10): optional in the spec.
- SSRF guard, robots.txt, UI in compose and SSE progress (slice 12).
- GitHub Actions CI (slice 11).

**Next:**
1. Upgrade to .NET 10 LTS before .NET 8 support ends (2026-11-10).
2. SSRF guard: resolve the host and refuse loopback, private, link-local and metadata addresses on every hop.
3. MassTransit EF outbox on the API, closing the publish-after-commit gap.
4. Cancel endpoint and UI button, with the worker re-reading the job's status between pages so a running crawl stops promptly.
5. A watchdog that fails or requeues jobs Running with no progress for N minutes, plus broker-level delayed redelivery for longer outages.
6. CI running build, fast tests, Docker tests and the UI build; automated tests for the consumers with the MassTransit test harness.
7. robots.txt, `www.`-aware host matching as an option, and SSE progress.
8. Parallel fetches within a job (a bounded worker pool over the database frontier).
