# Developer guide: debugging the UI, API and worker

> Written on 2026-10-26, after the assessment window closed. It adds documentation and debug-only tooling (`Dockerfile.debug`, `docker-compose.debug.yml`, `.vscode/`). It changes no application code or tests (D-30).

This guide is for working on the crawler: running it, stepping through each component in a debugger, and finding out why a crawl did what it did. For what the system does and why, see the [README](../README.md) and the [reviewer guide](REVIEWER_GUIDE.md).

Commands were run against the debug stack on 2026-10-26 unless marked *untested*. Steps that happen inside an IDE window (VS Code and Visual Studio attach) are written from the tools' documented behavior, and are marked as such.

## Contents
1. [The moving parts](#1-the-moving-parts)
2. [Shared toolbox: health, logs, broker, database](#2-shared-toolbox)
3. [Frontend (React UI)](#3-frontend)
4. [API](#4-api)
5. [Worker](#5-worker)
6. [Diagnostics inside containers](#6-diagnostics-inside-containers)
7. [Symptom → where to look](#7-symptom--where-to-look)
8. [Appendix: tests and environment gotchas](#8-appendix)

## 1. The moving parts

| Service | Host port | Container port | Health / UI | Logs |
|---|---|---|---|---|
| UI (Vite dev server, not in compose) | 5173 | — | http://localhost:5173 | the `npm run dev` terminal and the browser console |
| API | 5129 | 8080 | http://localhost:5129/health | `docker compose logs api` |
| Worker | 5250 | 8080 | http://localhost:5250/health | `docker compose logs worker` |
| PostgreSQL 16 | 5432 | 5432 | `pg_isready` (compose healthcheck) | `docker compose logs db` |
| RabbitMQ 3.13 | 5672, 15672 | same | http://localhost:15672 (user `crawler` / `crawler_dev`) | `docker compose logs rabbitmq` |

### Three ways to run

| Mode | Command | Use it for |
|---|---|---|
| **A. Plain compose** | `docker compose up --build` | Running the system as submitted. Release builds with no debugger. |
| **B. Debug stack** (recommended for stepping) | `docker compose -f docker-compose.yml -f docker-compose.debug.yml up --build -d` | Attaching VS Code or Visual Studio to the API and worker in their containers. Turns on SQL, MassTransit and Polly logging, and adds the `dotnet-counters`, `dotnet-trace` and `dotnet-dump` tools. |
| **C. Infra in Docker, services from source** | `docker compose up -d db rabbitmq`, then F5 (`Local: launch API` / `Local: launch worker`) or `dotnet run --project src/Crawler.Api` and `dotnet run --project src/Crawler.Worker` | The fastest edit-and-debug loop, *if your machine can run locally built DLLs* (see §8). |

**What the debug stack changes** (`docker-compose.debug.yml` → `Dockerfile.debug`):
- A Debug build: no optimization, PDBs next to the DLLs, so locals and stepping work.
- `procps` (`ps`, `top`), plus `curl` and `unzip` so VS Code can install `vsdbg` at `/vsdbg/vsdbg` when it attaches (§4.1, D-31).
- `dotnet-counters`, `dotnet-trace` and `dotnet-dump` in `/tools`, on `PATH`.
- `cap_add: SYS_PTRACE`.
- Its own image tags, `crawler-api:debug` and `crawler-worker:debug`, so it never overwrites the normal images. Switching back to mode A with `docker compose up --build` rebuilds the normal images.
- More logging:

  | Category | Level | Shows |
  |---|---|---|
  | `Microsoft.EntityFrameworkCore.Database.Command` | Information | every SQL statement with its timing |
  | `MassTransit` | Debug | receive, consume and fault events per message |
  | `Polly` (worker only) | Information | every HTTP attempt, including successes |

Ports, credentials and the database volume are the same in every mode. Switching modes keeps your data. To start clean, run `docker compose down -v`.

The debug image's first build downloads the three tools from `aka.ms`, so it needs internet access. Later builds use the cache. The first VS Code attach to each new container downloads vsdbg (about 60 MB).

## 2. Shared toolbox

### 2.1 Health
```
curl http://localhost:5129/health      # API: Healthy / Unhealthy
curl http://localhost:5250/health      # worker
```
Each returns `200 Healthy` when PostgreSQL and the RabbitMQ bus are both reachable, and `503 Unhealthy` otherwise. The body doesn't say which check failed. To find out, look at the service's log, or check the two dependencies directly (§2.3, §2.4).

### 2.2 Logs
Both services write one JSON object per line, with scopes included. These fields matter:

| Field | Where it appears |
|---|---|
| `Category` | Every line. For example `Crawler.Worker.JobCrawler`, `Crawler.Api.Jobs`, `MassTransit.ReceiveTransport`, `Polly`. |
| `Scopes[].correlationId` | The job id, on every API line under `/api/jobs/{id}`, on job creation, and on every worker line for a message. |
| `Scopes[].messageId` | Worker: the RabbitMQ message being consumed. |
| `Scopes[].pageId` | Worker: per-page lines (`Crawled … at depth …`). |
| `Scopes[].faultedMessageId` | Fault consumer: the message that was dead-lettered. |

Follow live output:
```
docker compose logs -f api worker
```

Filter one job in **PowerShell** (tested):
```powershell
$job = '<job id>'
docker compose logs worker --no-log-prefix |
  Where-Object { $_.StartsWith('{') } |
  ForEach-Object { $_ | ConvertFrom-Json } |
  Where-Object { $_.Scopes.correlationId -contains $job } |
  Select-Object LogLevel, Category, Message
```

The same in **bash**, with `jq` (*untested here: jq isn't installed on this machine*):
```bash
docker compose logs worker --no-log-prefix | grep '^{' |
  jq -c --arg j "<job id>" 'select(any(.Scopes[]?; .correlationId == $j)) | {LogLevel, Category, Message}'
```

Without jq, `grep "<job id>"` gets you most of the way, because the id appears in the scope of every relevant line.

**Logs live only as long as the container.** Recreating a container with `up --build` or `down` discards its log. Save anything you need first, for example `docker compose logs worker > worker.log`.

Useful MassTransit markers (visible at Debug level in the debug stack; faults and retries show in any mode):

| Marker | Meaning |
|---|---|
| `RECEIVE` / `R-OK` | A message was received and consumed successfully |
| `R-RETRY` | A transient error triggered an in-process retry (D-24, D-29) |
| `R-FAULT` | The consume failed for good; the message went to `<queue>_error` |

### 2.3 RabbitMQ
**Management UI:** http://localhost:15672 (`crawler` / `crawler_dev`).

| Queue | Contents |
|---|---|
| `crawl-job` | `CrawlJob` messages waiting for a worker. A crawl in progress shows 1 unacked message. |
| `crawl-job_error` | Dead-lettered `CrawlJob` messages: a non-transient error, a 6th transient failure, or a message that couldn't be deserialized. Nothing reads this queue. |
| `crawl-job-fault` | `Fault<CrawlJob>` events, consumed by the fault consumer, which marks the job Failed. |
| `crawl-job-fault_error` | Faults the fault consumer itself couldn't process (for example, the database still down). |

From the CLI:
```
docker compose exec rabbitmq rabbitmqctl list_queues name messages consumers
```

**Peek at a message:** in the UI, open Queues → `crawl-job_error` → *Get messages*, and choose *Ack mode: Nack message requeue true* so the message stays in the queue. The headers include MassTransit's `MT-Fault-*` entries, which carry the exception type, message and stack trace.

**Publish a message by hand:**
1. In the UI, open Exchanges → `Crawler.Infrastructure.Messaging:CrawlJob` → *Publish message*.
2. Set the property `content_type` = `application/vnd.masstransit+json`.
3. Use this payload:
   ```json
   {"messageType":["urn:message:Crawler.Infrastructure.Messaging:CrawlJob"],"message":{"jobId":"<job id>"}}
   ```

The same over the HTTP API (tested):
```bash
curl -u crawler:crawler_dev -X POST \
  "http://localhost:15672/api/exchanges/%2F/Crawler.Infrastructure.Messaging%3ACrawlJob/publish" \
  -H 'content-type: application/json' \
  -d '{"properties":{"content_type":"application/vnd.masstransit+json"},"routing_key":"","payload":"{\"messageType\":[\"urn:message:Crawler.Infrastructure.Messaging:CrawlJob\"],\"message\":{\"jobId\":\"<job id>\"}}","payload_encoding":"string"}'
```

**Move dead-lettered messages back to `crawl-job`.** The UI's *Move messages* panel comes from the shovel plugins, which the `rabbitmq:3.13-management` image doesn't enable. Turn them on once per broker container:
```
docker compose exec rabbitmq rabbitmq-plugins enable rabbitmq_shovel rabbitmq_shovel_management
```
Then use Queues → `crawl-job_error` → *Move messages* → destination `crawl-job`. The equivalent API call (tested) creates a one-off shovel that deletes itself once the queue is drained:
```bash
curl -u crawler:crawler_dev -X PUT http://localhost:15672/api/parameters/shovel/%2F/requeue-errors \
  -H 'content-type: application/json' \
  -d '{"value":{"src-uri":"amqp://","src-queue":"crawl-job_error","dest-uri":"amqp://","dest-queue":"crawl-job","src-delete-after":"queue-length","ack-mode":"on-confirm"}}'
```
A moved `CrawlJob` resumes its job from the Pending pages in the database. A job the fault consumer has already marked Failed is not reopened: the worker treats it as finished. To re-run such a job, set it back to `Running` in the database first (§2.4), then move the message.

### 2.4 PostgreSQL
```
docker compose exec db psql -U crawler crawler
```

| Question | Query |
|---|---|
| Recent jobs | `SELECT id, status, pages_processed, pages_enqueued, failure_reason, created_at FROM jobs ORDER BY created_at DESC LIMIT 10;` |
| Pages by outcome | `SELECT status, count(*) FROM pages WHERE job_id = '<id>' GROUP BY status;` |
| The frontier (what the worker crawls next) | `SELECT id, depth, url FROM pages WHERE job_id = '<id>' AND status = 'Pending' ORDER BY depth, id LIMIT 20;` |
| Why pages failed or were skipped | `SELECT status, error, count(*) FROM pages WHERE job_id = '<id>' AND status <> 'Fetched' GROUP BY status, error;` |
| The start page | `SELECT id, status, http_status, domain_link_ratio, error, final_url FROM pages WHERE job_id = '<id>' AND parent_page_id IS NULL;` |
| One page's links | `SELECT to_url, is_same_domain FROM edges WHERE from_page_id = <page id> ORDER BY to_url;` |
| Jobs that look stuck | `SELECT id, status, started_at, pages_processed, pages_enqueued FROM jobs WHERE status IN ('Pending','Running') AND created_at < now() - interval '5 minutes';` |
| Re-open a dead-lettered job before moving its message back | `UPDATE jobs SET status = 'Running', failure_reason = NULL, completed_at = NULL WHERE id = '<id>' AND status = 'Failed';` |

From PowerShell or bash without an interactive session: `docker compose exec -T db psql -U crawler crawler -c "<query>"`.

`pages_processed` / `pages_enqueued` is the progress the UI shows. A job is done when no Pending pages remain, not when those two counters match. Why the Pending pages are the work queue: [CRAWL_FRONTIER.md](CRAWL_FRONTIER.md).

## 3. Frontend

### 3.1 Run it
```
cd web
npm ci
npm run dev          # http://localhost:5173, API at http://localhost:5129
```
Requires Node 20.19+ or 22.12+. If Node isn't installed, run it in a container instead, with `node_modules` kept in a Docker volume (from the repo root, in Git Bash):
```bash
MSYS_NO_PATHCONV=1 docker run --rm -it -p 5173:5173 -v "$(pwd -W)/web:/web" \
  -v crawler-web-node-modules:/web/node_modules -w /web node:20-alpine sh -c "npm ci && npm run dev"
```
`vite.config.ts` sets `host: true`, so the dev server is reachable from the host. Breakpoints still work in the browser, because source maps are served either way.

To point the UI at a different API, set `VITE_API_URL` (for example `VITE_API_URL=http://localhost:5000 npm run dev`) and add that UI origin to the API's `Cors__AllowedOrigins__0`.

### 3.2 Browser DevTools (F12)
- **Network tab:** filter on `api/jobs`. A job page sends `GET /api/jobs/{id}` every 1.5 s, plus `GET /api/jobs/{id}/tree` only when `pagesProcessed` or `status` changed. Polling stops when the status is terminal, or on a 404.
  - Every GET is preceded by an `OPTIONS` preflight, because `api.ts` sends `Content-Type: application/json` on all requests (reviewer guide §5.11). Expect pairs.
  - Error responses are ProblemDetails. The *Response* tab shows `title`, `detail` and, for 400s, `errors` keyed by field. That `errors` object is what appears under the form fields.
- **Console:** CORS failures show up here as "blocked by CORS policy". In the UI they appear as "Cannot reach the API at …", because `fetch` rejects without a response.
- **Sources tab:** Vite serves source maps in dev, so `src/useJobPolling.ts`, `src/api.ts` and the `.tsx` files appear under `localhost:5173/src/`, where you can set breakpoints. Good places to break:

| Where | Why |
|---|---|
| `web/src/api.ts:22` `request()` | Every API call: the URL, the status, and how an error is converted to `ApiError` |
| `web/src/useJobPolling.ts:29` `poll()` | Each polling tick |
| `web/src/useJobPolling.ts:32` `treeIsStale` | Why the tree was or wasn't refetched |
| `web/src/pages/StartPage.tsx` submit handler | Validation errors mapped to fields |

- **React DevTools** (browser extension): inspect `JobPage`'s state (`job`, `tree`, `error`) and props for `PageTree` nodes.

### 3.3 Debug from VS Code
Start `npm run dev` first. Then run **UI: Edge** or **UI: Chrome** from the Run and Debug view. VS Code opens a browser it controls. Breakpoints set in the `.tsx`/`.ts` files in the editor bind through the source maps (`webRoot` = `web/`). *IDE steps untested here.*

**UI: debug Vitest** runs `vitest run --no-file-parallelism` under the Node debugger, so breakpoints in `PageTree.test.tsx` and in the components it renders are hit. It needs a local `web/node_modules`; run `npm ci` in `web/` first. The container approach above keeps `node_modules` in a volume, so it doesn't count.

### 3.4 Visual Studio
Visual Studio doesn't run the Vite app itself. Use the browser DevTools, or open the folder in VS Code for the configurations above.

### 3.5 Production bundle
```
npm run build      # tsc type-check + vite build into web/dist
npm run preview    # serve dist on http://localhost:4173
```
`npm run preview` serves on port 4173, which isn't in the API's default CORS list. To use it, run the API with `Cors__AllowedOrigins__1=http://localhost:4173` (in mode B, add it to the `api` environment).

### 3.6 Common UI problems

| Symptom | Likely cause | Check |
|---|---|---|
| "Cannot reach the API at http://localhost:5129" | API not running, or blocked by CORS | `curl localhost:5129/health`; look for a CORS error in the console; make sure the UI's origin is in `Cors__AllowedOrigins` |
| The form shows field errors | API validation (400) | Network → Response → `errors` |
| "Job not found", and polling stops | Wrong id, or the database was reset with `down -v` | `SELECT … FROM jobs WHERE id = …` |
| Progress stays at 0 / 1, status `Pending` | No worker consumed the message | §5.6 *Job stuck Pending* |
| The tree doesn't update | Tree requests are skipped while `pagesProcessed` doesn't change | Network tab: is `/tree` being requested? |
| A poll error flashes, then recovers | A transient API error (for example the first query after a database restart, a known limitation) | API log |

## 4. API

### 4.1 Attach in Docker: VS Code
Prerequisites: the **C# extension** (or C# Dev Kit), since vsdbg only works with Microsoft's IDE clients; and **Node.js** on the host (already needed for `web/`), which runs the vsdbg sync.

1. Start the debug stack: `docker compose -f docker-compose.yml -f docker-compose.debug.yml up --build -d`. The VS Code task `debug-stack: up` runs the same command.
2. In Run and Debug, start **Docker: attach API**.
3. Check the Debug Console for `Loaded '/app/Crawler.Api.dll'. Symbols loaded.` That line confirms the attach worked.
4. Set breakpoints in `src/Crawler.Api/...` and send a request.

How the configuration works (`.vscode/launch.json`):
- **vsdbg is matched to your C# extension at attach time (D-31).** The `preLaunchTask` (`vsdbg: sync api`) runs [`.vscode/sync-vsdbg.mjs`](../.vscode/sync-vsdbg.mjs). The script reads the newest installed C# extension's `package.json`, takes its Linux debugger download (URL and SHA-256) for the container's architecture, and installs it at `/vsdbg` with `docker compose exec -u 0`. It skips the download when the container already has that build. To run it by hand: `node .vscode/sync-vsdbg.mjs api`.
- **The service restarts when you stop debugging (D-32).** Stopping leaves the app suspended: vsdbg suspends the runtime to detach, but VS Code ends `docker compose exec` before it finishes, and nothing resumes the app. So each attach configuration has a `postDebugTask` (`debug-stack: restart api`) that restarts the service, which takes a few seconds. In-memory state is lost; a crawl that was running resumes from the queue on redelivery (D-13).
- **The sync task also restarts a service an earlier session left suspended.** That covers a session that ended without the `postDebugTask`, for example when VS Code was closed. It restarts the service if a vsdbg is still running in the container, or if the app doesn't answer `/health` within 3 seconds, then waits for it to answer before attaching.
- VS Code runs `docker compose exec -T api /vsdbg/vsdbg …` and talks to the debugger over that pipe.
- It attaches to PID 1. The image's entrypoint `exec`s `dotnet`, so the app is always PID 1.
- `sourceFileMap` maps `/src` to the workspace, because the image builds from `/src` and the PDBs point at `/src/src/Crawler.Api/...`.

Tested on 2026-10-26 with C# extension 2.160.4 and vsdbg 18.12.10903.2: attaching to the worker hit breakpoints in `JobCrawler.RunAsync`. The API uses the same configuration but hasn't been re-tested since the sync was added. Retested with the worker on 2026-10-27, after the restarts were added: stopping at a breakpoint restarted the worker, and attaching again worked.

**Breakpoints show solid red but never hit.** If the Debug Console shows "Symbols loaded" and the code runs (its log lines appear) but nothing stops, have vsdbg log its protocol traffic. Add `"logging": { "moduleLoad": false, "diagnosticsLog": { "protocolMessages": true } }` to the attach configuration, attach again, and search the Debug Console for `setBreakpoints`. A response of `"success":false` with "Incorrect breakpoint request format" means vsdbg is older than the C# extension. Run the sync task, or `node .vscode/sync-vsdbg.mjs <service>`. VS Code doesn't flag a rejected `setBreakpoints`; the dots stay solid red.

**The API or worker stops responding after a debug session.** `/health` hangs and the service logs nothing, even though its container is up. An earlier debug session left the app suspended (see above). Restart it: `docker compose -f docker-compose.yml -f docker-compose.debug.yml restart api` (or `worker`). Killing vsdbg isn't enough, because the app stays suspended.

**Attach fails with 0x80131c08.** That code is `CORDBG_E_TIMEOUT`: vsdbg started but timed out connecting to the runtime. The cause found on 2026-10-27 was a vsdbg from an earlier session, still attached to PID 1; the runtime accepts one debugger at a time. The sync task restarts the service in that case (D-32). If you attach some other way, check with `docker compose exec -T worker sh -c 'ps -eo pid,etime,args | grep "[/]vsdbg/vsdbg"'`, and restart the service if it lists one. vsdbg renames its process to "Command Thread", so look for it by command line.

### 4.2 Attach in Docker: Visual Studio
1. Start the debug stack as above.
2. **Debug → Attach to Process…** (Ctrl+Alt+P).
3. **Connection type:** *Docker (Linux Container)*. **Connection target:** *Find…*, then pick `alteva_rcm_tech_assessment-api-1`. The prefix is the folder name, so it differs if you cloned elsewhere.
4. Select the `dotnet` process (PID 1). Under **Attach to**, choose *Managed (.NET Core for Unix)*. Click **Attach**.
5. If Visual Studio says the source file wasn't found, browse to the matching file in your clone once. The PDB paths start with `/src/src/`.

Visual Studio may copy its own debugger into the container on first attach; that's expected. *Untested here*, including whether detaching leaves the app suspended as VS Code does (§4.1). If the service stops responding afterwards, restart it.

### 4.3 Attaching to the normal (Release) images
Visual Studio can attach to the mode A containers too. The code is optimized, though: locals show as unavailable, and breakpoints may land on nearby lines. Use the debug stack when you need to step through code. Mode A is for looking at a production-like build.

### 4.4 Run from source (mode C)
`docker compose up -d db rabbitmq`, then **Local: launch API** in VS Code, or F5 on `Crawler.Api` (profile `http`) in Visual Studio.
- It runs in `Development` and reads `appsettings.Development.json`, which points at the compose database and broker on localhost.
- It applies migrations at startup. Start it before the worker (D-16).

### 4.5 Where to break

| Location | What you see |
|---|---|
| `src/Crawler.Api/JobEndpoints.cs:30` `CreateJob` | Validation, then the job and root page insert |
| `JobEndpoints.cs:47` `SaveChangesAsync` | The commit. Stopping the API here and never continuing reproduces the outbox gap: a saved job with no message |
| `JobEndpoints.cs:53` `Publish` | The `CrawlJob` message being sent, with `CorrelationId` = job id |
| `JobEndpoints.cs:83` `GetJob` | The per-poll status query and `GROUP BY` page counts |
| `JobEndpoints.cs:103` `GetJobTree` | The pages and edges queries, and the tree built in memory |
| `src/Crawler.Api/JobRequestValidation.cs:9` `Validate` | Why a request got a 400 |
| `src/Crawler.Infrastructure/Persistence/PersistenceExtensions.cs:27` `MigrateAsync` | Migration at startup |

While the API is paused at a breakpoint, the UI's polls hang and then fail. That's expected.

### 4.6 SQL
In the debug stack, every command EF sends is logged under `Microsoft.EntityFrameworkCore.Database.Command`, with its parameters (values hidden) and duration:
```
docker compose logs api --no-log-prefix | grep Database.Command
```
In mode C, set the same level in `appsettings.Development.json`, or in an environment variable before `dotnet run`.

### 4.7 Common API problems

| Symptom | Likely cause | Check |
|---|---|---|
| The API container exits at startup | Migration failed: database not reachable, or bad credentials | `docker compose logs api` (startup exception); `docker compose ps db` |
| `/health` returns 503 | PostgreSQL or RabbitMQ unreachable | §2.3, §2.4; `docker compose ps` |
| A 500 on the first request after a database restart | A stale pooled connection (known limitation); the next request succeeds | API log, `Npgsql` exception |
| `POST` returns 202 but the job stays Pending | The message wasn't published or consumed | §5.6 |

## 5. Worker

### 5.1 Attach
Use the same steps as the API (§4.1–4.2), with **Docker: attach worker** in VS Code, or the `alteva_rcm_tech_assessment-worker-1` container in Visual Studio. Mode C uses **Local: launch worker**, or `Crawler.Worker` profile `http`. Then start a crawl from the UI or with:
```
curl -X POST http://localhost:5129/api/jobs -H "Content-Type: application/json" -d '{"url":"https://books.toscrape.com","maxDepth":1}'
```

### 5.2 One job's path, in breakpoints

| Step | Location |
|---|---|
| Message arrives; log scope opened | `src/Crawler.Worker/CrawlJobConsumer.cs:9` `Consume` |
| Job loaded and started (or skipped as a duplicate) | `src/Crawler.Worker/JobCrawler.cs:13` `RunAsync` → `CrawlStore.StartAsync` (`CrawlStore.cs:25`) |
| Per page | `JobCrawler.cs:34` `CrawlPageAsync` |
| HTTP fetch, redirects, content-type check | `src/Crawler.Infrastructure/Crawling/PageFetcher.cs:20` `FetchAsync`; the off-host redirect check is at `:49`; the body read is `ReadPageAsync` at `:69` |
| Link extraction and ratio | `src/Crawler.Domain/HtmlLinkExtractor.cs:10` `Analyze` → `LinkAnalysis.From` |
| The idempotent save (page, edges, children, counters) | `JobCrawler.cs:42` → `src/Crawler.Infrastructure/Persistence/CrawlStore.cs` `SavePageAsync`; the "already saved by another delivery" branch is `CrawlStore.cs:66` |
| Job completed or failed | `JobCrawler.cs:56` `FinishAsync` → `CrawlStore.FinishAsync` (`:99`) |
| Is this exception retried? | `src/Crawler.Infrastructure/Messaging/TransientErrors.cs:18` `IsTransient` |
| A dead-lettered job is marked Failed | `src/Crawler.Worker/CrawlJobFaultConsumer.cs:13` `Consume` → `CrawlStore.FailAsync` (`:114`) |

With several jobs running, use **conditional breakpoints**:
- `jobId == Guid.Parse("…")` in `RunAsync`,
- `page.Url.Contains("…")` in `CrawlPageAsync`.

### 5.3 What keeps running while you're paused
A breakpoint stops only the managed code. Timers and the broker keep going:
- **HTTP timeouts.** Polly's 10 s per-attempt and 20 s total timeouts are measured in wall-clock time. If you pause inside `FetchAsync` (or anything under it) for longer, the fetch can time out as soon as you continue, and the page is recorded as Failed ("Timed out after retries."). Put breakpoints after the fetch returns (`JobCrawler.cs:36` onward) when you care about the page outcome.
- **The job-row lock.** Pausing inside `SavePageAsync` holds a `FOR UPDATE` lock on the job row, inside an open transaction. Nothing else in this system waits on it, but a `psql` `UPDATE jobs …` for that job blocks until you continue.
- **RabbitMQ's delivery-acknowledgement timeout** (30 minutes by default). If the message isn't acknowledged within that time, the broker closes the channel and redelivers it. A later delivery resumes the job from its Pending pages.
- **`/health` stops answering.** Hitting a breakpoint suspends every thread in the process, including the web server. Compose has no healthcheck on `api` or `worker` (D-22), so nothing restarts a paused container. Don't add one to the debug override, or it will kill your debug session.

### 5.4 Reproducing scenarios on purpose

| Scenario | How | Expected |
|---|---|---|
| **Duplicate delivery** | Publish `CrawlJob` for a Completed job (§2.3) | Log: "Job is missing or already finished; nothing to do". No rows change. *(Tested.)* |
| **Dead letter** | Publish a payload that isn't valid JSON (for example `not json`) to the `CrawlJob` exchange | `R-FAULT` in the worker log; the message lands in `crawl-job_error`. It can't be deserialized, so there's no job id and no Fault event. *(Tested.)* |
| **Dead letter of a real job** | Insert a `Pending` job with no root page, then publish its id (as in README › Test) | The crawl ends with `InvalidOperationException`; the fault consumer marks the job Failed with that reason |
| **Database outage** | Start a depth-2 crawl, then `docker compose stop db`, wait about 8 s, then `docker compose start db` | `R-RETRY` lines, then the job completes (D-28, D-29). Over about 1 minute, it's dead-lettered instead |
| **Worker stopped mid-crawl** | `docker compose stop worker` during a crawl, then `docker compose start worker` | The job stays Running with its message back on `crawl-job`, then resumes and completes |
| **Concurrent duplicate delivery** | Publish the same `CrawlJob` again *while* the job is running | Both deliveries crawl. Pages saved by one are logged as "already crawled by another delivery; result discarded" by the other (reviewer guide §5.1) |
| **Redirects, timeouts, non-HTML** | Serve `tests/Crawler.IntegrationTests/Fixtures/site` from a small static server on the compose network (alias `site`) and crawl `http://site/` | Exercises the same paths as the fixture tests against the real worker |

### 5.5 HTTP detail
In the debug stack, Polly logs every attempt at Information under the category `Polly`. That includes the attempt number, outcome and duration, so you can see retries and timeouts per request:
```
docker compose logs worker --no-log-prefix | grep '"Category":"Polly"'
```
`System.Net.Http.HttpClient` stays at Warning. To log each request's start and end, add `Logging__LogLevel__System.Net.Http.HttpClient: Information` to the `worker` environment in `docker-compose.debug.yml`.

### 5.6 Common worker problems

| Symptom | Likely cause | Check |
|---|---|---|
| **Job stuck Pending** | No worker running; the worker can't reach RabbitMQ; or the API crashed between commit and publish (the outbox gap) | `docker compose ps worker`; `rabbitmqctl list_queues` (does `crawl-job` have a consumer?); API log for "Created crawl job". To recover, publish the `CrawlJob` by hand (§2.3) |
| **Job stuck Running, nothing moving** | The worker is paused in a debugger; or the message was dead-lettered while the database was down, so the fault couldn't be recorded | Frontier query (§2.4); `crawl-job_error` and `crawl-job-fault_error` |
| **Job Failed: "Start page failed: …" / "Start page skipped: …"** | The start URL errored, isn't HTML, or redirects to another host (`example.com` → `www.example.com`) | The start-page query (§2.4); resubmit with the host named in the reason |
| **Job Failed: "Crawl aborted by an unrecoverable worker error: …"** | The message was dead-lettered; the reason holds the exception | Worker log for `R-FAULT`; the message headers in `crawl-job_error` |
| **Many pages Failed with timeouts** | A slow site, or you paused in a fetch (§5.3) | Polly lines; `error` in pages |
| **Fewer pages than expected** | `maxDepth`, the 200-page cap (`maxPagesReached`), exact-host matching, or links the server doesn't render (JavaScript) | `pages_enqueued` vs `max_pages`; one page's edges with `is_same_domain = false` |

## 6. Diagnostics inside containers
The debug image includes `dotnet-counters`, `dotnet-trace` and `dotnet-dump`, which attach to PID 1 as the same `app` user. All of these commands were tested against the worker.

| Question | Command |
|---|---|
| Live CPU, GC, thread pool, exceptions/s | `docker compose exec worker dotnet-counters monitor -p 1` (press Q to quit) |
| The same, saved for later | `docker compose exec worker dotnet-counters collect -p 1 --duration 00:00:00:30 -o /tmp/counters.csv` |
| Where is CPU time going? | `docker compose exec worker dotnet-trace collect -p 1 --duration 00:00:00:30 -o /tmp/worker.nettrace` |
| A hang, a deadlock, or "what is every thread doing?" | `docker compose exec worker dotnet-dump collect -p 1 -o /tmp/worker.dmp`, then `docker compose exec worker dotnet-dump analyze /tmp/worker.dmp` and run `clrthreads`, `clrstack -all` or `dumpasync` |
| Memory growth | A dump, then `dumpheap -stat` in `dotnet-dump analyze` |

Copy files out to open them on the host. Open `.nettrace` in Visual Studio or PerfView, and `.dmp` in Visual Studio or with `dotnet-dump`:
```
docker compose cp worker:/tmp/worker.nettrace .
```
Delete them afterwards (`docker compose exec worker rm /tmp/worker.*`). A full dump is roughly the size of the process's memory, so expect hundreds of MB.

Write to `/tmp`: `/app` is owned by root and isn't writable by the `app` user.

## 7. Symptom → where to look

| Symptom | Start with |
|---|---|
| UI: "Cannot reach the API" | `curl localhost:5129/health`; browser console for CORS (§3.6) |
| UI: field errors on submit | Network → Response `errors` (§3.2) |
| API: 503 on `/health` | `docker compose ps`; db and rabbitmq logs |
| API: container exits at startup | `docker compose logs api` (migration exception) |
| Job Pending for more than a few seconds | `crawl-job` consumers; worker up? (§5.6) |
| Job Running with no progress | Frontier query; error queues; a paused debugger (§5.3) |
| Job Failed | `failure_reason`; the start-page query; `R-FAULT` in the worker log |
| A page has the wrong ratio | That page's edges (§2.4); break in `HtmlLinkExtractor.Analyze` |
| A page you expected is missing | Depth and cap; `is_same_domain`; the parent page's edges |
| Retries or timeouts | Polly lines (debug stack); `R-RETRY` |
| A worker that's slow or stuck | `dotnet-counters`, then `dotnet-dump` (§6) |
| A log override does nothing in Docker | Dotted environment variable names (§8.2) |

## 8. Appendix

### 8.1 Tests
```
dotnet test --filter Category!=Docker                 # fast: domain, fetcher (stub site), validation
TESTCONTAINERS_RYUK_DISABLED=true dotnet test         # all, incl. Testcontainers Postgres (needs Docker)
```
- **Running tests inside a container** (for example the .NET SDK image with the Docker socket mounted): also set `TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal`. Without it, every Docker test fails with "Failed to connect to 172.17.0.1:<port>".
- **Debugging a test:**
  - VS Code (C# Dev Kit): use the *Debug Test* code lens above a test, or the Testing view.
  - Visual Studio: in Test Explorer, right-click → *Debug*.
  - Docker tests start a `postgres:16-alpine` container for the run, so Docker must be running.
- **UI tests:** `npm test` in `web/`, or the **UI: debug Vitest** configuration.

### 8.2 Environment gotchas
- **Smart App Control (Windows 11).** On some machines, Windows Application Control blocks freshly built, unsigned DLLs (`0x800711C7`). Then mode C, `dotnet test` and `dotnet ef` fail to load the project assemblies (D-17). Use the debug stack (mode B) to debug, and run build and tests in `mcr.microsoft.com/dotnet/sdk:8.0` with the repo mounted.
- **Git Bash rewrites container paths.** `docker compose exec api ls /app` becomes `ls C:/Program Files/Git/app`. Prefix the command with `MSYS_NO_PATHCONV=1`, or run it from PowerShell.
- **Dotted environment variable names are dropped by the normal images.** `Dockerfile` starts the app with `sh -c`, and Debian's `sh` (dash) removes variables whose names aren't valid shell identifiers. So an override like `Logging__LogLevel__Microsoft.AspNetCore=Debug` on the mode A `api` or `worker` service silently has no effect. Names without dots (`Logging__LogLevel__Polly`) work. `Dockerfile.debug` starts with `bash -c`, which keeps them (D-30).
  - Check what the process really got: `docker compose exec worker sh -c 'tr "\0" "\n" < /proc/1/environ'`.
  - `docker compose exec … printenv` does *not* show this, because `exec` bypasses the entrypoint.
- **vsdbg must match the C# extension.** An older vsdbg in the container rejects the extension's breakpoint requests, so breakpoints stay solid red but never hit. The Docker attach configurations sync vsdbg before each attach (§4.1, D-31). If you attach some other way, run `node .vscode/sync-vsdbg.mjs <service>` first.
- **Stopping a Docker debug session suspends the app.** vsdbg never finishes detaching, so the app stays suspended until the service restarts. The VS Code attach configurations restart it for you (§4.1, D-32).
- **Ports in use.** 5129, 5250, 5432, 5672, 15672 and 5173 must be free. A local PostgreSQL on 5432 is the usual culprit.
- **Resetting.** `docker compose down -v` deletes the database volume (all jobs). The broker keeps no volume, so its queues, and any shovel plugins you enabled, reset whenever its container is recreated.
