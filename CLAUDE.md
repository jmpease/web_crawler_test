# Conventions
- C#, .NET 8 (required by the assessment), nullable enabled, async throughout, CancellationToken passed through.
- Layers: Domain (pure, no I/O) <- Infrastructure (EF, HTTP, messaging) <- Api / Worker. Domain never references EF or MassTransit.
- Minimal APIs; errors returned as ProblemDetails. Small methods, meaningful names.
- Frontend: Vite + React + TypeScript in `web/`. Function components and hooks. No UI framework; plain CSS.
- Tests: xUnit with plain asserts. Tests never touch the live internet. HTML fixtures are local files served by a stub HttpMessageHandler.
- Fast tests: `dotnet test --filter Category!=Docker` after every change, and show the output.
  Tests needing Docker (Testcontainers) carry `[Trait("Category","Docker")]` and run before each commit.
- Under the timebox: when a requirement is unclear, use the default in PLAN.md's open-questions list, log it in DECISIONS.md, and keep going.
  Stop and ask only if no default covers it and the choice is hard to reverse.

# Packages
Pre-approved (don't ask again): Npgsql.EntityFrameworkCore.PostgreSQL 8.x (PostgreSQL License), Microsoft.EntityFrameworkCore.Design 8.x (MIT),
MassTransit + MassTransit.RabbitMQ + MassTransit.EntityFrameworkCore **8.x only** (Apache-2.0), Microsoft.Extensions.Http.Resilience (MIT),
AngleSharp (MIT), AspNetCore.HealthChecks.NpgSql / .Rabbitmq (Apache-2.0), xUnit (Apache-2.0), Microsoft.AspNetCore.Mvc.Testing (MIT),
Testcontainers.PostgreSql (MIT); npm: react-router-dom (MIT), vitest (MIT).
Never: FluentAssertions 8+ or MassTransit 9+ (commercial licenses). Anything else: say what it's for and its license, then ask.

# Logging and safety
- Structured logs. Every consumer and request log scope includes jobId (as correlationId) plus pageId/messageId where known.
- Never log page bodies or full HTML. URLs are fine.
- Crawler: honor maxDepth and maxPages, same-host only, a descriptive User-Agent, timeouts on every request.
- No secrets in git. Compose uses dev-only credentials via environment variables.
- Never copy anything from the review folder's `payload/` or `context/` into this repo.

# Documentation: updated in the same commit as the change
- README.md: how to run, how to test, configuration, current scope. Update it in the same commit as any change to setup, endpoints,
  configuration or behavior. By submission it must also contain: key architectural choices; message schema; idempotency strategy;
  retry policy and what counts as transient; DLQ handling; known limitations; assumptions and trade-offs; what was built first and why;
  what was cut and why; what's next; a short summary of what was implemented.
- DECISIONS.md: a numbered log of design decisions (a lightweight form of architecture decision records). When a commit makes a design
  choice, append an entry: decision, options considered, why, trade-off. Don't rewrite old entries; add one that supersedes them.
  Create the file if it doesn't exist.
- No doc changes for commits that don't change behavior, setup or design.
- Before every commit, check README.md and DECISIONS.md against the diff.

# Working from the plan
- PLAN.md holds the approved plan: approach, then an ordered list of slices, each with its tests and a must-have or cuttable mark.
- Build from PLAN.md one slice at a time, in order. Tick the slice off in PLAN.md in the commit that finishes it.
- If a slice shows the plan is wrong, stop and propose a change to PLAN.md before going on.
- Timebox: checkpoints and cut order are in PLAN.md. When a checkpoint is missed, say so and propose the next cut. Don't silently push on.
  4:00 pm ET is code freeze; 4:00–4:20 is reserved for docs and packaging.

# Commits
- At the end of each slice: stop, summarize the substantive change and anything needing Jim's ruling, and wait.
  Commit only after Jim approves. Commit messages say why.
- Stage only the paths the slice touched; never `git add -A`. Verify the commit with `git log -1 --stat`.

# Quality checks before each commit
- `dotnet format`, then `dotnet build` with no new warnings, then all tests.
- If `web/` changed: `npm run build` (includes `tsc`) passes.
- Before the final commit: `docker compose up --build` from clean, run one crawl end to end, and check `/health` on API and worker.
