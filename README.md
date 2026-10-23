# Job-based web crawler

> Work in progress. Summary, architecture, message schema, idempotency, retry/DLQ, limitations and trade-offs are added as the slices land (see PLAN.md).

## Current scope
.NET 8 solution (Domain, Infrastructure, Api, Worker, unit and integration test projects) and a compose file for PostgreSQL and RabbitMQ. Domain: URL normalization, link extraction from HTML, same-host classification and the domain link ratio (same-domain links / all unique http(s) links; 0 when a page has none). Persistence: EF Core schema for `jobs`, `pages` and `edges` with an initial migration, applied by the API at startup. API: create, get, tree and history endpoints, publishing one `CrawlJob` message per job to RabbitMQ, plus `/health`. Worker: a `CrawlJob` consumer that crawls the job breadth-first (same host only, within `maxDepth` and `maxPages`), stores each page's status, link counts, ratio and outgoing links, then marks the job Completed or Failed; plus `/health`. Not yet: message retry and fault handling (slice 5), Docker images for the services (slice 7), UI (slice 8).

## Worker
- One consumer per job (endpoint `crawl-job`). The frontier is the job's `Pending` pages in the database, taken shallowest first in discovery order, so a redelivered message resumes where the last attempt stopped. A message for a job that is already Completed, Failed or Canceled is acknowledged and ignored.
- Each page is fetched, then its outcome, outgoing links (`edges`) and new same-host child pages are written in one transaction with the job row locked (`FOR UPDATE`). The page update is conditional on `status = 'Pending'`, so a concurrent duplicate delivery can't count a page twice. Children are enqueued only while `depth + 1 ≤ maxDepth` and the job is under `maxPages`.
- Page statuses: `Fetched` (HTML, links parsed), `Skipped` (not HTML, body over 2 MB, or a redirect off the start host), `Failed` (HTTP error after retries, network error, timeout, redirect loop or more than 5 redirects). The job is `Failed` only if the start page failed; otherwise `Completed`.
- Fetching: descriptive `User-Agent`, no automatic redirects (each hop is checked against the start host before it is requested), and the standard resilience handler per host: 10 s per attempt, 20 s total, 3 retries with exponential backoff (from 500 ms) and jitter on 5xx, 408, 429, network errors and timeouts. 4xx and non-HTML responses aren't retried. The body is read under a separate 10 s timeout. HTTP failures become page results, never exceptions, so they don't trigger message retries (D-9).

## API
All errors are ProblemDetails (`application/problem+json`); validation errors are keyed by field name. Enums are serialized as strings.

| Method and path | Result |
|---|---|
| `POST /api/jobs` `{"url": "https://example.com", "maxDepth": 2}` | `202` `{"jobId"}` with `Location: /api/jobs/{id}`. `url` must be an absolute http(s) URL (max 2048 chars); `maxDepth` is optional, 0–5, default 2. Inserts the job and its root page (depth 0, `Pending`) in one transaction, then publishes `CrawlJob`. |
| `GET /api/jobs/{id}` | Status, timestamps, failure reason and progress (`pagesProcessed` / `pagesEnqueued`, `maxPages`); `404` if unknown. |
| `GET /api/jobs/{id}/tree` | `{jobId, status, root}`; each node has `url, depth, status, domainLinkRatio, error, outgoingLinks[{url, isSameDomain}], children[]`. Children are in discovery order; links sorted by URL. |
| `GET /api/jobs?page=1&pageSize=20` | `{items, page, pageSize, total}`, most recent first; `pageSize` 1–100. |
| `GET /health` | `200 Healthy` when PostgreSQL and the RabbitMQ bus are reachable, otherwise `503`. |

## Message schema
```json
CrawlJob { "jobId": "guid" }
```
Published to the `Crawler.Infrastructure.Messaging:CrawlJob` exchange with the MassTransit envelope's `correlationId` set to the job id. Delivery is at-least-once.

## Logging
JSON console logs with scopes. Every request under `/api/jobs/{id}`, and job creation, logs inside a scope carrying `correlationId` = job id. In the worker, every log line for a message carries `correlationId` (job id) and `messageId`, and per-page lines add `pageId`. Page URLs are logged; page bodies never are.

## Prerequisites
- .NET 8 SDK (pinned by `global.json`)
- Docker Desktop

## Run
Start the infrastructure, then the API and the worker:
```
docker compose up -d db rabbitmq
dotnet run --project src/Crawler.Api
dotnet run --project src/Crawler.Worker
```
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

No test touches the network. `StubSite` is an `HttpMessageHandler` standing in for a website. Fetcher tests (fast) cover redirects, retries and content types through the real resilience pipeline. Crawl tests (`Category=Docker`) run `JobCrawler` over the local fixture site in `tests/Crawler.IntegrationTests/Fixtures/site` on real Postgres. They check BFS order, the depth limit, dedupe, a cycle, max pages, ratios and stored links, a failed start page, and that a second delivery of a completed job changes nothing.

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

`appsettings.Development.json` points at the compose database and broker with the dev credentials above.
