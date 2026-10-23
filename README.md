# Job-based web crawler

> Work in progress. Summary, architecture, message schema, idempotency, retry/DLQ, limitations and trade-offs are added as the slices land (see PLAN.md).

## Current scope
.NET 8 solution (Domain, Infrastructure, Api, Worker, unit and integration test projects) and a compose file for PostgreSQL and RabbitMQ. Domain: URL normalization, link extraction from HTML, same-host classification and the domain link ratio (same-domain links / all unique http(s) links; 0 when a page has none). Persistence: EF Core schema for `jobs`, `pages` and `edges` with an initial migration, applied by the API at startup. No API or worker endpoints yet.

## Prerequisites
- .NET 8 SDK (pinned by `global.json`)
- Docker Desktop

## Run
Start the infrastructure:
```
docker compose up -d db rabbitmq
```
PostgreSQL listens on `localhost:5432`, RabbitMQ on `localhost:5672` (management UI on `http://localhost:15672`).

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
All tests, including Testcontainers-based ones (needs Docker running and the `postgres:16-alpine` image):
```
dotnet test
```

## Configuration
Compose reads dev-only credentials from environment variables, with defaults:

| Variable | Default |
|---|---|
| `POSTGRES_DB` | `crawler` |
| `POSTGRES_USER` | `crawler` |
| `POSTGRES_PASSWORD` | `crawler_dev` |
| `RABBITMQ_USER` | `crawler` |
| `RABBITMQ_PASSWORD` | `crawler_dev` |

API and worker read the database from `ConnectionStrings:Crawler` (env: `ConnectionStrings__Crawler`). `appsettings.Development.json` points at the compose database with the dev credentials above.
