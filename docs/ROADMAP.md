# Unified IT Management System — Phased Roadmap (Solo Dev + AI-Assisted)

**Context:** Solo developer using AI codegen (Codex/ChatGPT) for implementation, with human review and correction. Dev on WSL + Aspire, production target = Linux VM running Docker Compose on the corporate network.

Durations assume part-time-to-full-time solo effort with AI assist. Treat them as relative sizing, not promises.

---

## Phase 0 — Foundation & Dev Machine (2-3 weeks)

**Goal:** A skeleton that boots with one command and enforces the patterns every later phase inherits.

- Aspire AppHost composing: Postgres (+TimescaleDB extension), Redis, RabbitMQ, Keycloak, MinIO, React dev server (`AddNpmApp`), one placeholder Python service.
- Modular monolith solution layout: `Helpdesk`, `Assets`, `Monitoring` as module assemblies inside one ASP.NET Core host; shared `Contracts` and `Platform` libraries.
- Platform services: OIDC auth (Keycloak locally, designed to swap to Entra ID), coarse RBAC (Admin / Technician / Manager / End User), audit log, central notification service stub, Quartz.NET scheduler.
- MassTransit + RabbitMQ wiring **with the transactional outbox from day one** — retrofitting it later is painful.
- EF Core migrations per module schema; global exception handling; ASP.NET rate limiting middleware on auth + API.
- React shell: Vite + TS, TanStack Query, auth flow, layout/navigation, dark mode.
- CI pipeline: build, test, `docker build`, image push to registry, Dependabot/docker scout.
- **Demo-data seeder project started** (fake org, users, locations) — grows every phase.

**Solo-dev setup for AI-assisted work (do this now, it pays off in every phase):**
- Write an `ARCHITECTURE.md` + `CONVENTIONS.md` (naming, folder layout, error handling, how events are published). Paste/reference these in every AI coding session so generated code lands in your patterns instead of inventing new ones.
- Establish the review loop: AI writes → you run analyzers/tests → you review diffs like a PR. Never merge unreviewed generated code that touches auth, credentials, or the bus.
- Set up integration-test harness with Testcontainers (Postgres, RabbitMQ) so AI-generated code is verified by tests, not by your eyeballs alone.

**Exit criteria:** `dotnet run` boots everything; you can log in via Keycloak; an audited "hello" event flows API → outbox → RabbitMQ → consumer.

---

## Phase 1 — Helpdesk Core (4-6 weeks)

**Goal:** A usable ticketing system — the module with the most UI surface, so it hardens your React patterns early.

- Tickets: CRUD, types (Incident/Service Request), priority matrix, configurable status workflow with guarded transitions.
- Queues, manual + round-robin assignment, reassignment history.
- Comments (public/internal), worklogs with time tracking, attachments to MinIO (type/size limits, AV-scan hook stubbed).
- SLA engine: policies, response/resolution targets, pause states, breach warnings, escalation chain via scheduler.
- Email-to-ticket: IMAP/Graph ingestion, reply threading, outbound notifications.
- Self-service portal (separate React area or route group): submit, track, comment.
- Categories with dynamic custom fields (form builder v1 — keep it simple: field types, required flags).
- Canned responses, saved views/filters, Postgres full-text search on tickets.
- Seeder: generate realistic ticket history.

**AI-assist notes:** CRUD screens, DTOs, validators, and grid views are ideal codegen targets. Hand-review the SLA timer logic and status-transition guards yourself — that's where subtle bugs live.

**Exit criteria:** You could run a small helpdesk on it for a day without touching the database manually.

---

## Phase 2 — Assets Core / CMDB (3-5 weeks)

**Goal:** The backbone every other module references.

- CI registry with type-specific schemas (hardware, software, virtual, logical) + custom fields.
- Asset lifecycle states with history; ownership/assignment to users, departments, locations; check-in/out.
- CI-to-CI relationships (runs-on, connects-to, depends-on) — recursive CTE queries for the dependency graph.
- **Ticket ↔ asset linking both directions** (first unified feature: asset page shows ticket history, ticket page shows linked CIs).
- CSV/Excel import wizard with mapping + dedupe; bulk edit.
- Warranty, contracts, vendor records, renewal reminders via scheduler.
- Barcode/QR label generation and scan-to-lookup page.
- Seeder: 50+ fake devices with relationships.

**Exit criteria:** Create an asset, relate it to others, link it to a ticket, and see the 360° asset page.

---

## Phase 3 — Monitoring Core + First Unified Loop (5-7 weeks)

**Goal:** The proof-of-product loop: *device breaks → alert → ticket → asset context → auto-resolve.*

- Python poller service: asyncio ICMP + SNMP v2c/v3 polling, config pulled from `Monitoring.Api`, telemetry published to RabbitMQ (least-privilege bus credentials: publish-only + signed config queue).
- Metrics into TimescaleDB with retention/downsampling policies.
- **Alert engine as a state machine** (OK → Warning → Critical → Recovering) with state in Redis; thresholds, hysteresis, flap suppression, maintenance windows.
- **Alert-to-ticket automation with dedupe keys** (`alert:{deviceId}:{ruleId}`), auto-resolve on clear, **rate limits + circuit breaker on ticket creation**.
- Alert enrichment from CMDB: owner, location, warranty, open tickets shown on the alert.
- Service checks: TCP, HTTP(S) content match, cert expiry.
- Real-time dashboards via SignalR: status board, alert board, per-device metric graphs (ECharts/Recharts).
- Notification channels: email + Teams/Slack webhooks with per-schedule routing.
- **Poller heartbeats** — correlation-side alert if a poller misses N cycles.
- **Credential vault** for SNMP/SSH creds: encrypted at rest, write-only API, scoped per site.
- Test rig: `snmpsim` + mock HTTP endpoints in Compose so CI exercises the full pipeline without hardware.

**AI-assist notes:** Great for pysnmp plumbing, chart components, and dashboard widgets. Review the alert state machine and dedupe logic line-by-line — this is the heart of the product.

**Exit criteria:** Kill a simulated device → one ticket appears with asset context → revive it → ticket auto-resolves. End-to-end latency < 5s.

---

## Phase 4 — Discovery & CMDB Pipeline (3-4 weeks)

**Goal:** The CMDB fills itself.

- Python discovery service: scheduled subnet scans, SNMP/LLDP/CDP walk, optional WMI/SSH inventory.
- Discovery → CMDB pipeline with "new device found" review queue (no silent CI creation).
- Discovered-vs-recorded drift reports; normalization catalog v1 for software names.
- Auto-generated topology maps (React Flow) with live status overlay.
- Software inventory + license pools, installed-vs-entitled compliance, expiry alerts.
- Interface monitoring (bandwidth, errors, utilization) now that discovery knows the interfaces.

**Exit criteria:** Point discovery at a lab subnet; devices, interfaces, and topology appear with review-queue approval.

---## Phase 5 — Correlation, Impact & Automation (4-6 weeks)

**Goal:** The differentiators that justify "unified."

- Correlation engine: topology-aware root-cause suppression (core switch down → one root-cause ticket, downstream alerts suppressed and listed as affected CIs).
- Impact analysis + blast-radius preview on alerts and (later) changes, powered by the dependency graph.
- Timeline view per CI: interleaved alerts, tickets, changes on one axis.
- Global search across tickets, assets, devices, alerts, KB.
- Unified executive dashboard; role-based dashboards.
- Auto-remediation runbooks v1: **allowlisted, reviewed scripts only — no free-text execution from the UI**; every run logged to audit + attached to the ticket; escalate on failure.
- Recurring-incident detection → suggest problem record; problem management (incident linking, known-error DB).
- Maintenance sync: approved change/maintenance schedules a monitoring window automatically.
- Knowledge base with agent-side article suggestions.

**Exit criteria:** Simulated switch outage produces exactly one root-cause ticket with a blast-radius list; a runbook fires, logs, and closes it.

---

## Phase 6 — Production Hardening & Corporate Deployment (3-4 weeks)

**Goal:** Same images, promoted to the corporate network.

- Provision Linux VM (Ubuntu LTS, ~8 vCPU / 16-32GB); Docker Engine; `aspire publish`/`aspire deploy`-generated Compose adjusted for prod: pinned versions, named volumes, restart policies, resource limits.
- Reverse proxy (Caddy/Nginx) with internal-CA wildcard cert; TLS terminates at proxy, plain HTTP on the private Docker network (no service-to-service mTLS).
- Swap Keycloak federation → Entra ID/AD groups mapped to roles; session lifetime 8-12h with silent refresh.
- Secrets via Docker secrets/`.env` with tight permissions; device-credential vault keys backed up separately.
- Firewall rules for poller reach into device VLANs; deploy first **remote poller container** into a restricted segment to validate the distributed model. Verify UDP 162/514 mapping for traps/syslog.
- Backups: nightly `pg_dump` + volume snapshots shipped off-box; **perform and document a full restore test before go-live**.
- Egress checks: registry pulls, Teams/SMS webhooks through the corporate proxy.
- Self-monitoring: OpenTelemetry → Grafana/Prometheus (or the system monitoring itself + one external uptime check).
- Pilot: onboard your own team, real devices in one VLAN, run in parallel with existing process for 2-4 weeks; fix the noise (alert tuning is a real work item, budget for it).

**Exit criteria:** A colleague logs in with their AD account, submits a ticket, and a real device outage opens a ticket — with you on vacation.

---

## Phase 7 — Advanced & Intelligence (ongoing, post-launch)

Prioritize by actual usage pain, not by the list order:

- Change management: calendar, CAB approvals, freeze windows, rollback plans (wire into maintenance sync + blast radius from Phase 5).
- SNMP trap + syslog receivers with parsing rules; network config backup with diff/change alerts.
- NetFlow/sFlow collection; distributed pollers at additional sites.
- Anomaly detection service (Python/FastAPI): baseline deviation alerts, alert noise scoring.
- AI assists: ticket auto-categorization, duplicate detection, KB suggestions to end users, natural-language report queries.
- Report builder + scheduled reports; CSAT surveys; procurement/depreciation; stock & consumables.
- Localization, portal branding, API keys/webhooks for third parties.
- Revisit scale: k3s only if you outgrow the single VM or need HA.

---

## Cross-Phase Rules (the discipline that keeps a solo project alive)

1. **Every phase ends deployable.** CI produces runnable images at all times; the seeder always works.
2. **Vertical slices over horizontal layers.** Finish alert→ticket end-to-end before adding a second check type.
3. **AI writes, you own.** Generated code touching auth, credentials, the bus, or automation gets line-by-line review. Everything else gets tests + diff review.
4. **Structural security over procedural.** SSO, allowlists, network isolation, least-privilege bus creds — nothing that requires you to remember a routine.
5. **Tune before you add.** After Phase 6, resist new features until alert noise and SLA settings reflect reality — an ignored monitoring system is a dead one.
6. **Keep a decision log.** One markdown file of "chose X over Y because Z." Future-you (and every AI session) needs it.

**Total to production pilot: roughly 6-8 months** of consistent solo effort with AI assist — Phases 0-3 are the critical path; everything after compounds on that loop.
