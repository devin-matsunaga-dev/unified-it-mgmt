# Unified IT Management System — Feature Specification

**Modules:** Helpdesk (ITSM) · Asset Management (ITAM/CMDB) · Network Monitoring (NMS)
**Stack:** ASP.NET Core · React · Python · Docker · WSL · .NET Aspire

---

## 1. Vision & Architecture Principles

- **Single source of truth:** The CMDB (asset database) is the backbone. Tickets, alerts, and monitored devices all reference the same asset/CI records.
- **Event-driven core:** Modules communicate over a message bus so correlation, automation, and notifications react to anything happening anywhere in the system.
- **Modular monolith or microservices via Aspire:** Each module is a separately deployable service (or vertical slice) orchestrated by .NET Aspire, sharing contracts through a common library.
- **API-first:** Every feature available in the UI is available via REST (and optionally SignalR/WebSocket for real-time).

---

## 2. Module A — Helpdesk / Service Desk (ITSM)

### Core

| Feature | Description |
|---|---|
| Ticket management | Create/read/update/close tickets with type (Incident, Service Request, Problem, Change), priority, urgency, impact, computed priority matrix |
| Ticket lifecycle & workflow | Configurable statuses and transitions (New → Triage → In Progress → Pending → Resolved → Closed), guarded transitions with required fields |
| Queues & assignment | Team queues, round-robin/load-based auto-assignment, manual assignment, reassignment history |
| SLA engine | SLA policies per priority/customer/category; response & resolution targets; pause on "pending customer"; breach warnings and escalation chains |
| Email-to-ticket | Inbound mailbox ingestion (IMAP/Graph API), reply threading, outbound notifications with reply parsing |
| Self-service portal | End-user portal: submit requests, track status, comment, approve, search KB |
| Knowledge base | Articles with categories, versioning, draft/publish workflow, article suggestions while typing a ticket |
| Comments & worklogs | Public replies vs. internal notes, time tracking per worklog entry |
| Attachments | File uploads on tickets/comments with virus-scan hook and size/type policies |
| Categories & forms | Category tree with dynamic custom fields per category (form builder) |
| Approvals | Multi-step approval flows for service requests and changes |
| Canned responses & templates | Reusable reply snippets and ticket templates |
| Escalations | Time-based and event-based escalation rules |
| CSAT | Post-resolution satisfaction survey with reporting |
| Search & filters | Full-text search, saved views/filters per user and team |

### Advanced

- **Problem management:** link many incidents to one problem; known-error database.
- **Change management:** change calendar, risk assessment, CAB approvals, rollback plans, freeze windows.
- **Recurring/scheduled tickets** for maintenance tasks.
- **Merge/split tickets**, parent-child relationships, and linked tickets.

---

## 3. Module B — Asset Management (ITAM + CMDB)

### Core

| Feature | Description |
|---|---|
| CI/asset registry | Hardware, software, virtual, cloud, and logical CIs with flexible type-specific schemas |
| Asset lifecycle | States: Ordered → In Stock → Deployed → In Repair → Retired → Disposed, with history |
| Automated discovery | Agentless network discovery (SNMP, WMI/WinRM, SSH) plus optional lightweight agent for endpoint inventory |
| Relationship mapping | CI-to-CI relationships (runs-on, connects-to, depends-on, hosted-on) forming a dependency graph |
| Ownership & assignment | Assign assets to users, departments, locations; check-in/check-out |
| Software inventory & licensing | Installed software detection, license pools, compliance (installed vs. entitled), expiry alerts |
| Warranty & contracts | Warranty tracking, support contracts, vendor records, renewal reminders |
| Procurement | Purchase orders, cost tracking, depreciation schedules |
| Barcode/QR support | Label generation and mobile-friendly scan-to-lookup |
| Audit & reconciliation | Physical audit workflows, discovered-vs-recorded drift reports |
| Custom fields & types | User-defined CI types and attributes |
| Import/export | CSV/Excel import wizard with mapping and dedupe; bulk edits |

### Advanced

- **Normalization catalog:** map raw discovered software names to canonical products/versions.
- **Cloud connectors:** pull inventory from Azure/AWS/M365/Intune.
- **Stock/consumables management** (cables, peripherals, toner) with reorder thresholds.
- **Configuration baselines & drift detection** for critical CIs.

---

## 4. Module C — Network Monitoring (NMS)

### Core

| Feature | Description |
|---|---|
| Device polling | ICMP up/down, SNMP v2c/v3 metric polling (CPU, memory, temperature), configurable intervals |
| Interface monitoring | Bandwidth in/out, errors, discards, utilization %, status per interface |
| Service checks | TCP port, HTTP(S) with content match, DNS, certificate expiry, custom script checks |
| Agent-based checks | Optional agent for disk, services/processes, event logs on servers |
| SNMP traps & syslog | Trap receiver and syslog collector with parsing rules |
| Alerting engine | Threshold + state-change alerts, multi-condition rules, flap suppression, hysteresis, maintenance windows |
| Notification channels | Email, Teams/Slack webhooks, SMS gateway, push; per-schedule routing and on-call rotations |
| Dashboards | Real-time status boards, top-N talkers, custom widget dashboards |
| Metric storage | Time-series storage with retention/downsampling policies |
| Network maps | Auto-generated topology (LLDP/CDP + subnet scans) and manual maps with live status overlay |
| Availability reports | Uptime %, MTTR/MTBF per device and service |
| Discovery scheduling | Recurring subnet scans that feed new devices into the CMDB (see §5) |

### Advanced

- **NetFlow/sFlow collection** for traffic analysis.
- **Configuration backup** for network devices (SSH-based, diff between versions, change alerts).
- **Anomaly detection:** baseline-vs-actual deviation alerts (Python/ML service).
- **Distributed pollers:** remote probe containers for multiple sites, reporting to the central bus.

---

## 5. Unified / Cross-Module Features (the differentiators)

### 5.1 Correlation & Automation

| Feature | Description |
|---|---|
| Alert-to-ticket automation | Monitoring alerts auto-create tickets with dedupe (one ticket per alert storm), auto-resolve ticket when alert clears |
| Event correlation engine | Rules + topology-aware correlation: if a core switch goes down, suppress downstream device alerts and open **one** root-cause ticket listing affected CIs |
| Impact analysis | Using the CMDB dependency graph: "this server hosts these services used by these departments" — shown on alerts and change requests |
| Blast-radius preview | Before approving a change on a CI, show every dependent CI, open ticket, and active SLA that could be affected |
| Ticket ↔ asset linking | Attach CIs to tickets; asset page shows full ticket history; ticket page shows asset health & recent alerts inline |
| Alert enrichment | Alerts carry asset context automatically: owner, location, warranty status, assigned tech, related open tickets |
| Auto-remediation runbooks | Alert triggers a runbook (restart service, clear disk, run script) with results logged to the auto-created ticket; escalate to human only on failure |
| Discovery → CMDB pipeline | Network discovery creates/updates CMDB CIs; unknown devices open a "new device found" review queue |
| Maintenance sync | An approved change request automatically schedules a monitoring maintenance window for the affected CIs |
| Recurring-incident detection | Flag assets generating repeated incidents; auto-suggest opening a problem record |

### 5.2 Unified Experience

- **Global search:** one search box across tickets, assets, devices, alerts, KB, and users.
- **Unified dashboard:** executive view combining open tickets, SLA health, asset compliance, and network status; role-based dashboards for tech vs. manager.
- **360° views:** a user page shows their assets, tickets, and requests; an asset page shows monitoring graphs, tickets, contracts, and relationships.
- **Timeline view per CI:** interleaved history of alerts, tickets, changes, and config diffs on one axis — the "what happened to this thing" view.
- **Unified notification center** with per-user channel and digest preferences.

### 5.3 Intelligence (optional AI layer)

- Ticket auto-categorization, priority suggestion, and duplicate detection.
- KB article suggestions to agents and end users.
- Alert noise scoring and grouping.
- Natural-language reporting queries ("show unresolved P1s on Building B switches").

---

## 6. Platform / Foundation Features

| Feature | Description |
|---|---|
| AuthN/AuthZ | OIDC SSO (Entra ID/Keycloak), local accounts fallback, MFA |
| RBAC | Roles + granular permissions, scoping by team/site/CI class |
| Multi-site / org structure | Locations, departments, teams; data visibility per scope |
| Audit log | Immutable who-did-what-when across all modules |
| Notifications service | Central templated notification service used by all modules |
| Scheduler | Central job scheduler (polls, escalations, reports, discovery) |
| REST API + webhooks | Versioned API, API keys/scopes, outbound webhooks on any event |
| Real-time updates | SignalR push for dashboards, ticket lists, and alert boards |
| Reporting & exports | Report builder, scheduled email reports, CSV/PDF export |
| Data retention | Configurable retention for metrics, logs, closed tickets |
| Backup/restore & health | Built-in backup jobs; self-monitoring via OpenTelemetry |
| Theming & branding | Portal branding, dark mode |
| Localization | i18n framework in UI and notification templates |

---

## 7. Tech Stack Mapping & Suggestions

### Service layout (orchestrated by .NET Aspire)

| Service | Tech | Responsibility |
|---|---|---|
| `Gateway/BFF` | ASP.NET Core (YARP) | Auth, routing, rate limiting |
| `Helpdesk.Api` | ASP.NET Core | Tickets, SLA, KB, approvals |
| `Assets.Api` | ASP.NET Core | CMDB, lifecycle, licensing |
| `Monitoring.Api` | ASP.NET Core | Alert rules, device config, dashboards API |
| `Correlation.Engine` | ASP.NET Core worker | Event correlation, automation rules |
| `Poller` (×N) | **Python** | SNMP/ICMP/HTTP polling, trap/syslog receivers (pysnmp, scapy, asyncio) |
| `Discovery` | **Python** | Subnet scans, LLDP/CDP walk, WMI/SSH inventory |
| `Analytics/ML` | **Python** (FastAPI) | Anomaly detection, ticket NLP |
| `Notifications` | ASP.NET Core worker | Email/Teams/SMS fan-out |
| `Web` | React (Vite + TS) | SPA frontend |

### Infrastructure suggestions

- **Database:** PostgreSQL for relational data (tickets, assets). Use **TimescaleDB extension** for metrics — you get time-series performance without adding a second database engine. (Alternative: VictoriaMetrics/InfluxDB if metric volume gets large.)
- **Message bus:** RabbitMQ (first-class Aspire integration; MassTransit on the .NET side, `aio-pika` in Python). This is the glue for correlation — pollers publish events, the correlation engine and helpdesk subscribe.
- **Cache/real-time backplane:** Redis (SignalR backplane, alert dedupe state, rate limiting).
- **Search:** PostgreSQL full-text to start; move to Meilisearch/Elasticsearch if global search needs grow.
- **Auth:** Keycloak container (or Entra ID) with OIDC; ASP.NET Core `AddAuthentication().AddOpenIdConnect()`.
- **Object storage:** MinIO container for attachments (S3-compatible, keeps you portable).
- **Observability:** OpenTelemetry everywhere — Aspire's dashboard gives you traces/logs/metrics for free during dev; ship to Grafana/Prometheus in prod.

### .NET / Aspire specifics

- Use the **Aspire AppHost** to compose everything: Postgres, Redis, RabbitMQ, Keycloak, MinIO, the Python services (via `AddPythonApp`/container resources), and the React dev server (`AddNpmApp`). One `dotnet run` boots the whole system in WSL.
- **Aspire service discovery + health checks** replace hand-written connection wiring; Python services can consume the injected connection strings via env vars.
- Prefer a **modular monolith first** (one ASP.NET Core host, module-per-assembly, MediatR or plain services) and split into microservices only when a module needs independent scaling — pollers and ML are the natural first splits, and they're already Python.
- **EF Core + Npgsql** with migrations per module schema; consider `ltree`/recursive CTEs for the CI dependency graph before reaching for a graph database.
- **SignalR** hubs for live ticket boards and alert dashboards.
- **Quartz.NET** (or Hangfire) for scheduling; keep Python pollers on their own asyncio schedulers driven by config from `Monitoring.Api`.

### React frontend suggestions

- Vite + TypeScript, TanStack Query for server state, TanStack Table for the many grids, Zustand for light client state.
- A component library that handles dense data well: Mantine or shadcn/ui + TanStack Table.
- Recharts or ECharts for metric graphs; React Flow for topology/dependency maps.
- `@microsoft/signalr` client for live updates.

### Docker / WSL

- Everything containerized; Aspire generates the compose/manifests (`aspire publish` for Compose, `aspire deploy` — now GA — for k8s/AKS later).
- In WSL, run Docker Engine natively (or Docker Desktop WSL backend). Note: **SNMP trap (UDP 162) and syslog (UDP 514) receivers need host networking or explicit UDP port mapping** — test this early, it's a common gotcha.
- Distributed pollers ship as a single self-contained container image configured by environment variables — easy remote-site deployment.

---

## 8. Suggested Build Phases

1. **Foundation:** Auth, RBAC, audit, notifications, Aspire skeleton, Postgres schemas.
2. **Helpdesk core:** tickets, queues, SLA, email-to-ticket, portal, KB.
3. **Assets core:** CI registry, lifecycle, manual entry + CSV import, ticket↔asset linking.
4. **Monitoring core:** Python poller, alerting, dashboards, alert-to-ticket automation. *(First unified win.)*
5. **Discovery + CMDB pipeline:** scans feed assets; topology maps.
6. **Correlation & impact:** dependency graph, root-cause suppression, blast radius, maintenance sync.
7. **Advanced:** licensing compliance, config backup, NetFlow, anomaly detection, AI assists.

---

## 9. Non-Functional Requirements (summary)

- Poll 1,000+ devices at 60s intervals without missed cycles (horizontal poller scale-out).
- Alert-to-ticket latency < 5s end-to-end.
- API p95 < 300ms for list views; SignalR update fan-out < 1s.
- All inter-service traffic authenticated; secrets via Aspire parameters/Key Vault.
- Metric retention: raw 30d, downsampled 1y (configurable).
