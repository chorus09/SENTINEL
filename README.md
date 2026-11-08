# SENTINEL

**Monitor. Detect. Understand.**

SENTINEL is a backend-first monitoring and observability platform built with C# and .NET.

The project aims to help developers monitor HTTP services, detect incidents, inspect historical performance, and receive alerts through a web dashboard and CLI.

## Project Goals

- Monitor HTTP/HTTPS applications and APIs.
- Detect downtime, errors, and performance degradation.
- Store monitoring history and incident records.
- Send Telegram notifications for incidents and recovery.
- Provide a React dashboard and a .NET command-line interface.
- Build a reliable and extensible backend architecture.

## Planned Technology Stack

- Backend: ASP.NET Core (.NET 10)
- Frontend: React + TypeScript
- Database: PostgreSQL + Entity Framework Core
- Background Processing: .NET Worker Service
- Authentication: ASP.NET Core Identity
- CLI: C# / .NET
- Deployment: Docker
- Testing: xUnit and integration tests

## Project Structure

```text
src/       Application source code
tests/     Automated tests
docs/      Architecture and project documentation
deploy/    Deployment configurations
```

## Status

Currently in development — Foundation & Infrastructure.

## Development Approach

This project follows an AI-assisted development workflow with code reviews, automated tests, and incremental commits.

## Roadmap

See the project planning documentation in `docs/` for the detailed development roadmap.

## License

No license selected yet.

## Monitoring worker

The HTTP monitoring worker is disabled by default. Apply the `AddMonitorLeases`
EF Core migration through the approved deployment process before enabling it.
The API does not apply migrations at startup.

Set `MonitoringWorker__Enabled=true` on one intended API instance after the
migration succeeds. The default settings are a 5 second poll interval, up to 5
concurrent checks, a batch size of 10 and a 60 second lease. They can be changed
with `MonitoringWorker__PollIntervalSeconds`, `MonitoringWorker__MaxConcurrentChecks`,
`MonitoringWorker__BatchSize` and `MonitoringWorker__LeaseDurationSeconds`.
Keep the lease longer than the maximum 30 second check timeout plus a safety margin.

When running multiple API replicas, enable the worker only on the intended
replicas. PostgreSQL leases coordinate enabled replicas, but a process failure
may cause a check to run again after lease expiry. Check outcomes are logged;
history storage is not part of this feature.
