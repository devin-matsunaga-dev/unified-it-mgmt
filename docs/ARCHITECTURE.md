# ARCHITECTURE.md — Unified IT Management System

> Read this fully before writing any code. Do not deviate from it without asking. If a work package forces a change here, updating this file is part of that package.

## 1. What this system is

A unified IT management platform: **Helpdesk (ITSM)** + **Asset Management (ITAM/CMDB)** + **Network Monitoring (NMS)**, with cross-module correlation. The CMDB is the backbone: tickets, alerts, and monitored devices all reference the same CI (Configuration Item) records.

## 2. Topology

**Modular monolith** in one ASP.NET Core host, plus satellite services, all orchestrated by .NET Aspire (AppHost project).

| Component | Tech | Role |
|---|---|---|
| `Web.Host` | ASP.NET Core | Hosts all module APIs + SignalR hubs |
| `Modules.Helpdesk` | class lib | Tickets, SLA, KB, queues, portal API |
| `Modules.Assets` | class lib | CMDB, lifecycle, licensing, contracts |
| `Modules.Monitoring` | class lib | Device/check config, alert engine, dashboards API |
| `Platform` | class lib | Auth, audit, notifications, scheduler, outbox, vault |
| `Contracts` | class lib | Event + shared DTO definitions (the ONLY cross-module types) |
| `Poller` | Python (asyncio) | SNMP/ICMP/HTTP polling; publishes telemetry |
| `Discovery` | Python | Subnet scans, LLDP/CDP, inventory (Phase 4+) |
| `Web` | React (Vite + TS) | SPA frontend |

Correlation engine starts as a consumer inside `Modules.Monitoring`; it may be extracted to its own worker later — do not pre-extract.

## 3. Module boundaries (hard rules)

- A module NEVER queries another module's tables. Cross-module reads go through that module's public service interface; cross-module reactions go through **events**.
- `Contracts` holds events and shared value objects only — no entities, no EF types.
- Each module owns its own Postgres **schema** (`helpdesk`, `assets`, `monitoring`, `platform`) and its own EF migrations.
- Ownership map: Tickets/SLA/KB → Helpdesk. CIs/relationships/licenses/contracts → Assets. Devices/checks/alerts/metrics → Monitoring. Auth/audit/notifications/scheduler/outbox/credential-vault → Platform.
- Ticket↔CI links are owned by Helpdesk (it stores CI ids); it renders CI context via the Assets service interface.
- When two modules read each other, neither can hold a project reference to the other. The read interface then lives in `Platform/Integration` as a **port**, and the owning module implements it (`ICiDirectory` → Assets, `ITicketLinkDirectory` → Helpdesk). A port is a narrow read surface only — never a write path, and never a substitute for an event.

## 4. Communication

- **In-process:** direct service interfaces (no MediatR ceremony unless already established).
- **Cross-service / async:** MassTransit over RabbitMQ. ALL publishes go through the **EF transactional outbox** — never publish directly from application code.
- **Consumers must be idempotent.** Use the Platform dedupe helper with a deterministic key (e.g. `alert:{deviceId}:{ruleId}`).
- Python services: `aio-pika`. Pollers have **publish-only** credentials plus one read-only config queue. Pollers never consume commands.
- **Real-time to browser:** SignalR hubs in `Web.Host`, Redis backplane.

## 5. Data stores

| Store | Used for | Not for |
|---|---|---|
| PostgreSQL | All relational data, per-module schemas; full-text search | — |
| TimescaleDB (same PG) | Metric hypertables, continuous aggregates (raw 30d, 5-min 1y) | Relational data |
| Redis | Alert state machines, SignalR backplane, dedupe/rate-limit state, cache | Source-of-truth data (must survive Redis flush) |
| MinIO (S3) | Attachments, generated PDFs | Anything queryable |
| RabbitMQ | Transport only | Storage — messages are transient; outbox is the durability layer |

Dependency graph = `assets.ci_relationships` table + recursive CTEs. No graph database.

## 6. AuthN/AuthZ

- OIDC via Keycloak in dev; **must swap to Entra ID by configuration only** — never reference Keycloak-specific claims in module code; map claims in one Platform location.
- Roles: `Admin`, `Technician`, `Manager`, `EndUser`. Authorize with policy names (e.g. `CanManageTickets`), not raw role strings, at API endpoints.
- EndUsers see only their own tickets/assets — enforced in queries, not just UI.

## 7. Invariants (never break these)

1. Every write endpoint produces an **audit** entry (who, what, before/after, correlation id).
2. Every event publish goes through the **outbox**; every consumer is **idempotent**.
3. **Credential vault** material is write-only: no API ever returns secret values; secrets are encrypted at rest; every access is audited.
4. Automation is bounded: alert→ticket has dedupe + rate limit + circuit breaker; runbooks are server-side allowlisted (no free-text execution path exists anywhere).
5. All input validated at the API edge; EF parameterization only (no raw interpolated SQL).
6. One dead device/target never blocks a polling cycle.
7. Migrations are append-only — never edit an applied migration.

## 8. Versions & environments

**Pinned platform versions (LTS/latest-supported only — see WORKFLOW.md table):** .NET 10 LTS · Aspire 13.x (latest; `aspire update` at phase gates) · Node 24 LTS · Python 3.13+ · React 19 + latest Vite · PostgreSQL newest-Timescale-supported major · Ubuntu LTS. Never scaffold or pull base images for EOL versions (net8.0/net9.0, Node ≤22, Python ≤3.12).

- **Dev:** WSL2 (Ubuntu LTS), everything via Aspire AppHost (`aspire run`), MailHog for SMTP, snmpsim for devices (two containers: `snmpsim` serves the healthy and degraded profiles, `snmpsim-downable` serves one device that can be stopped on its own), `http-target` (nginx) as a mock HTTP service whose page this repository owns.
- **Prod:** Linux VM, Docker Compose from CI-built images, Caddy/Nginx TLS front, Entra ID, real SMTP. No building on the prod box; dev-only containers excluded from prod profile.

## 9. Deferred on purpose (do not add unprompted)

Microservice extraction, graph DB, Elasticsearch, Kubernetes, mTLS between services, MediatR/CQRS ceremony, per-field encryption. These have decision records; revisit only via a work package.
