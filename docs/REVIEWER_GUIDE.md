# Reviewer guide

> Written on 2026-10-26, after the assessment window closed. It changes no code: the code and tests are as submitted on 2026-10-23, and so is DECISIONS.md through D-29. The README's "Post-assessment documentation" section lists everything added since.

This guide is for someone reviewing the submission. It explains what the project does, how it is built, why it is built that way, what is missing, and what I would improve in the code that exists. It gathers in one place what is spread across three other files:

- [README.md](../README.md): how to run, test and configure it, plus the submission sections.
- [DECISIONS.md](../DECISIONS.md): the dated decision log (D-1 to D-29). This guide cites entries as D-n.
- [PLAN.md](../PLAN.md): the approved plan, slice order, checkpoints and cuts.

## 0. Summary

A user submits a URL in a React UI. The API stores a crawl job and publishes one message to RabbitMQ. A separate worker crawls the start host breadth-first and computes each page's **Domain Link Ratio**. It stores jobs, pages and links in PostgreSQL. The UI shows live progress, the page tree with ratios and links, and a paged history.

- **Built:** in one sitting on 2026-10-23, inside the assessment's 4-hour timebox (12:30–4:20 pm ET, including docs), with Claude Code as a pair programmer. Each slice was reviewed and approved before it was committed (D-3).
- **Stack:** .NET 8 minimal APIs, EF Core 8 on PostgreSQL 16, MassTransit 8 on RabbitMQ, `Microsoft.Extensions.Http.Resilience`, AngleSharp, and Vite + React + TypeScript.
- **Tests:** 51 xUnit test methods (75 cases once theories expand) and 3 Vitest cases. 14 of the xUnit tests run against real Postgres through Testcontainers (`Category=Docker`). None touch the internet.
- **Verified by hand:** a clean-clone `docker compose up --build`, an end-to-end crawl, a dead-letter check, a mid-crawl worker stop, a database outage during a crawl, and a UI walkthrough. All are recorded in [README › Test](../README.md#test).

## 1. What the project does

### User flow
1. **Start** (`/`): enter a URL and an optional max depth. Validation errors from the API appear under each field.
2. **Job** (`/jobs/{id}`): shows the job ID, status, progress ("processed / enqueued"), page counts by outcome, any failure reason and timestamps. It polls every 1.5 s until the job finishes. The page tree shows each page's status, ratio, depth, error and its internal and external links.
3. **History** (`/history`): lists jobs newest first, 20 per page. Each row opens its job.

### The Domain Link Ratio
For each fetched page: **same-host links ÷ all unique http(s) links**, or 0 when there are none. The counting rules:

- Links are normalized and deduplicated per page.
- A fragment-only link (`#top`) counts as an internal link to the page itself.
- `mailto:`, `tel:`, `javascript:` and other non-http schemes are excluded from both counts.
- `<base href>` is honored.
- "Same host" means the exact host of the start URL, case-insensitive. `www.` and other subdomains count as external.

### Crawl rules
| Rule | Value |
|---|---|
| Scope | Fetch pages on the start host only. External links are recorded and counted, never fetched. |
| Order | Breadth-first: shallowest first, then discovery order. |
| Depth | Start page is depth 0. `maxDepth` defaults to 2, range 0–5. Pages at max depth are fetched and scored, but their links aren't followed. |
| Page cap | 200 pages per job, including the root (`Jobs__MaxPages`). |
| Requests | Descriptive User-Agent. 10 s per attempt, 20 s total per hop, 3 retries on 5xx/408/429/network errors. 2 MB body cap. Up to 5 redirects, each checked against the start host before it is requested. |

### Outcomes
- **Page:** `Fetched` (HTML, parsed and scored), `Skipped` (not HTML, over 2 MB, or redirected off-host) or `Failed` (HTTP error after retries, network error, timeout, redirect loop or more than 5 redirects).
- **Job:** `Completed` if the start page was Fetched. `Failed` if it wasn't (D-25) or if the message was dead-lettered. `Canceled` exists as a status, but nothing sets it yet.

### API
| Endpoint | Purpose |
|---|---|
| `POST /api/jobs` `{url, maxDepth?}` | `202 {jobId}` with a `Location` header |
| `GET /api/jobs/{id}` | Status, timestamps, progress, `maxPagesReached`, `pageCounts` |
| `GET /api/jobs/{id}/tree` | Nested page tree with ratios and outgoing links |
| `GET /api/jobs?page&pageSize` | Paged history, newest first |
| `GET /health` (API and worker) | PostgreSQL and the RabbitMQ bus |

All errors are ProblemDetails. Full shapes are in [README › API](../README.md#api).

## 2. Architecture

### Components
```
React UI ──HTTP──> Crawl API ──CrawlJob──> RabbitMQ ──> Crawl Worker ──HTTP──> target site
                      │                                     │
                      └──────────────> PostgreSQL <─────────┘
```
The API and the worker are separate processes and containers. Both are built from one multi-stage [Dockerfile](../Dockerfile), and a `PROJECT` build arg picks which (D-22). `docker compose up --build` runs everything except the UI, which runs with `npm run dev`.

### Layers
| Project | Holds | Depends on |
|---|---|---|
| `Crawler.Domain` | [`UrlNormalizer`](../src/Crawler.Domain/UrlNormalizer.cs), [`LinkAnalysis`](../src/Crawler.Domain/LinkAnalysis.cs) (ratio, same-host rule), [`HtmlLinkExtractor`](../src/Crawler.Domain/HtmlLinkExtractor.cs) (AngleSharp), [`PageTree`](../src/Crawler.Domain/PageTree.cs), statuses, limits | AngleSharp only. No I/O, EF or MassTransit. |
| `Crawler.Infrastructure` | EF context and migration, [`CrawlStore`](../src/Crawler.Infrastructure/Persistence/CrawlStore.cs) (the worker's SQL), [`PageFetcher`](../src/Crawler.Infrastructure/Crawling/PageFetcher.cs), the message contract [`CrawlJob`](../src/Crawler.Infrastructure/Messaging/CrawlJob.cs), bus setup, [`RetryPolicy`](../src/Crawler.Infrastructure/Messaging/RetryPolicy.cs), [`TransientErrors`](../src/Crawler.Infrastructure/Messaging/TransientErrors.cs), health checks | Domain |
| `Crawler.Api` | Minimal API endpoints, validation, startup migration | Infrastructure |
| `Crawler.Worker` | [`CrawlJobConsumer`](../src/Crawler.Worker/CrawlJobConsumer.cs), [`JobCrawler`](../src/Crawler.Worker/JobCrawler.cs) (the BFS loop), [`CrawlJobFaultConsumer`](../src/Crawler.Worker/CrawlJobFaultConsumer.cs) | Infrastructure |

### One job, end to end
1. **API** ([`JobEndpoints.CreateJob`](../src/Crawler.Api/JobEndpoints.cs)) validates and normalizes the URL. It inserts the job (`Pending`, `pages_enqueued = 1`) and its root page (depth 0, `Pending`) in one transaction. Then it publishes `CrawlJob { jobId }` with `CorrelationId = jobId`.
2. **Worker** `CrawlJobConsumer` opens a log scope (`correlationId`, `messageId`) and calls `JobCrawler.RunAsync`.
3. `CrawlStore.StartAsync` moves the job from `Pending` to `Running` with a conditional update. A job that is already terminal returns null, and the message is acknowledged as a duplicate.
4. **Loop:** `NextPendingPageAsync` takes the shallowest pending page. `PageFetcher.FetchAsync` fetches it. `HtmlLinkExtractor.Analyze` scores it. `SavePageAsync` then runs one transaction with the job row locked `FOR UPDATE`:
   - a conditional page update,
   - edges inserted,
   - new same-host children inserted up to the page cap,
   - counters bumped.
5. When no pending pages remain, `FinishAsync` reads the root page's status and marks the job `Completed` or `Failed`.

**The frontier is the database.** The set of pages still to crawl is the job's `Pending` rows, not messages in the broker. That single choice (D-13) makes redelivery resume where the last attempt stopped, and it is what keeps idempotency simple. [CRAWL_FRONTIER.md](CRAWL_FRONTIER.md) walks through it in detail.

### Data model
- `jobs`: settings, status, timestamps, failure reason, `pages_enqueued` / `pages_processed`. Index `(created_at DESC, id)` serves history paging.
- `pages`: one row per normalized URL per job, carrying depth, BFS parent, status, HTTP details, link counts and ratio. A **unique `(job_id, url_hash)`** key does the deduplication. `url_hash` is the SHA-256 of the normalized URL, which keeps the btree key small whatever the URL length.
- `edges`: every unique outgoing link of every fetched page, with primary key `(from_page_id, to_url_hash)`. Edges point at URLs, not page ids, because most targets never become pages: external links, and links past the depth or page cap.
- **Tree:** each page appears once, under the page that first discovered it. `GET /tree` reads pages in one query and edges in a second, then builds the tree in memory (`PageTree.Build`), so there is no N+1.

### Idempotency (at-least-once delivery)
| Write | Guard | What a duplicate does |
|---|---|---|
| Job start | `WHERE status IN (Pending, Running)`; `started_at` only set if null | A terminal job is ignored; a Running job resumes |
| Page result | `UPDATE … WHERE status = 'Pending'` | 0 rows, so the whole transaction rolls back (no edges, children or counters) |
| Child pages | `INSERT … WHERE NOT EXISTS … ON CONFLICT (job_id, url_hash) DO NOTHING` under the job-row lock | Never enqueued twice; the page cap stays exact |
| Edges | `ON CONFLICT DO NOTHING` on the primary key | No-op |
| Job finish | `WHERE status = 'Running'` | No-op |
| Fault | `WHERE status IN (Pending, Running)` | Can't overwrite a Completed job |

### Failure handling
| What goes wrong | What happens |
|---|---|
| Target site error, timeout or bad redirect | Retried inside the HTTP pipeline, then recorded as a `Failed` or `Skipped` page. **Never an exception** (D-9). |
| Transient database or broker-socket error | 5 in-process message retries, 1 s→30 s exponential, about 1 minute in total (D-24, D-28, D-29). The retry resumes from the database frontier. |
| Any other exception, or retries exhausted | The message moves to `crawl-job_error`. MassTransit publishes `Fault<CrawlJob>`, and `CrawlJobFaultConsumer` marks the job `Failed`. |
| Worker stopped mid-crawl | The consume is canceled, and the message goes back to `crawl-job`. The next worker resumes. |
| Crash between the API's commit and its publish | **Not handled:** the job stays `Pending` forever. This is the outbox gap; see §4. |

### Observability
- JSON console logs with scopes. Every API request under `/api/jobs/{id}` and every worker log line carries `correlationId` (the job id). Worker lines add `messageId`, and per-page lines add `pageId`.
- URLs are logged; page bodies never are.
- `/health` on both services checks PostgreSQL and the MassTransit bus.

## 3. Decisions and the reasoning behind them

Grouped by theme. D-numbers link to the full entry, which lists the options considered.

### 3.1 Messaging and the unit of work

**One message per job, with the frontier in the database (D-13, supersedes D-2).** This is the decision that shaped everything else.
- **Original plan (D-2):** one `CrawlPage` message per page. That allows parallel workers and per-page retries.
- **What it would have needed:** cross-message counters, a "last page finished" completion check that races, and fault handling per page.
- **Why it changed:** after slice 0 ran 33 minutes late, I switched before building the per-page path, saving about 30 minutes.
- **What was kept:** the per-page idempotent schema survived intact. One consumer now owns a whole job and pulls pages from the job's `Pending` rows.
- **Benefits:** redelivery is a resume rather than a restart, and completion is simply "no pending rows left".
- **Cost:** no parallelism within a job, and one slow site holds a consumer for the whole crawl.

**MassTransit 8 on RabbitMQ (D-1).** It provides retry, error queues, `Fault<T>` events and an EF outbox, so none of that needed hand-rolling on RabbitMQ.Client. It is pinned below 9 because MassTransit 9 has a commercial license.

**Only transient infrastructure errors are retried (D-9, D-24).**
- HTTP failures become page results, so HTTP retries (inside Polly) never multiply with message retries. Without this, D-9 estimated about 90 s of retrying per bad page.
- A bug or bad data fails the same way every time, so it goes straight to the error queue.

**A fault consumer is a must-have (D-7, amended by D-13).** Without it, a dead-lettered job stays `Running` forever. It cost about 10 minutes and closes the most visible failure mode.

**The transient check walks the whole exception chain (D-28), and socket errors count (D-29).** Both were found by the acceptance run, not designed up front:
- EF wraps transient Npgsql errors in `InvalidOperationException` outside `SaveChanges`, which D-24's filter missed.
- While the `db` container is down, its hostname doesn't resolve, and Npgsql throws that `SocketException` unwrapped.
- The retry window also grew from about 15 s to about 1 minute, because Postgres needs a few seconds after it restarts.
- I chose not to use EF's `EnableRetryOnFailure`, because it conflicts with `CrawlStore`'s explicit transactions.

### 3.2 Persistence

**The database enforces idempotency.** Unique keys plus conditional updates (the table in §2) mean a duplicate delivery is harmless by construction. Correctness doesn't depend on every code path remembering to check.

**Hand-written SQL where EF can't express it (D-20).** `FOR UPDATE`, `INSERT … SELECT FROM unnest(…) … ON CONFLICT`, and `LIMIT remaining` for the page cap. This is one statement per page for children and one for edges, which keeps the locked transaction short. Snake-case identifiers keep this SQL readable.

**Only the API migrates (D-16).** EF 8 takes no lock while migrating, so two processes starting together can race. A separate migration job would be cleaner but cost time. Compose starts the worker after the API, and the worker touches tables only once a message arrives, which only happens after the API has migrated.

**Testcontainers on real Postgres (D-6).** The worker depends on `ON CONFLICT`, `FOR UPDATE` and `unnest`. The EF in-memory provider and SQLite would test something else.

**Page counts computed on read (D-26).** `pageCounts` is one `GROUP BY status` per poll rather than counter columns. At 200 pages per job it is cheap, and it needed no migration.

### 3.3 Fetching

**Manual redirects (D-8).** With automatic redirects, an off-host page is fetched before any check can run. That breaks the same-host rule and widens SSRF exposure. The fetcher follows up to 5 hops itself, normalizing each `Location` and checking the host before requesting it.

**Resilience pipeline per host, body read outside it (D-20).**
- `SelectPipelineByAuthority` gives each host its own circuit breaker, so one dead site can't open the circuit for others.
- Only headers are read inside the pipeline. The body is then read under its own 10 s timeout with a 2 MB cap, so large non-HTML bodies aren't downloaded before the content type is checked.

**Polly logs at Warning (D-23).** At Information it logs one line per successful attempt, which buries the crawler's own lines.

### 3.4 Domain rules

**HTML parsing in Domain (D-15).** Parsing a string is pure computation. Putting it next to normalization keeps all link rules, including `<base href>`, under fast unit tests. AngleSharp handles malformed HTML that a regex wouldn't. I used version 1.8.5 because 1.1.2 has a security advisory.

**Normalization is deliberately conservative.**
- It lowercases scheme and host, drops the default port, user info and fragment, and turns an empty path into `/`.
- It keeps path case, trailing slashes and the query string. Each of those can name a different resource, so merging them risks losing pages rather than just crawling a duplicate.

**Exact-host matching.** It is the simplest defensible reading of "same domain". The cost: a site that redirects `example.com` → `www.example.com` fails at the start page, with a reason that names the target host (D-25).

### 3.5 API and UI

- **Bus health check instead of a separate RabbitMQ check (D-18).** It reports the connection the API actually publishes on, with one package fewer.
- **Publish not tied to the request token (D-18).** A client disconnect after commit must not leave a saved job unpublished.
- **Configuration read lazily (D-18).** `WebApplicationFactory` test settings apply after `Program` runs.
- **Test split (D-19).** Validation tests need no database, so they stay in the fast loop. Persistence and 404 tests run on Testcontainers.
- **Tree nodes include `error` and `isSameDomain` per link (D-19).** The UI can explain a failed page and mark links internal or external without a second call.
- **UI stack (D-21).**
  - The first install (Vite 5 / react-router 6 / Vitest 2) showed critical and high `npm audit` advisories, so I moved to patched versions.
  - Vitest 4.1.11 still has 2 moderate, dev-only advisories. The fix (4.1.12) was 8 days old, and I chose not to take a release that new.
  - The tree is refetched only when progress or status changes.
  - The UI test renders static markup, because jsdom and Testing Library aren't on the approved package list.
- **One Dockerfile, no curl in images (D-22).** Both services share the whole build. A compose healthcheck would have meant adding curl to the image.

### 3.6 Process and scope

- **Timebox management (D-5, D-10, D-12, D-14, D-27).** The plan was re-baselined openly when checkpoints slipped. Cuts followed a pre-agreed order and were logged, not made silently.
- **Order of work.** Must-have slices came first: domain rules → schema → API → worker → UI → Docker → retries and dead-lettering. The UI moved ahead of Docker and robustness (D-10) so the visible deliverable was safe first.
- **What was cut (D-27):**
  - The outbox, because the rubric accepts the gap if it is documented.
  - Cancel, which is optional in the spec.
  - SSRF guard, robots.txt and SSE.
  - CI.
- **What the saved time went to:** closing spec gaps (D-25, D-26), a clean-clone acceptance run, and the docs. The acceptance run found two real retry bugs (D-28, D-29), which I take as evidence it was the right use of the time.
- **Dev environment (D-17).** Windows Smart App Control started blocking freshly built DLLs mid-session. Build and test moved into the .NET 8 SDK container. Nothing in the repo depends on this.
- **Licensing.** FluentAssertions 8+ and MassTransit 9+ are excluded (D-1, D-4). `coverlet.collector` was removed because it wasn't on the approved list (D-11).

### 3.7 Defaults taken where the spec was silent
These came from PLAN.md's open-questions list and are restated in [README › Assumptions and trade-offs](../README.md#assumptions-and-trade-offs):

- Fetch the same host only, but count external links.
- Exact-host match.
- The ratio counting rules in §1.
- Depth 0 = start page, default 2, max 5.
- 200-page cap including the root.
- First-discovery parent in the tree.
- Non-HTML pages are Skipped with no ratio.
- A job fails only on a start-page or infrastructure failure.
- No auth.
- The UI is not in compose.

## 4. What's missing

Ranked by risk. Items 1–6 are what I'd do before calling this production-ready.

| # | Gap | Impact | Fix |
|---|---|---|---|
| 1 | **SSRF guard** | Anyone who can reach the API can make the worker request `localhost`, private ranges or cloud metadata endpoints. | Resolve the host and refuse loopback, private, link-local and metadata addresses on every hop. A `SocketsHttpHandler.ConnectCallback` that checks the resolved IP also covers DNS rebinding. |
| 2 | **Transactional outbox** | A crash between commit and publish leaves a `Pending` job with no message. | MassTransit's EF outbox on the API. The packages are pre-approved; it was cut slice 9. |
| 3 | **Watchdog and delayed redelivery** | A database outage longer than about 1 minute dead-letters the job. If the database is still down, the fault consumer fails too and the job stays `Running`. Nothing replays `crawl-job_error`. | A periodic sweep that requeues or fails jobs `Running` with no progress for N minutes, plus MassTransit delayed redelivery (broker-level) for long outages. |
| 4 | **Cancel** | Users can't stop a runaway crawl. | `POST /api/jobs/{id}/cancel` sets `Canceled`; `JobCrawler` re-reads status between pages; add a UI button. The status is already terminal everywhere. |
| 5 | **Automated tests for the messaging edges** | Consumer wiring, concurrent duplicate delivery and the dead-letter path were checked by hand only (D-12, D-24). | MassTransit test harness tests for both consumers, and a Testcontainers test running two `JobCrawler`s on one job at once. |
| 6 | **CI** | Nothing enforces the build and test gates. | A GitHub Actions workflow: build, fast tests, Docker tests, `npm run build`, `npm test`. Cut slice 11. |
| 7 | **robots.txt and politeness** | The crawler ignores `robots.txt` and `Crawl-delay`, and fetches back to back. | Fetch and cache `robots.txt` per host, and add a per-host minimum delay. |
| 8 | **Host matching options** | `example.com` and `www.example.com` are treated as different sites. | An option to treat `www.` (or the registrable domain) as the same site. |
| 9 | **Parallelism within a job** | A large site crawls serially. | A bounded worker pool over the database frontier, which needs page claiming (§5.1). |
| 10 | **Push progress** | The UI polls every 1.5 s. | Server-sent events from the API. |
| 11 | **Auth and rate limiting** | `POST /api/jobs` is open, so anyone can queue unlimited crawls. | Authentication plus the ASP.NET Core rate limiter on job creation. |
| 12 | **.NET 10** | .NET 8 support ends 2026-11-10. | Upgrade to .NET 10 LTS and move to the matching EF and Npgsql versions. |
| 13 | **Data retention** | Jobs, pages and edges grow without limit. | A retention job that deletes old jobs (foreign keys already cascade). |
| 14 | **UI in compose, coverage reporting** | Setup is slightly more manual, and coverage is unknown. | A static-site container for `web/`, and a licensed coverage tool. |

## 5. What could be better in the current code

These come from re-reading the code for this review. None of them breaks the documented behavior. They are what I'd raise if I were reviewing it myself. Severity: **High** = fix before scaling or exposing; **Medium** = fix soon; **Low** = polish.

### 5.1 Concurrent duplicate deliveries both fetch every page (Medium)
[`CrawlStore.NextPendingPageAsync`](../src/Crawler.Infrastructure/Persistence/CrawlStore.cs#L45) reads the next `Pending` page without claiming it.
- **Problem:** if two consumers ever run the same job (a redelivery after the 30-minute ack timeout while the first is still going, or a duplicate publish), both fetch every remaining page.
- **Why correctness holds:** `SavePageAsync`'s `WHERE status = 'Pending'` keeps results right, and the loser's write is discarded.
- **Cost:** twice the requests to the target site and wasted worker time.
- **Fix:** claim the page with `SELECT … FOR UPDATE SKIP LOCKED` plus a `Claimed` status and a lease timestamp. Pages whose lease has expired go back to the frontier. This is also the foundation for parallel fetching (§4 item 9).

### 5.2 Long jobs and the delivery-ack timeout (Medium)
One consumer holds the message for the whole crawl ([`JobCrawler.RunAsync`](../src/Crawler.Worker/JobCrawler.cs#L23)). A crawl longer than RabbitMQ's 30-minute delivery-acknowledgement timeout gets redelivered while still running, which leads straight to §5.1. At 200 pages this is unlikely, but it is the ceiling of the D-13 design.
- **Fix:** have the consumer process a bounded batch of pages, then publish a continuation `CrawlJob` and acknowledge. The database frontier already makes that safe.

### 5.3 Worker concurrency isn't set explicitly (Medium)
The `crawl-job` endpoint ([`CrawlJobConsumerDefinition`](../src/Crawler.Worker/CrawlJobConsumerDefinition.cs)) sets only retry. How many jobs one worker runs at once is left to MassTransit's prefetch default.
- **Why it matters:** each job is long-running and holds a DbContext and HTTP connections, so this number should be a deliberate setting.
- **Fix:** set `ConcurrentMessageLimit` (and prefetch) in configuration.

### 5.4 Fault reasons expose raw exception text (Medium)
[`CrawlJobFaultConsumer`](../src/Crawler.Worker/CrawlJobFaultConsumer.cs#L25) stores `"{ExceptionType}: {Message}"` as `failure_reason`. The API returns it, and the UI displays it. Exception messages can include host names, SQL fragments or other internal details.
- **Fix:** store a user-facing reason ("Crawl aborted by an internal error; reference {faultedMessageId}") and keep the exception detail in the logs, which already carry `faultedMessageId`.

### 5.5 No politeness between requests (Medium)
The BFS loop ([`JobCrawler.cs:23`](../src/Crawler.Worker/JobCrawler.cs#L23)) fetches the next page as soon as the previous one is saved. The crawl is serial, so it is only one request at a time, but there is no delay, no `Crawl-delay` and no backing off when the site sends 429s beyond the retry pipeline.
- **Fix:** add a per-host minimum interval (configurable), and honor `Retry-After` across pages, not only within one request.

### 5.6 No overall deadline per page (Low)
Each redirect hop gets the full 20 s resilience budget ([`PageFetcher.FetchAsync`](../src/Crawler.Infrastructure/Crawling/PageFetcher.cs#L24)), plus up to 10 s for the body. A 5-hop chain can take about 2 minutes (accepted in D-20).
- **Fix:** add a linked `CancellationTokenSource` with one deadline for the whole page.

### 5.7 Character set handling (Low)
[`PageFetcher.Decode`](../src/Crawler.Infrastructure/Crawling/PageFetcher.cs#L124) uses the `charset` from the `Content-Type` header.
- **Unknown encodings:** on .NET Core, `Encoding.GetEncoding` recognizes only a few encodings unless `CodePagesEncodingProvider` is registered, so `windows-1252` and similar fall back to UTF-8.
- **Meta tags ignored:** `<meta charset>` is never consulted.
- **Impact:** hrefs are almost always ASCII, so link extraction is rarely affected. But non-ASCII paths could be mangled.
- **Fix:** register `CodePagesEncodingProvider.Instance`, and let AngleSharp detect the encoding from the byte stream instead of decoding first.

### 5.8 Frontier query has no supporting index (Low)
`NextPendingPageAsync` filters `job_id, status` and orders by `depth, id`. The available indexes are `(job_id, url_hash)` and `(job_id, parent_page_id)`, so Postgres scans the job's pages and sorts them. That is trivial at 200 pages, but it is O(pages) per step, so O(pages²) per job.
- **Fix:** a partial index `(job_id, depth, id) WHERE status = 'Pending'` before raising the cap.

### 5.9 Any `SocketException` counts as transient (Low)
[`TransientErrors.cs:26`](../src/Crawler.Infrastructure/Messaging/TransientErrors.cs#L26). This is safe today, because the fetcher converts every HTTP-side socket error into a page result. But the safety relies on that convention rather than the type system. A future code path that lets one escape would be retried for about a minute.
- **Fix:** match only `HostNotFound`, `ConnectionRefused` and similar `SocketError` codes, or tag database exceptions at the `CrawlStore` boundary.

### 5.10 API: history paging and a stale-connection 500 (Low)
- **Paging:** [`ListJobs`](../src/Crawler.Api/JobEndpoints.cs#L72) runs `COUNT(*)` and `OFFSET` on every request. That is fine for a demo, but keyset paging on the existing `(created_at DESC, id)` index scales better.
- **Stale connection:** after a database restart, the first API query on a stale pooled connection returns 500 (already in README limitations). A small retry around read endpoints, or EF's retrying execution strategy on the API side only (the API has no explicit transactions besides job creation), would hide it.

### 5.11 UI: every poll triggers a CORS preflight (Low)
[`api.ts:27`](../web/src/api.ts#L27) sends `Content-Type: application/json` on every request, including GETs. That makes each GET a non-simple cross-origin request, so the browser sends an `OPTIONS` preflight before every 1.5 s poll.
- **Fix:** set the header only when there is a body, or add `SetPreflightMaxAge` to the CORS policy.
- **Related:** the whole tree is refetched whenever `pagesProcessed` changes, which during a running crawl is nearly every tick. An incremental endpoint (pages since id N) or SSE would cut that.

### 5.12 Schema coupling between services (Low)
The API and worker share one database and one EF model. Only the API migrates, but that is enforced by convention (D-16), not by permissions. It is acceptable at this size and stated in the README.
- **For a larger system:** separate database roles (the worker gets DML only), or a dedicated migration job in compose.

### 5.13 Testing depth (Low)
- `JobCrawler` is tested directly through DI, not through MassTransit (D-20). The consumer wiring, retry filter registration and fault routing have no automated test.
- Concurrent duplicate delivery has no test.
- The UI test checks static markup, not interaction or polling behavior. `useJobPolling` is untested.
- There is no coverage measurement.

### 5.14 Smaller notes
- `<area href>`, `rel="nofollow"` and `<link rel="canonical">` are ignored. These are deliberate scope choices, but worth stating.
- Normalization doesn't collapse `/index.html` to `/`, sort query parameters, or decode unreserved percent-escapes. This is conservative on purpose (§3.4), but it means some duplicate crawling.
- Two URLs that redirect to the same page are both crawled and stored (known limitation, D-8). Deduplicating on `final_url` would fix it.

## 6. Suggested 10-minute walkthrough of the code

1. [`CrawlJob.cs`](../src/Crawler.Infrastructure/Messaging/CrawlJob.cs): the whole message contract is one field.
2. [`JobEndpoints.CreateJob`](../src/Crawler.Api/JobEndpoints.cs): insert, then publish, and where the outbox gap sits.
3. [`JobCrawler.RunAsync`](../src/Crawler.Worker/JobCrawler.cs): the BFS loop over the database frontier.
4. [`CrawlStore.SavePageAsync`](../src/Crawler.Infrastructure/Persistence/CrawlStore.cs): the idempotent per-page transaction. This is the core of the design.
5. [`PageFetcher.FetchAsync`](../src/Crawler.Infrastructure/Crawling/PageFetcher.cs): manual redirects and failures as data.
6. [`LinkAnalysis`](../src/Crawler.Domain/LinkAnalysis.cs) and [`UrlNormalizer`](../src/Crawler.Domain/UrlNormalizer.cs): the ratio and its rules.
7. [`RetryPolicy`](../src/Crawler.Infrastructure/Messaging/RetryPolicy.cs), [`TransientErrors`](../src/Crawler.Infrastructure/Messaging/TransientErrors.cs) and [`CrawlJobFaultConsumer`](../src/Crawler.Worker/CrawlJobFaultConsumer.cs): retry and dead-letter.
8. Tests: [`JobCrawlerTests`](../tests/Crawler.IntegrationTests/JobCrawlerTests.cs) (fixture site on real Postgres), [`PageFetcherTests`](../tests/Crawler.IntegrationTests/PageFetcherTests.cs), [`UrlNormalizerTests`](../tests/Crawler.UnitTests/UrlNormalizerTests.cs) and [`HtmlLinkExtractorTests`](../tests/Crawler.UnitTests/HtmlLinkExtractorTests.cs).
