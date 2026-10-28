# Retrospective

> Written on 2026-10-27, after the assessment window closed. It reviews the 16 commits on `main` (2026-10-23, 13:01–15:59 ET) and changes no code. Code-level improvements are in the [reviewer guide §4–5](REVIEWER_GUIDE.md#4-whats-missing). This page is about **how the work was planned, sequenced, tested and committed**, and what to do differently next time.

**How the window ran.** The assessment was delivered at 12:00 ET, and that is when the timebox started. Nothing about it was known in advance. From 12:00 to 12:30 the spec was reviewed, and the first CLAUDE.md and PLAN.md were drafted in a separate Cowork session. At about 12:30 the repo was created and Claude Code was first started in it, which is why PLAN.md's build window begins at 12:30. Prerequisites that weren't on the machine (the .NET 8 SDK, Node, Docker Desktop and its images) were installed inside the window. Git records only what happened from the first commit at 13:01 onward, so anything earlier comes from Jim's account.

Sources: `git log main`, [PLAN.md](../PLAN.md) (as first committed in `d2b6ae4` and as finished in `6053354`) and [DECISIONS.md](../DECISIONS.md) (cited as D-n).

## 1. Summary

The result was good. All must-have slices shipped, the clean-clone acceptance run passed, the docs were done at code freeze, and every trade-off is on record. The misses were not in the design. They were in **estimating, sequencing and testing**:

- The plan budgeted 12:00–12:30 for review and planning. It had no line for environment setup or for reviewing the plan again once it was in the repo, so slice 0, planned to finish at 12:45, finished at 13:18, 33 minutes late (D-12). The plan's arithmetic was also wrong.
- The estimates turned out about 2× too pessimistic, so tests were cut early and never put back.
- Those cut tests covered the code that then broke: two retry bugs were found only by the manual outage check, 20 minutes before freeze.
- The docs and the spec-checklist pass both came late, so the last hour was spent on rework.

## 2. Timeline

Commit times include waiting for approval (D-3), so each gap is an upper bound on the work time. Rows marked † come from Jim's account, not from git.

| Time | Commit | Slice | Gap since previous | Estimate |
|---|---|---|---|---|
| 12:00 † | — | assessment delivered; spec review and first CLAUDE.md / PLAN.md in Cowork | — | 30 m (budgeted) |
| 12:30 † | — | repo created, Claude Code started in it; prerequisite installs (.NET 8 SDK, Node, Docker Desktop, images) | 30 m | not budgeted |
| 13:01 | `d2b6ae4` | plan + conventions committed | 31 m | not budgeted |
| 13:16 | `b0790e7` | plan review in the repo (D-5 to D-10) | 15 m | not budgeted |
| 13:26 | `31e782b` | 0 scaffold; switch to one message per job (D-12 to D-14) | 10 m | 15 m |
| 13:29 | `c663d09` | 1 domain rules | 3 m* | 25 m |
| 13:40 | `2d6d5db` | 2 schema | 11 m | 20 m |
| 13:53 | `b47b6e3` | 3 API | 13 m | 35 m |
| 14:12 | `e1f3485` | 4 worker + fetcher | 19 m | 40 m |
| 14:35 | `cd860f6` | 8 UI | 23 m | 35 m |
| 14:47 | `a537f5c` | 7 Docker | 12 m | 15 m |
| 15:03 | `7a8466a` | 5 retry / DLQ | 16 m | 10 m |
| 15:18 | `7eb2ca2` | spec-checklist gaps (D-25) | 15 m | — |
| 15:24 | `869d29b` | job summary (D-26) | 6 m | — |
| 15:28 | `1eb52d2` | README submission sections (D-27) | 4 m | — |
| 15:36 | `ec99c10` | retry fix found by acceptance (D-28) | 8 m | — |
| 15:43 | `c4e6be2` | retry fix found by acceptance (D-29) | 7 m | — |
| 15:59 | `6053354` | walkthrough recorded, plan closed | 16 m | — |

\* Probably written mostly while slice 0 waited for approval; read slices 0 and 1 together as 13 m against 40 m.

**Slices 1–8 took about 97 minutes against the 180 minutes estimated.** D-14 had projected the must-haves to finish about 20 minutes late. They finished at 15:03, roughly 50 minutes ahead of the 3:55 UI checkpoint.

## 3. Findings

Each finding gives what happened, the evidence, the impact, and what to do next time.

### F1. Setup and plan review weren't in the schedule, and the arithmetic was wrong
- **What happened:** the first 30 minutes (12:00–12:30) were budgeted and used for spec review and planning in Cowork. The schedule then assumed coding from 12:30. It had no line for creating the repo, installing missing prerequisites (F6), or reviewing the plan a second time once it was in the repo (`b0790e7`, 13:16). The plan also put the must-have total at "≈ 3 h 35 m", but the slices summed to 245 m (D-5; PLAN.md later restates the original as 235 m). Checkpoints were set three times in about 25 minutes (D-5, then D-10, then D-14).
- **Evidence:** D-5, D-10, D-12 ("slice 0 finished at 1:18, 33 m late (the plan review used the start of the window)"), D-14.
- **Impact:** slice 0 was "33 m late" because of work the plan never listed, not because building was slow. That number drove the early cuts in F2.
- **Next time:** give the slice table explicit rows for "environment setup" and "plan review in the repo", and project checkpoints from the actual time building starts, not the time the plan assumed. Moving from a planning tool (Cowork) into the repo is a handoff that invites a second review, so budget it. Add up the estimate column (a spreadsheet or a one-line script) instead of summing by eye, and set one rule for checkpoints ("projected from the actual end of each slice") rather than rebaselining by hand.

### F2. The cuts came too early and were never undone
- **What happened:** at 13:26, facing a projected 20 to 48 minute shortfall, two cuts were made: the Docker tests for duplicate delivery and the fault consumer (D-12), and the switch to one message per job (D-13). After that, every slice but one came in well under its estimate. When the work got ahead, one small `FailAsync` test was restored (D-24). The rest of D-12 stayed cut, and the spare time went to docs.
- **Evidence:** the timeline above; D-12, D-24 ("We were ahead of the D-14 checkpoints, so one `FailAsync` test … went back in").
- **Impact:** the riskiest code, the retry and dead-letter path, shipped with no automated end-to-end test (see F3).
- **Next time:** recalibrate after the first two slices: multiply the remaining estimates by the observed ratio of actual to estimated time. Keep a **restore order** next to the cut order, and work through it at every checkpoint where you are ahead. A deferred cut costs nothing if it turns out not to be needed. A test cut too early may never come back.

### F3. The tests that were cut covered the code that broke
- **What happened:** `TransientErrors` shipped with unit tests built from *assumed* exception shapes (an `NpgsqlException`, or a `DbUpdateException` wrapping one). The manual outage check during acceptance, stopping Postgres mid-crawl, then found two shapes the filter missed:
  1. EF's default execution strategy wraps the error in `InvalidOperationException` outside `SaveChanges`. The job was dead-lettered at once and stayed Running (`ec99c10`, D-28).
  2. Npgsql throws an unwrapped `SocketException` when the `db` hostname doesn't resolve, and the 10-second retry window was shorter than a Postgres restart (`c4e6be2`, D-29).
- **Evidence:** D-24 (the original filter), D-28, D-29. Both fixes landed after the README had been written.
- **Impact:** 15 minutes of fixes at 15:36–15:43. The 1-minute retry window and the broad `SocketException` rule were chosen under time pressure, and the reviewer guide §5.9 now flags that rule as too broad.
- **Next time:** as soon as a retry filter exists, write one fault-injection test: a Testcontainers Postgres that is stopped and restarted mid-crawl, asserting the job ends Completed. Build the exception-shape unit tests from exceptions **observed** in that test, not from documentation. Treat "retry and DLQ behavior" as a must-test, not a cuttable one.

### F4. The docs were written before acceptance
- **What happened:** the README's submission sections landed at 15:28 (`1eb52d2`, D-27, moved forward to start "early"). The acceptance run then found the two bugs in F3. Each fix had to update the README's retry and limitations sections, and the last commit added the walkthrough.
- **Evidence:** `ec99c10`, `c4e6be2` and `6053354` all touch README.md.
- **Impact:** small, but it was rework in the most time-critical 30 minutes, and there was a risk that the README and the code disagree at freeze.
- **Next time:** run the acceptance checklist (clean clone, outage, walkthrough) **before** the write-up, and keep the docs slot after it. If the docs must start early, write only the parts that acceptance can't change (architecture, schema, decisions), and leave retry, limitations and verification for last.

### F5. Spec-checklist gaps were found late
- **What happened:** at 15:18–15:24 a pass over the spec checklist found that the job ID wasn't shown on the details page, the button wasn't labeled "Start Crawl", a page with only ignored-scheme links had no test, a non-HTML start page ended Completed with an empty tree (D-25), and the "summary" was undefined (D-26).
- **Evidence:** `7eb2ca2`, `869d29b`.
- **Impact:** two extra commits, each with a design decision, in the last 45 minutes. They were caught in time, but only because there was spare time from F2.
- **Next time:** during planning, map every line of the spec's checklist to a slice and a test in PLAN.md, and add a "spec trace" column to the slice table. Undefined terms (like "summary") go in the open-questions list with a default from the start.

### F6. Environment setup ran inside the timebox
- **What happened:** the stack wasn't known until the spec arrived, and several prerequisites weren't on the machine. The .NET 8 SDK, Node, Docker Desktop and its images had to be installed inside the window, before any code. More came up mid-build. The installed .NET 10 SDK would have overridden 8 (D-11). Windows Smart App Control began blocking freshly built DLLs mid-session, which moved the whole dev loop into the SDK container (D-17). npm ended up running in a `node:20-alpine` container (D-21). `npm audit` on the first UI install reported critical advisories, forcing a move to new majors (Vite 7, react-router 7, Vitest 4) in the middle of the UI slice (D-21). npm 10's resolver fails on a fresh install of the tree (D-21).
- **Evidence:** D-11, D-17, D-21; the installs before 13:01 are from Jim's account.
- **Impact:** most of the unbudgeted time in F1, more unplanned time in slices 0, 2 and 8, and a permanent extra step in the dev loop (the container helper).
- **Next time:** a timed assessment's stack can't be pre-flighted, but the machine can be ready for the likely ones:
  1. **Keep a baseline machine between assessments**, whatever the spec: Docker Desktop installed and running, current LTS .NET and Node SDKs, common images (Postgres, RabbitMQ, Redis) pulled. Check that OS policies like Smart App Control allow locally built binaries to run.
  2. **Start long installs in the background** as soon as planning picks the stack, while the plan is still being drafted, rather than after it.
  3. **Audit dependencies as part of scaffold** (`npm audit`, `dotnet list package --vulnerable`), and record the chosen versions in the plan, so no slice picks versions on the clock.

### F7. Effort went into hardening a design that was then dropped
- **What happened:** the plan drafted in Cowork made one message per page the primary design (D-2). The second review, in the repo at 13:16, spent its time hardening it: a `Fault<CrawlPage>` consumer as a must-have (D-7), and counters for a per-page completion race. Ten minutes later D-13 switched to one message per job, which the plan had listed only as the fallback. D-7 had to be amended.
- **Evidence:** `b0790e7` (D-7, D-9), `31e782b` (D-13 "supersedes D-2; amends D-7").
- **Impact:** part of the 15-minute review went to a design that was never built, and D-13 had to be explained in the write-up. The final design was the better fit (see [CRAWL_FRONTIER.md §5](CRAWL_FRONTIER.md)).
- **Next time:** under a timebox, plan the **simplest design that meets the rubric** as primary and the ambitious one as a stretch slice, not the other way round. Review effort goes to the path most likely to ship.

### F8. Commit hygiene
- **What happened:** the commit messages are strong: each says why and cites its D-n. A few commits mix unrelated concerns:
  - `31e782b` combines the scaffold and an architecture change (message per job), and its subject line has to say both.
  - `2d6d5db` combines the schema and a dev-environment change (D-17, checks run in the SDK container).
  - `e1f3485` (about 1,000 lines) puts the fetcher, the crawl store, the crawler and their tests in one commit, though the fetcher stands alone with its stub-handler tests.
- **Impact:** harder to review, revert or bisect a single concern. With approval at every commit (D-3), a big commit is also a big review under time pressure.
- **Next time:** keep one concern per commit. Record a decision that changes the plan in its own commit (PLAN.md + DECISIONS.md), separate from the code that follows. Split a large slice at its natural seam (here, the fetcher first, then the crawler).

### F9. Items that arguably should not have been cuttable
The reviewer guide covers the code-level debt. Two planning calls stand out:
- **SSRF guard (slice 12, cut):** the service fetches any URL a user types, from inside the compose network. A simple block on loopback, private and link-local addresses was probably 10–15 minutes, and it was cut alongside nice-to-haves like SSE. It belonged with the must-haves, or at least in a separate, earlier cut tier than robots.txt and SSE.
- **CI (slice 11, cut):** the plan listed `.github/workflows/ci.yml` from the first commit and estimated it at 10 minutes. With the work about 50 minutes ahead at 15:03, it could have gone in before the docs. A CI run would also have given a second, independent clean-environment check.

The outbox (slice 9) was a reasonable cut: the rubric accepts the gap if documented, and it is documented (D-27).

## 4. What went well (keep doing)
- **Fast start from a cold spec:** from delivery at 12:00 to an approved plan, reviewed in two tools and with the missing prerequisites installed, by 13:16.
- **DECISIONS.md discipline:** 29 entries, each with options, why and trade-off, superseded rather than rewritten. A reviewer can see why every cut was made and when.
- **Commit messages that say why**, tied to decision numbers.
- **A real acceptance run on a clean clone**, including fault injection (DB outage, worker stop). It found two real bugs that no test had caught. This is the step that saved the submission.
- **Design for at-least-once delivery from the start:** conditional writes, a database frontier, and a job row lock made redelivery safe without extra machinery (D-13, D-20).
- **Taking the cheap reversible fallback early:** D-13 was the right call even with hindsight, and taking it before slices 2–3 avoided building and discarding the per-page path.
- **Dependency advisories handled deliberately** (D-15, D-21), including declining a release too new to trust.
- **Code freeze honored:** the last commit was at 15:59.

## 5. Checklist for next time
1. Keep a baseline dev machine ready between assessments, start long installs in the background as soon as the stack is chosen, and audit dependencies at scaffold (F6).
2. Budget environment setup and the in-repo plan review as rows in the plan, project checkpoints from when building actually starts, and add up estimates mechanically (F1).
3. Make the simplest design that meets the rubric primary; make the ambitious one a stretch (F7).
4. Trace every spec checklist item to a slice and a test during planning (F5).
5. Recalibrate estimates after two slices; keep a restore order beside the cut order and use it when ahead (F2).
6. Never cut the test for the failure path you are relying on. Write a fault-injection test as soon as the retry logic exists, and build exception filters from observed exceptions (F3).
7. Run acceptance before writing the docs that it can invalidate (F4).
8. One concern per commit; plan changes get their own commit (F8).
9. Put security basics (SSRF) and cheap safety nets (CI) in a cut tier above nice-to-haves (F9).
