# Decisions

A numbered log of design decisions. Entries are never rewritten; a later entry supersedes an earlier one.

## D-1 Messaging: MassTransit 8.x on RabbitMQ
- **Decision:** MassTransit 8.x on RabbitMQ, pinned below 9.
- **Options:** MassTransit 8; raw RabbitMQ.Client; MassTransit 9.
- **Why:** retries, error queues, fault events and an EF outbox come built in. Version 9 has a commercial license.
- **Trade-off:** we stay on the 8.x line.

## D-2 One message per page
- **Decision:** one `CrawlPage` message per page. If the 2:30 checkpoint (now 2:50, see D-5) is missed, fall back to one message per job.
- **Options:** per page; per job.
- **Why:** per-page messages allow parallel workers, per-page retries and exercise idempotency.
- **Trade-off:** more moving parts (counters, completion detection, duplicate deliveries).

## D-3 Commit cadence
- **Decision:** commit after Jim approves each slice's diff summary.
- **Why:** reviewable history; one slice per commit.
- **Trade-off:** pauses between slices.

## D-4 Test assertions
- **Decision:** xUnit with plain asserts; no FluentAssertions.
- **Why:** FluentAssertions 8+ is commercially licensed; plain asserts are enough.
- **Trade-off:** slightly less readable failure messages.

## D-5 Checkpoints rebaselined to the estimates
- **Decision:** checkpoints at 2:00 (slices 0–3) and 2:50 (end-to-end via curl). If 2:00 is missed, cut 12 and 10 right away. Slice 2's database smoke test is dropped (slice 4's Docker test applies the migration).
- **Options:** keep 1:45/2:30 and start cutting early; cut estimates; move checkpoints.
- **Why:** the original checkpoints were about 20 minutes ahead of the slice estimates, so they would be missed even if every estimate held.
- **Trade-off:** later warning of slippage. Open: must-have slices total 245 minutes against a 210-minute build window; the UI checkpoint waits on Jim's ruling.

## D-6 Postgres-backed tests use Testcontainers on the local image
- **Decision:** worker and duplicate-delivery tests run against Testcontainers Postgres using the locally installed `postgres:16` image. One container per run (xUnit collection fixture), migrate once, TRUNCATE between tests, tagged `Category=Docker`.
- **Options:** EF in-memory provider; SQLite; Testcontainers.
- **Why:** the worker step depends on `ON CONFLICT`, `FOR UPDATE` and `RETURNING`, which only real Postgres supports. Docker Desktop is available.
- **Trade-off:** these tests sit outside the fast loop and run before each commit, not after every change. Slice 5's duplicate-delivery test is no longer cuttable.

## D-7 Fault consumer is a must-have
- **Decision:** a `Fault<CrawlPage>` consumer marks a dead-lettered page Failed through the same "finish page" transaction (`WHERE status='Pending'`, increments `pages_processed`, completion check).
- **Options:** leave it cuttable; add a job watchdog; Fault consumer.
- **Why:** completion requires processed == enqueued, so one poison message would otherwise leave its job Running forever. It is about 10 minutes of work.
- **Trade-off:** a crashed worker (not a fault) can still strand a page; no watchdog remains a stated limitation.

## D-8 Redirects followed manually
- **Decision:** `AllowAutoRedirect = false`. The fetcher follows up to 5 hops, normalizing each `Location` and checking same-host on every hop. Off-host hop → Skipped without fetching; too many hops or a loop → Failed. The page keeps its requested URL; `final_url` records the destination.
- **Options:** automatic redirects with a check on the final URL; manual redirects.
- **Why:** automatic redirects fetch off-host pages before any check can run, which breaks the same-host rule and widens SSRF exposure.
- **Trade-off:** a little more fetcher code. Two URLs redirecting to the same page are both crawled (stated limitation).

## D-9 Fetcher returns results; message retry is infrastructure-only
- **Decision:** the fetcher returns `FetchResult` (Fetched | Skipped | Failed + reason) and never throws for HTTP outcomes. `UseMessageRetry` handles only transient Postgres errors. HTTP total timeout lowered from 30 s to 20 s.
- **Options:** let HTTP exceptions reach message retry; separate the layers.
- **Why:** stacking 30 s of HTTP retries inside 3 message retries could hold a consumer for about 90 s on one bad page.
- **Trade-off:** a page whose host is down is marked Failed after about 20 s with no second attempt via the broker.

## D-10 Trim scope and build the UI before Docker and robustness (supersedes D-5's checkpoints)
- **Decision:** fold slice 6 (log scopes, health checks) into slices 3 and 4; show UI progress as "processed / enqueued" text; use one multi-stage Dockerfile for API and worker. Build order 0 → 4, then 8 (UI), 7 (Docker), 5 (robustness). Checkpoints: 2:05 slices 0–3, 2:55 end-to-end via curl, 3:30 UI, 3:45 compose up, 4:00 freeze.
- **Options:** (A) trim scope only; (B) reorder only; (C) both. Jim chose C.
- **Why:** must-have slices totalled 245 m against a 210 m window. Trimming brings it to 225 m; reordering puts the visible deliverable and the required compose check ahead of the remaining overrun.
- **Trade-off:** slice 5 runs last with about 15 of its 30 m. Its retry config and Fault consumer go in first; its Docker tests are the first must-have to cut, documented as a manual check. A slip in slices 0–4 now pushes the UI past 3:30.

## D-11 Scaffold choices
- **Decision:** `global.json` pins SDK 8.0.426 (roll forward within 8.0.x). The worker is a web project so it can serve `/health`. Compose and Testcontainers use the locally installed `postgres:16-alpine` and `rabbitmq:3.13-management` images. Analyzers at `latest-recommended`; warnings stay warnings. The test template's `coverlet.collector` was removed because it isn't on the approved package list.
- **Options:** default SDK (10.x is installed and would win); a generic-host worker plus a separate health listener; `postgres:16` (not present locally).
- **Why:** the assessment requires .NET 8; one hosting model for both services; no image pulls during the timebox.
- **Trade-off:** the worker carries the ASP.NET Core runtime; no coverage collection.

## D-12 Cut slice 5's Docker tests
- **Decision:** drop the automated duplicate-delivery and Fault-consumer tests. Slice 5 keeps the retry configuration and the Fault consumer; the README documents a manual check instead.
- **Options:** keep them and cut elsewhere; cut them now; cut them at the end if time runs out.
- **Why:** slice 0 finished at 1:18, 33 m late (the plan review used the start of the window), leaving about 48 m more must-have work than time. These tests were next in the cut order.
- **Trade-off:** duplicate delivery and fault handling have no automated coverage. The slice 4 integration test still covers a second delivery of a completed job.

## D-13 One message per job (supersedes D-2; amends D-7)
- **Decision:** the API publishes one `CrawlJob { jobId }`; one consumer crawls the whole job. The BFS frontier is the job's `Pending` pages in the database, taken in `(depth, id)` order, so a redelivered message resumes where the previous attempt stopped. A `Fault<CrawlJob>` consumer marks the job Failed. The pages/edges schema, idempotent page writes and the API are unchanged.
- **Options:** keep one message per page; switch now; switch at the 2:55 checkpoint as D-2 allowed.
- **Why:** removes the per-page completion race, cross-message counters and most duplicate-delivery handling, saving about 30 m across slices 4–5. Switching now avoids building the per-page path in slices 2–3 and then discarding it.
- **Trade-off:** no parallelism across a job's pages; one slow site holds a consumer for the whole crawl. Idempotency is at job level (Completed/Failed jobs are skipped) plus the existing `WHERE status='Pending'` page writes. The messaging story in the write-up is simpler.

## D-14 Schedule after D-12 and D-13
- **Decision:** checkpoints reprojected from the real start of slice 1 (1:18): 2:40 slices 0–3, 3:20 end-to-end via curl, 3:55 UI, 4:00 freeze.
- **Why:** after D-12 and D-13, 180 m of must-have work remains against 162 m to freeze, so we are still about 20 m short.
- **Trade-off / open:** Docker (7) and robustness (5) don't fit as estimated. The next must-have cut is pending Jim's ruling.

## D-15 HTML parsing lives in Domain, with AngleSharp 1.8.5
- **Decision:** `HtmlLinkExtractor` (AngleSharp) sits in Domain beside `UrlNormalizer` and `LinkAnalysis`. AngleSharp is 1.8.5, the latest 1.x, because 1.1.2 has a moderate-severity advisory (GHSA-pgww-w46g-26qg).
- **Options:** parse in Infrastructure and pass hrefs to Domain; parse in Domain; regex extraction.
- **Why:** parsing a string is pure computation with no I/O, so it keeps the extraction rules (including `<base href>`) under fast unit tests next to the normalization rules. A real parser handles malformed HTML that regex wouldn't.
- **Trade-off:** Domain takes a third-party dependency (MIT, already pre-approved).

## D-16 Schema: EF Core migrations, applied only by the API
- **Decision:** one EF Core `InitialCreate` migration (snake_case names, statuses stored as strings), generated with `dotnet-ef` 8.0.11 as a repo-local tool. The API runs `Migrate` at startup; the worker doesn't.
- **Options:** both processes migrate at startup; API only; a separate migration job in compose; `EnsureCreated`.
- **Why:** EF 8 takes no lock while migrating, so two processes starting together can race on `__EFMigrationsHistory`. A separate job is cleaner but costs time we don't have. `EnsureCreated` can't evolve the schema.
- **Trade-off:** the worker needs the API to have started once against a database. Compose ordering (slice 7) handles this; integration tests migrate in their fixture.

## D-17 Dev loop runs in the .NET 8 SDK container on this machine
- **Decision:** mid-session, Windows Application Control (Smart App Control) began blocking freshly built, unsigned DLLs (0x800711C7), so test hosts and `dotnet-ef` could no longer load the project assemblies. Build, `dotnet format`, `dotnet ef` and tests now run inside `mcr.microsoft.com/dotnet/sdk:8.0` against a copy of the repo without `bin`/`obj`.
- **Options:** turn off Smart App Control (irreversible without a Windows reset); run in the SDK container.
- **Why:** it keeps the machine's security policy intact and needs no repo changes.
- **Trade-off:** each check costs a container start. Local environment only; nothing in the repo depends on it.

## D-18 API wiring: MassTransit 8.5.12, built-in bus health check, late-bound connection string
- **Decision:** MassTransit.RabbitMQ 8.5.12 (latest 8.x) with kebab-case endpoint names. `/health` combines AspNetCore.HealthChecks.NpgSql 8.0.2 with the bus health check MassTransit registers itself; AspNetCore.HealthChecks.Rabbitmq isn't used. The DbContext and the Postgres check read `ConnectionStrings:Crawler` from the final configuration when first resolved, not in `Program`. `Database:MigrateOnStartup` (default true) lets tests skip migration. The publish after the insert isn't tied to the request's cancellation token.
- **Options:** the Rabbitmq health-check package (opens its own connection; its RabbitMQ.Client major version must match MassTransit's); reading config eagerly in `Program`; publishing with the request token.
- **Why:** the bus check reports the connection the API actually publishes on, with one package fewer. Eager reads in `Program` happen before `WebApplicationFactory` applies test settings. A client disconnect between the commit and the publish would otherwise strand a saved job without a message.
- **Trade-off:** a missing connection string fails on the first request, not at startup. The outbox gap (crash between commit and publish) remains until slice 9.

## D-19 API test split and tree response shape
- **Decision:** validation and routing tests run in the fast loop against an API with an unreachable database. Persistence, 404, history, tree and health tests run on Testcontainers Postgres with the API's own startup migration; Ryuk is disabled (`TESTCONTAINERS_RYUK_DISABLED=true`) so no image is pulled. Tree nodes add `error` and per-link `isSameDomain` to the planned shape; the tree is built by `PageTree` in Domain (the plan's `TreeBuilder`).
- **Options:** EF in-memory provider for 404s (not an approved package, and it ignores Postgres behavior); all API tests on Docker; plain URL strings for links.
- **Why:** 400s need no database, so they stay fast; 404s need real queries. The UI can show why a page failed and which links are external without a second call.
- **Trade-off:** if a test run is killed, its Postgres container is left behind. Responses are slightly larger.

## D-20 Worker and fetcher details
- **Decision:** `PageFetcher` is a typed client on `Microsoft.Extensions.Http.Resilience` 8.10.0 (latest 8.x). The standard handler keys pipelines per host (`SelectPipelineByAuthority`); the first retry waits 500 ms. The client reads only headers inside the pipeline, then reads the body under its own 10 s timeout with a 2 MB cap (a larger body is Skipped). `text/html` and `application/xhtml+xml` count as HTML. Child pages are enqueued with one `INSERT … SELECT FROM unnest(…) WHERE NOT EXISTS … LIMIT remaining` while the job row is locked. Whether the job is Completed or Failed is decided when the frontier is empty, from the root page's status. Tests run `JobCrawler` through DI with a stub primary handler, not through MassTransit; the consumer only adds the log scope.
- **Options:** one shared pipeline per client; buffering the whole response inside the pipeline; one INSERT per child; failing the job right after the root page is saved; exercising the consumer through the MassTransit test harness.
- **Why:** a single pipeline shares one circuit breaker across every site being crawled. Buffering inside the pipeline downloads large non-HTML bodies before the content type is checked. One statement keeps the transaction short. Deciding at the end also covers a redelivery after a crash between saving the root page and finishing the job. Calling the crawler directly keeps the tests deterministic.
- **Trade-off:** each redirect hop gets its own 20 s budget, so a 5-hop chain can take up to about 2 minutes. A long crawl can outlast RabbitMQ's default 30-minute delivery-acknowledgement timeout, in which case the job is redelivered and resumes. The consumer's wiring is checked only by the slice 7 smoke run.

## D-21 UI stack, versions and polling
- **Decision:** React 18.3.1 with react-router-dom 7.18.4, Vite 7.3.7, @vitejs/plugin-react 5.2.0, TypeScript 5.6.3, Vitest 4.1.11; exact versions, `package-lock.json` committed. The job page polls the job every 1.5 s and refetches the tree only when `pagesProcessed` or the status changes. The UI test renders `PageTree` with `react-dom/server`'s `renderToStaticMarkup` in Vitest's node environment. The API base URL comes from `VITE_API_URL`, defaulting to the API's dev port. Locally, npm runs in `node:20-alpine` with `node_modules` in a Docker volume, because Node isn't installed on this machine (same reason as D-17).
- **Options:** Vite 5 / react-router 6 / Vitest 2 (installed first: `npm audit` showed 2 critical, 1 high and 5 moderate advisories, mostly in the dev server and test runner, plus a moderate open redirect in react-router's `<Link>`); latest majors; jsdom or Testing Library for the tree test; refetching the tree on every tick.
- **Why:** same rule as D-15: take the patched version instead of shipping known advisories. Vitest 4.1.12, which fixes the last two advisories, is 8 days old, so 4.1.11 is used instead of a release that new. Static rendering needs no extra package (jsdom and Testing Library aren't on the approved list). The tree is the large response, so it isn't refetched unless something changed.
- **Trade-off:** `npm audit` still reports 2 moderate advisories in Vitest 4.1.11 (mock-redirect path traversal, GHSA-xp26-3454-mjgp). They are dev-only and need a running Vitest server with untrusted test code; we only run `vitest run`. Upgrade to 4.1.12 once it has been out for two weeks. npm 10's resolver fails on a fresh install of this tree (`Cannot read properties of null (reading 'edgesOut')`); `npm ci` from the lockfile works on npm 10, and the lockfile was generated with npm 11.6.2. The tree test checks markup, not interaction (the collapsible link lists are native `<details>`).

## D-22 One Dockerfile for both services; compose orders startup with depends_on only
- **Decision:** a multi-stage `Dockerfile` (`sdk:8.0` build, `aspnet:8.0` runtime, non-root `app` user, port 8080) whose `PROJECT` build arg picks the API or the worker. Restore runs from the project files alone so source edits keep the package layer cached. Compose adds `api` (host 5129, the UI's default) and `worker` (host 5250). Both wait for healthy `db` and `rabbitmq`; the worker waits only for the API to have *started*. Connection settings come from compose environment variables built from the same dev-credential variables as the infrastructure, so the containers run in `Production` and never read `appsettings.Development.json`. No compose healthcheck for the API or worker.
- **Options:** two Dockerfiles; a healthcheck with curl installed in the image, or a bash `/dev/tcp` probe; the worker waiting for a healthy API.
- **Why:** the two services share their whole build. The worker touches tables only when a message arrives, and messages are published only after the API has migrated and started serving, so `service_started` is enough for D-16's ordering. Adding curl only for a healthcheck makes the image bigger and gives it more to patch.
- **Trade-off:** `docker compose ps` doesn't show API or worker health; check `/health` directly. Each image is about 340 MB.

## D-23 Worker logs Polly at Warning
- **Decision:** the worker's `Logging:LogLevel:Polly` is `Warning`.
- **Options:** keep Information; Warning; filter only the "Execution attempt" event.
- **Why:** at Information, Polly logs one line for every successful attempt (75 lines for a 74-page crawl), which buries the crawler's own per-page lines. Retries and failed attempts still log at Warning.
- **Trade-off:** per-attempt timings aren't logged for successful fetches. Set it back to `Information` when debugging the retry pipeline.

## D-24 Message retry, dead-lettering and the fault consumer
- **Decision:** both worker endpoints (`crawl-job`, `crawl-job-fault`) use `UseMessageRetry` with 3 exponential retries (1 s to 10 s), filtered by `TransientErrors.IsTransient`: an `NpgsqlException` whose `IsTransient` is true, or a `DbUpdateException` wrapping one. Everything else, or a fourth failure, goes to MassTransit's `<endpoint>_error` queue, which publishes `Fault<CrawlJob>`. `CrawlJobFaultConsumer` then calls `CrawlStore.FailAsync`, which moves the job to Failed only `WHERE status IN ('Pending','Running')`, with the first exception's type and message as `failure_reason`. Retry is configured through consumer definitions, not bus-wide. A small Docker test covers `FailAsync`; the consumers themselves were checked by hand.
- **Options:** bus-wide retry; retrying every exception; MassTransit's redelivery (delayed, via the broker) in addition to immediate retry; no retry on the fault consumer; leaving the D-12 cut in place and adding no test.
- **Why:** a retried crawl resumes from the database frontier, so retrying is safe, but only worth it when the cause can clear on its own. A bug or bad data fails the same way every time and should reach the error queue at once. The fault consumer writes to the same database, so it gets the same retry. We were ahead of the D-14 checkpoints, so one `FailAsync` test (about 2 minutes) went back in; the consumer wiring still has no automated test.
- **Trade-off:** retries are in-process, so a database outage longer than about 15 s dead-letters the job, and if the database is still down the fault consumer can't mark it Failed either; that fault then sits in `crawl-job-fault_error`. The error queue is not replayed automatically. A worker that is stopped mid-crawl is not a fault: MassTransit treats the cancellation as consume-canceled and the message is redelivered (checked by hand, below).

## D-25 A job fails when its start page isn't fetched (amends the "Job Failed only if the root page fails" default)
- **Decision:** when the frontier is empty, the job is Completed only if the start page is Fetched. A start page that Failed gives "Start page failed: <error>"; one that was Skipped (not HTML, over 2 MB, or redirected off-host) gives "Start page skipped: <reason>". Both set `completed_at`.
- **Options:** keep Completed with a single Skipped page and document it; fail the job.
- **Why:** a crawl whose start page couldn't be parsed has no tree and no ratios, so Completed would hide the problem (checklist W-6). The failure reason tells the user why.
- **Trade-off:** a start URL that redirects to another host (for example `example.com` → `www.example.com`) now fails instead of completing empty; the reason names the target host so the user can resubmit with it.

## D-26 Job summary: page counts by outcome and a page-limit flag
- **Decision:** `GET /api/jobs/{id}` adds `pageCounts` (pending, fetched, skipped, failed) from one `GROUP BY status` over the job's pages, and every job response adds `maxPagesReached` (`pagesEnqueued >= maxPages`). History rows leave `pageCounts` null.
- **Options:** counter columns on `jobs` kept by the worker; counts on history rows too; leave the summary as processed / enqueued.
- **Why:** the spec asks for a status "summary" without defining it; outcome counts and whether the limit cut the crawl short are what a user needs to read a result (checklist W-4). The grouped query uses the `(job_id, …)` index and stays cheap at 200 pages per job, so no new columns or migration.
- **Trade-off:** the job page runs this query on every 1.5 s poll while a job is running.

## D-27 Cut the outbox, cancel and slice 12; start docs early
- **Decision:** at 3:20, with all must-have slices done, slices 9 (transactional outbox), 10 (cancel) and 12 (SSRF guard, robots.txt, UI in compose, SSE) are cut. The remaining time goes to the gaps found by the spec checklist (job ID visible, D-25, D-26), the README's submission sections from 3:25 instead of 4:00, and a clean-copy acceptance run. Slice 11 (CI) is built only if time remains.
- **Options:** build the outbox for the event-robustness score; build cancel; spend the time on docs and acceptance.
- **Why:** the rubric accepts the publish-after-commit gap if it is "handled or documented", and cancel is optional. The README was missing most of its required sections, and 20 minutes wasn't enough for them.
- **Trade-off:** the outbox gap and the lack of an SSRF guard remain, stated as known limitations with fixes listed under next steps.

## D-28 Transient check walks the whole exception chain (supersedes D-24's filter)
- **Decision:** `TransientErrors.IsTransient` returns the `IsTransient` of the first `NpgsqlException` found anywhere in the exception's inner-exception chain. Retry counts, backoff and dead-lettering are unchanged from D-24.
- **Options:** keep the two explicit shapes and add a third for `InvalidOperationException`; enable EF's `EnableRetryOnFailure`; walk the chain.
- **Why:** the acceptance run's 5 s database outage showed that outside `SaveChanges`, EF's default execution strategy wraps transient Npgsql errors in `InvalidOperationException`. D-24's filter missed it, so the job was dead-lettered at once and, since the fault consumer failed the same way, stayed Running. EF's retrying strategy would conflict with the explicit transactions in `CrawlStore`. Walking the chain covers every wrapper without listing them.
- **Trade-off:** an unrelated exception that happens to wrap a transient Npgsql error is retried too; at worst that costs three extra attempts before dead-lettering.
