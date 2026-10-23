# Job-based web crawler

> Work in progress. Summary, architecture, message schema, idempotency, retry/DLQ, limitations and trade-offs are added as the slices land (see PLAN.md).

## Current scope
.NET 8 solution (Domain, Infrastructure, Api, Worker, unit and integration test projects) and a compose file that runs PostgreSQL, RabbitMQ, the API and the worker. Domain: URL normalization, link extraction from HTML, same-host classification and the domain link ratio (same-domain links / all unique http(s) links; 0 when a page has none). Persistence: EF Core schema for `jobs`, `pages` and `edges` with an initial migration, applied by the API at startup. API: create, get, tree and history endpoints, publishing one `CrawlJob` message per job to RabbitMQ, plus `/health`. Worker: a `CrawlJob` consumer that crawls the job breadth-first (same host only, within `maxDepth` and `maxPages`), stores each page's status, link counts, ratio and outgoing links, then marks the job Completed or Failed; plus `/health`. UI (`web/`): start a crawl, a job page that polls until the job finishes and shows progress and the page tree with each page's ratio and links, and a paged history. Messaging: transient database errors are retried, anything else dead-letters the message and marks its job Failed.

## Worker
- One consumer per job (endpoint `crawl-job`). The frontier is the job's `Pending` pages in the database, taken shallowest first in discovery order, so a redelivered message resumes where the last attempt stopped. A message for a job that is already Completed, Failed or Canceled is acknowledged and ignored.
- Each page is fetched, then its outcome, outgoing links (`edges`) and new same-host child pages are written in one transaction with the job row locked (`FOR UPDATE`). The page update is conditional on `status = 'Pending'`, so a concurrent duplicate delivery can't count a page twice. Children are enqueued only while `depth + 1 ≤ maxDepth` and the job is under `maxPages`.
- Page statuses: `Fetched` (HTML, links parsed), `Skipped` (not HTML, body over 2 MB, or a redirect off the start host), `Failed` (HTTP error after retries, network error, timeout, redirect loop or more than 5 redirects). The job is `Failed` if the start page wasn't Fetched (it failed, wasn't HTML, or redirected off-host), with the reason "Start page failed: …" or "Start page skipped: …"; otherwise `Completed`, with per-page statuses in the tree.
- Fetching: descriptive `User-Agent`, no automatic redirects (each hop is checked against the start host before it is requested), and the standard resilience handler per host: 10 s per attempt, 20 s total, 3 retries with exponential backoff (from 500 ms) and jitter on 5xx, 408, 429, network errors and timeouts. 4xx and non-HTML responses aren't retried. The body is read under a separate 10 s timeout. HTTP failures become page results, never exceptions, so they don't trigger message retries (D-9).

## Retries and dead-lettering
- **What counts as transient:** only database errors Npgsql marks transient (lost connection, timeout, serialization failure, too many connections, and similar), raised directly or wrapped in EF's `DbUpdateException`. HTTP problems are not exceptions here; they become page results (see above).
- **Message retry:** on both worker endpoints, 3 in-process retries with exponential backoff from 1 s to 10 s. A retry reruns the consumer, which resumes the job from its Pending pages, so pages already saved are not fetched again.
- **Dead letter queue:** a non-transient exception, or a fourth transient failure, moves the message to `crawl-job_error` and MassTransit publishes `Fault<CrawlJob>`. The fault consumer (endpoint `crawl-job-fault`) marks the job Failed with `failure_reason` = "Crawl aborted by an unrecoverable worker error: <type>: <message>". It only touches a Pending or Running job, so a late or duplicate fault can't overwrite a Completed one. Messages in `crawl-job_error` stay there for inspection; nothing replays them automatically. Move one back to `crawl-job` from the RabbitMQ management UI (http://localhost:15672) to retry the job; it resumes where it stopped.
- **Worker shutdown:** stopping the worker mid-crawl cancels the consumer, and MassTransit returns the message to `crawl-job` rather than dead-lettering it. The next worker resumes the job.

## API
All errors are ProblemDetails (`application/problem+json`); validation errors are keyed by field name. Enums are serialized as strings.

| Method and path | Result |
|---|---|
| `POST /api/jobs` `{"url": "https://example.com", "maxDepth": 2}` | `202` `{"jobId"}` with `Location: /api/jobs/{id}`. `url` must be an absolute http(s) URL (max 2048 chars); `maxDepth` is optional, 0–5, default 2. Inserts the job and its root page (depth 0, `Pending`) in one transaction, then publishes `CrawlJob`. |
| `GET /api/jobs/{id}` | Status, timestamps, failure reason and progress (`pagesProcessed` / `pagesEnqueued`, `maxPages`); `404` if unknown. |
| `GET /api/jobs/{id}/tree` | `{jobId, status, root}`; each node has `url, depth, status, domainLinkRatio, error, outgoingLinks[{url, isSameDomain}], children[]`. Children are in discovery order; links sorted by URL. |
| `GET /api/jobs?page=1&pageSize=20` | `{items, page, pageSize, total}`, most recent first; `pageSize` 1–100. |
| `GET /health` | `200 Healthy` when PostgreSQL and the RabbitMQ bus are reachable, otherwise `503`. |

## UI
Vite + React + TypeScript in `web/`, no UI framework. Routes: `/` starts a crawl (URL and max depth; validation errors from the API appear under each field), `/jobs/{id}` shows the job, `/history?page=N` lists jobs 20 per page, most recent first; a row opens its job.

The job page polls `GET /api/jobs/{id}` every 1.5 s until the job is Completed, Failed or Canceled, and refetches the tree only when the processed count or status changes. It shows the job ID, URL, status, progress ("processed / enqueued"), any failure reason and the created, started and completed times. Each tree node shows its status, URL, ratio (a dash when the page wasn't scored), depth, any error, and a collapsible list of outgoing links marked internal or external. A failed poll is shown and retried on the next tick; an unknown job id stops polling with a not-found message.

## Message schema
```json
CrawlJob { "jobId": "guid" }
```
Published to the `Crawler.Infrastructure.Messaging:CrawlJob` exchange with the MassTransit envelope's `correlationId` set to the job id. Delivery is at-least-once.

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
