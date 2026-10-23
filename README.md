# Job-based web crawler

> Work in progress. Summary, architecture, message schema, idempotency, retry/DLQ, limitations and trade-offs are added as the slices land (see PLAN.md).

## Current scope
Scaffold only: .NET 8 solution (Domain, Infrastructure, Api, Worker, unit and integration test projects) and a compose file for PostgreSQL and RabbitMQ.

## Prerequisites
- .NET 8 SDK (pinned by `global.json`)
- Docker Desktop

## Run
Start the infrastructure:
```
docker compose up -d db rabbitmq
```
PostgreSQL listens on `localhost:5432`, RabbitMQ on `localhost:5672` (management UI on `http://localhost:15672`).

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
