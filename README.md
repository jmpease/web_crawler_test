# Job-based web crawler

> Work in progress. Summary, architecture, message schema, idempotency, retry/DLQ, limitations and trade-offs are added as the slices land (see PLAN.md).

## Current scope
.NET 8 solution (Domain, Infrastructure, Api, Worker, unit and integration test projects) and a compose file for PostgreSQL and RabbitMQ. Domain: URL normalization, link extraction from HTML, same-host classification and the domain link ratio (same-domain links / all unique http(s) links; 0 when a page has none). Persistence: EF Core schema for `jobs`, `pages` and `edges` with an initial migration, applied by the API at startup. API: create, get, tree and history endpoints, publishing one `CrawlJob` message per job to RabbitMQ, plus `/health`. No worker consumer yet, so jobs stay `Pending`.

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
JSON console logs with scopes. Every request under `/api/jobs/{id}`, and job creation, logs inside a scope carrying `correlationId` = job id. Page bodies are never logged.

## Prerequisites
- .NET 8 SDK (pinned by `global.json`)
- Docker Desktop

## Run
Start the infrastructure, then the API:
```
docker compose up -d db rabbitmq
dotnet run --project src/Crawler.Api
```
PostgreSQL listens on `localhost:5432`, RabbitMQ on `localhost:5672` (management UI on `http://localhost:15672`). The API listens on `http://localhost:5129`.

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
| `RabbitMq__Host`, `__Port`, `__VirtualHost`, `__Username`, `__Password` | `localhost`, `5672`, `/`, `guest`, `guest` (Development: compose credentials) | API |
| `Jobs__MaxPages` | `200` | API (stored on each job) |
| `Cors__AllowedOrigins__0` | `http://localhost:5173` (Vite dev server) | API |
| `Database__MigrateOnStartup` | `true` | API |

`appsettings.Development.json` points at the compose database and broker with the dev credentials above.
