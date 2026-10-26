# SENTINEL — Phase 1 Plan

**Version:** 0.4 (architecture baseline, pending operational validation)
**Date:** 2026-10-26
**Status:** Planning completed for development kickoff; deployment feasibility and final API details remain validation gates.

## 1. Product scope

SENTINEL v1.0 is a **personal developer utility** to monitor real HTTP/HTTPS applications and APIs, starting with FORMA. It is intended to support future multi-service e-commerce projects but is **not** a public multi-tenant SaaS in v1.0.

Clients: a separately developed **React + TypeScript** dashboard and a **C#/.NET CLI**. Both consume an **ASP.NET Core REST API**. Monitoring runs independently through a **.NET Background Worker** and persists data in **PostgreSQL / EF Core**.

V1 includes: monitor configuration; scheduled and on-demand HTTP checks; persistent results; incident detection/recovery; 24h/7d/30d analytics; Telegram alerts; secured dashboard and CLI; deployment, logging, backup, and tests.

V1 excludes: public signup, teams, subscriptions, multiple probing regions, host-level agents, distributed tracing, and prediction/AI.

## 2. Architecture

```text
Browser -> HTTPS reverse proxy -> React static application / ASP.NET Core API
CLI ---------------------------> authenticated REST API
                                       |
                               Application + Domain
                                       |
                                   PostgreSQL
                                       ^
                              .NET Background Worker
                                |              |
                            HTTP checks   Telegram API
```

Use a pragmatic modular monolith: one solution and shared domain/application logic, **separate API and Worker processes**. Neither the CLI nor dashboard accesses the database directly. APIs should be versioned under `/api/v1` and documented with OpenAPI.

Suggested solution projects:

```text
src/Sentinel.Api
src/Sentinel.Worker
src/Sentinel.Cli
src/Sentinel.Domain
src/Sentinel.Application
src/Sentinel.Infrastructure
src/Sentinel.Dashboard    # React + TypeScript
tests/Sentinel.UnitTests
tests/Sentinel.IntegrationTests
tests/Sentinel.EndToEndTests
docs/
deploy/
```

## 3. Data model

| Entity | Key fields | Rules |
|---|---|---|
| ApplicationUser (Identity) | Id, Email | One personal owner initially; no open public registration |
| Monitor | Id, OwnerId, Slug, Url, IntervalSeconds, TimeoutSeconds, IsEnabled, NextCheckAt, CreatedAt | Owner+Slug unique; URL security validation; disable retains history |
| CheckResult | Id, MonitorId, CheckedAt, HttpStatusCode?, DurationMs?, Outcome, FailureCategory?, ErrorSummary? | Append-oriented; UTC timestamps; distinguish DNS/timeout/TLS/HTTP; indexes on MonitorId+CheckedAt |
| Incident | Id, MonitorId, StartedAt, ResolvedAt?, Status, Reason | At most one open incident per monitor; retain history |
| Notification | Id, MonitorId, IncidentId, EventType, DeliveryStatus, Attempts, CreatedAt, SentAt? | Track delivery and retries; deduplicate by event identity |
| PersonalAccessToken | Id, UserId, TokenHash, Label, Scopes, ExpiresAt, RevokedAt?, LastUsedAt? | Show secret once; store hash; revoke/expire; least privilege |

Identity additionally owns its standard authentication and supporting tables. Application-specific ID and migration details will be verified when implementing the first migration. Consider optimistic concurrency and a database lease/reservation for scheduling.

**Scheduling:** single active worker in v1 deployment. Claim due checks to prevent duplicates; bound concurrency; persist outcomes and next schedule; on restart do not invent historical checks or create a storm of catch-up tasks. Expose worker heartbeat/stale state.

## 4. Proposed default monitoring policy

- HTTP/HTTPS only; 60-second interval, 10-second timeout.
- Limit: 20 monitors and 5 simultaneous checks for MVP; both configurable.
- DOWN after 3 consecutive failed checks; recovered after 2 consecutive successes.
- HEALTHY, DEGRADED, DOWN, UNKNOWN states. UNKNOWN is used for missing/stale results.
- 30-day retention of raw checks in v1; aggregated historic periods: 24h, 7d, 30d.
- Separate request result, incident state, and notification delivery state.
- Availability is an estimate over eligible observed checks, with missing-data periods visible. Do not claim continuous availability from sparse samples.
- No overly aggressive automatic HTTP retries that mask incidents; cold starts can count as slow/degraded based on documented thresholds.

## 5. REST API sketch

Base: `/api/v1`. Path IDs are UUIDs; CLI resolves a monitor slug through list/search or a future slug route.

| Method | Route | Purpose |
|---|---|---|
| GET, POST | `/monitors` | List/create monitors |
| GET, PUT, DELETE | `/monitors/{id}` | Read/update/delete (defined data-retention semantics) |
| POST | `/monitors/{id}/check` | Request an asynchronous on-demand check; rate-limited |
| GET | `/monitors/{id}/checks` | Paginated check history |
| GET | `/monitors/{id}/stats?period=24h` | Aggregated stats with completeness metadata |
| GET | `/incidents` | Filtered, paginated incidents |
| GET | `/incidents/{id}` | Incident detail |
| GET | `/overview` | Dashboard summary |
| POST | `/auth/login` | Create authenticated browser session |
| POST | `/auth/logout` | End browser session |
| GET | `/auth/me` | Current authenticated user |
| GET, POST | `/tokens` | Token metadata / create token (show raw secret once) |
| DELETE | `/tokens/{id}` | Revoke token |

Use explicit request/response DTOs, validation, consistent Problem Details errors, pagination and UTC ISO-8601 timestamps. `POST .../check` should return an accepted job/reference rather than pretend the check has already completed. Complete auth route design only after reviewing Identity's supported APIs and CSRF flow.

### CLI baseline

```text
sentinel status forma   # shortcut: sentinel forma
sentinel list
sentinel history forma --days 7
sentinel incidents forma
sentinel check forma
sentinel status forma --json
sentinel watch forma
```

Default status commands show latest **persisted** observation and its timestamp; `check` requests a new observation. For machine use, standardize JSON fields and exit codes, and keep secrets out of arguments/history/logs.

## 6. Authentication and security

- **Dashboard:** ASP.NET Core Identity, secured HttpOnly/Secure cookies; appropriate SameSite policy and CSRF defenses for state-changing requests. Prefer frontend and API behind one HTTPS origin/reverse proxy, despite separate code/deploy artifacts.
- **CLI:** revocable, scoped, expiring PATs; strong random raw tokens issued once; only hashed values in database; encrypted/OS-protected local credential storage where supported.
- Enforce owner authorization on every monitor, result, incident, and token route.
- No public registration in v1; safely provision initial personal account.
- HTTP monitor URL security: prevent SSRF, localhost, link-local, private/metadata IPs, DNS rebinding, suspicious redirects, credential URLs and unsafe schemes. Validate each resolved destination/connection and cap response body and redirect behavior.
- Rate-limit login, token creation, and ad-hoc checks; bound worker concurrency, requests and outgoing traffic.
- HTTPS and minimal exposed ports; PostgreSQL private; secret injection rather than source control; scrub credentials and sensitive URLs in logs.
- Backup and restore test; audit logs for security-sensitive changes.

## 7. Alerts

**Selected:** Telegram Bot. Trigger on incident opening and recovery, not every failed check. Deduplicate notifications, store delivery state, retry with exponential backoff and bounded attempts, never leak tokens or credentials into messages. Bot token and destination chat ID are configured as secrets. Incident detection and alert delivery remain separate, so a Telegram outage must not corrupt incident state.

## 8. Hosting evaluation (2026-10-26)

**Budget:** preferably zero. **Development:** local Docker Compose. **Production-first candidate:** Oracle Cloud Always Free Linux VPS running reverse proxy, React assets, API, Worker, PostgreSQL with internal network and volumes.

| Option | Assessment | Decision |
|---|---|---|
| Oracle Cloud Always Free VM | Eligible always-free compute can run continuous processes and DB; capacity not guaranteed, idle instance reclaim possible; owner maintains system | First proof-of-feasibility candidate |
| Railway Free | $1/month recurring usage credit after trial; likely insufficient for all persistent services without measurement | Prototype/fallback evaluation only |
| Render Free | Web spins down; no free dedicated background workers; free PostgreSQL expires after 30 days | Not acceptable for v1 continuous monitoring |
| Paid VPS/managed | More predictable if free options cannot support reliable service | Reevaluate if free verification fails |

Official references (checked 2026-10-26):

- Oracle Always Free: https://docs.oracle.com/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm
- Oracle Free Tier: https://www.oracle.com/cloud/free/
- Railway pricing: https://railway.com/pricing
- Render free limitations: https://render.com/docs/free
- Render background workers: https://render.com/docs/background-workers

**Deployment gate:** verify account eligibility, capacity in selected home region, resource limits, architecture compatibility (Arm64 if Ampere), DNS/HTTPS, database persistence, network egress, restart behavior, actual uptime, and backup restoration. Free ≠ guaranteed. Do not introduce paid resources without explicit approval and budget check. Place an independent external check on SENTINEL itself.

## 9. Roadmap: proposed 32 commits

**Phase A — Foundation (1–4)**
1. Initialize repository/solution and docs.
2. API and layered projects.
3. PostgreSQL/EF migrations.
4. Local Docker Compose/config/logging.

**Phase B — Identity (5–8)**
5. Identity and personal bootstrap.
6. Secure browser sessions and CSRF protections.
7. PAT issuance/validation/revocation.
8. Authorization/security tests.

**Phase C — Monitoring (9–13)**
9. Monitor CRUD and SSRF-aware validation.
10. HTTP executor, timeout, outcome taxonomy.
11. Worker scheduling and persistence.
12. Check results and concurrency coordination.
13. History/statistics and integration tests.

**Phase D — Incidents/alerts (14–17)**
14. Incident transitions.
15. Recovery transitions.
16. Telegram notification delivery.
17. Incident/alert failure tests and deduplication.

**Phase E — CLI (18–21)**
18. CLI bootstrap and auth.
19. List and status.
20. History, incidents and check.
21. JSON, exit codes, tests and packaging.

**Phase F — Dashboard (22–26)**
22. React/TypeScript bootstrap and client.
23. Login/session UI.
24. Overview/list UI.
25. Monitor details and analytics.
26. Incident and monitor management UI.

**Phase G — Hardening (27–30)**
27. Rate limits/SSRF/security review.
28. Worker restart recovery and health checks.
29. Retention, indexing, backup/restore.
30. End-to-end and reliability tests.

**Phase H — Release (31–32)**
31. Production deployment and operational validation.
32. Monitor FORMA, README/runbook and v1.0 release.

Commit count is an organizing tool, not an artificial cap. Review each functionality before commit; retain the 50/50 AI-assisted coding approach with code explanation and user participation.

## 10. Acceptance criteria for v1

- Login works from multiple devices; unauthenticated requests are rejected.
- Owner can configure FORMA and at least one other URL; forbidden destinations remain blocked.
- Worker checks continuously without any open browser; results and incidents survive restart.
- Down/recovery rules work, Telegram notification is delivered without repeated spam.
- CLI status and JSON report real persisted checks, including timestamps and stale/unknown cases.
- Dashboard accurately displays monitor state, history, incidents and missing-data periods.
- Unit, integration, security and E2E checks pass; reproducible deployment and tested restore exist.

## 11. Remaining operational gates

1. Validate an actual Oracle Always Free instance (or substitute provider) before production promise.
2. Choose a domain/HTTPS hostname strategy and backup destination.
3. Finalize endpoint DTOs, data retention/delete behavior, token scopes, UI specifics, and Telegram message language during implementation design reviews.
4. Confirm owner has a Telegram destination and can create bot credentials when alerts are implemented; do not collect bot tokens in project documentation.

**Phase 1 result:** architecture and MVP baseline accepted for implementation planning. **Production readiness is not claimed.**
