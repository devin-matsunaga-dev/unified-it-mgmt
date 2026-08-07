# Unified IT Management System — Codex Work Packages

Companion to the roadmap. Each work package (WP) is sized for one focused Codex session, ends with verification and a git checkpoint, and builds strictly on the previous ones.

---

## Session Model

**One Codex chat per work package** (bundling 2-3 small, tightly-related WPs like 2.6+2.7 is fine). Never carry a chat across phases or past a completed package — long chats degrade context and bypass your checkpoints. Continuity between sessions comes from the steering files, not chat history:

- `ARCHITECTURE.md` + `CONVENTIONS.md` + `DESIGN.md` (UI design system w/ reference screenshot) — pasted/referenced at the start of every session.
- `STATUS.md` — the "you are here" file: current WP, completed checklist, carry-over notes. Read at session start, updated at session end.
- `DECISIONS.md` — **Codex's final task in every session is appending any new decisions made** (one line each: "chose X over Y because Z") **and updating STATUS.md** (flip the WP checkbox, set next WP, note anything in flight). This is the memory handoff to the next session.

Session lifecycle: open branch → new chat with protocol + steering files + WP text → build → verify → Codex appends DECISIONS.md → close chat → you review, merge, tag if phase gate.

---

## The Package Protocol (paste this at the start of EVERY Codex session)

> You are implementing one work package in an existing codebase. Follow `ARCHITECTURE.md` and `CONVENTIONS.md` exactly — do not invent new patterns, folders, or libraries without asking.
>
> **Rules for this session:**
> 1. Implement ONLY the scope of this work package. If you believe something outside scope is needed, stop and ask.
> 2. Write automated tests for everything you build (unit + integration via Testcontainers where infra is involved). Run them and show me the results.
> 3. At the end, output a **Package Completion Report** containing:
>    - **Changes:** files added/modified with a one-line purpose each.
>    - **Automated tests:** what you wrote, what it covers, pass/fail output.
>    - **Manual verification checklist:** numbered steps I will personally perform (exact URLs, commands, expected results, including at least one failure-path check, e.g. "submit invalid data → expect 400").
>    - **Regression check:** the single command I run to verify nothing prior broke (e.g. `dotnet test && npm test`).
>    - **Git suggestion:** branch name `feat/wp-X.Y-short-name` and a conventional commit message.
> 4. After the report, append any decisions made this session to `DECISIONS.md` (one line each: "chose X over Y because Z"; if none, say so) and update `STATUS.md`: check off this WP, set the next one, and note anything unfinished under "In flight".
> 5. Then STOP. Do not start the next package. I will verify, commit, and open the next session myself.
> 6. Never modify: auth configuration, credential handling, bus topology, or migration history from previous packages unless the WP explicitly says so.

**My side of the loop (per package):**
1. Open a branch: `git checkout -b feat/wp-X.Y-name`
2. Run Codex with the protocol + the WP text + relevant convention files.
3. Run the regression command, then walk the manual checklist myself.
4. Review the diff like a PR (line-by-line if the WP is marked **[SENSITIVE]** — auth, credentials, bus, automation, money/SLA math).
5. Squash-merge to `main`, tag phase completions (`v0.3-phase3`).
6. Append one line to `DECISIONS.md` if anything notable was chosen.

*Rationale for this shape (vs. "pause for git"):* the pause alone isn't enough — the completion report forces Codex to hand you a testable, reviewable unit, and branch-per-package + squash-merge means a failed package is one `git branch -D` away from gone, never entangled with working code.

---

# PHASE 0 — Foundation

### WP-0.1 — Repo, solution skeleton, convention docs
- Git repo, `.gitignore`, `.editorconfig`, solution: `AppHost`, `Web.Host` (ASP.NET Core), module assemblies `Modules.Helpdesk`, `Modules.Assets`, `Modules.Monitoring`, plus `Platform`, `Contracts`, `Tests`.
- Write `ARCHITECTURE.md`, `CONVENTIONS.md` (naming, folders, error model, event publishing pattern, test style), empty `DECISIONS.md`.
- **Verify:** solution builds; docs describe the layout accurately (you edit them until they match your taste — these are your steering files forever).

### WP-0.2 — Aspire AppHost + infrastructure resources
- AppHost composes: Postgres (TimescaleDB image), Redis, RabbitMQ (management UI), Keycloak, MinIO; wire connection strings to `Web.Host`.
- Health checks endpoint `/health` aggregating all dependencies.
- **Verify:** `dotnet run` on AppHost → Aspire dashboard shows all resources green; `/health` returns healthy; RabbitMQ and MinIO consoles reachable.

### WP-0.3 — Authentication + RBAC **[SENSITIVE]**
- Keycloak realm import file (realm, clients, 4 roles: Admin/Technician/Manager/EndUser, 2 test users per role).
- OIDC auth in `Web.Host`; role-based authorization policies; `/api/me` endpoint returning identity + roles.
- Design note in code: identity provider must be swappable to Entra ID via config only.
- **Verify:** login as each role; `/api/me` shows correct roles; an Admin-only test endpoint returns 403 for EndUser; logout works.

### WP-0.4 — Audit log + platform services
- Audit service: append-only table (who, what, entity, before/after JSON, timestamp, correlation id); interceptor/middleware so module code logs in one line.
- Notification service stub (logs instead of sending) with template model; Quartz.NET scheduler with one heartbeat job.
- **Verify:** hit any write endpoint → audit row appears with your user id; scheduler job visible in logs every minute.

### WP-0.5 — Message bus + transactional outbox **[SENSITIVE]**
- MassTransit + RabbitMQ, EF outbox pattern; `Contracts` project gets first event `SystemPing`; one publisher endpoint, one consumer that audits receipt.
- Idempotency helper (dedupe-key based) in `Platform` for consumers.
- **Verify:** POST `/api/dev/ping` → event row in outbox → consumed → audit entry. Kill RabbitMQ, POST again, restart RabbitMQ → event still delivered (outbox proof). Send same dedupe key twice → consumed once.

### WP-0.6 — React shell + auth flow
- Vite + TS app styled per `DESIGN.md` (Tailwind + shadcn/ui + lucide, dark-navy sidebar shell): OIDC login, layout (sidebar/topbar), protected routes per role, dark mode, TanStack Query configured with auth header, error toast pattern.
- Aspire `AddNpmApp` so it launches with everything else.
- **Verify:** login redirects through Keycloak and back; EndUser cannot see admin nav; token refresh works after 5+ min idle.

### WP-0.7 — CI + test harness
- GitHub Actions (or equivalent): build, `dotnet test`, `npm test`, docker image build+push on tag; Dependabot config.
- Testcontainers base fixture (Postgres + RabbitMQ) used by one example integration test.
- **Verify:** push a branch → CI green; intentionally break a test → CI red; image appears in registry on tag.

### WP-0.8 — Demo-data seeder v1
- Console project/endpoint: seeds org (sites, departments), 20 users across roles, idempotent re-run.
- **Verify:** run twice → no duplicates; seeded users can log in (synced to Keycloak realm or documented as realm users).

**🏁 Phase 0 gate:** fresh clone → `dotnet run` → log in → ping event round-trips. Tag `v0.1-phase0`.

---

# PHASE 1 — Helpdesk Core

### WP-1.1 — Ticket domain + CRUD API
- Ticket entity (type, priority/urgency/impact + computed priority matrix, requester, timestamps), EF config, migrations, REST endpoints with validation, audit on all writes, `TicketCreated/Updated` events via outbox.
- **Verify:** create/read/update via Swagger; invalid priority → 400; audit rows exist; event visible in RabbitMQ.

### WP-1.2 — Status workflow engine
- Configurable statuses + allowed transitions (seed default: New→Triage→InProgress→Pending→Resolved→Closed); guarded transitions (e.g. Resolve requires resolution note); transition history table.
- **Verify:** legal transition succeeds; illegal transition → 409 with clear message; resolving without note → 400; history endpoint lists every hop.

### WP-1.3 — Queues + assignment
- Team + queue entities; manual assignment, round-robin auto-assign per queue, reassignment history, "my tickets" endpoint.
- **Verify:** create 3 tickets in a queue with 2 techs → alternating assignment; reassign → history shows both; tech sees only theirs in "my tickets".

### WP-1.4 — Comments, worklogs, attachments
- Public vs internal comments; worklogs with minutes; attachments to MinIO with size/type allowlist and AV-scan hook interface (no-op impl).
- **Verify:** EndUser cannot read internal notes (API test + UI later); upload 10MB allowed file OK; blocked extension → 400; download link works.

### WP-1.5 — SLA engine **[SENSITIVE — review timer math]**
- SLA policies (per priority/category): response + resolution targets, business hours calendar, pause on Pending, breach + warning events via scheduler, escalation chain (notify → reassign).
- **Verify:** create ticket with 5-min test SLA → warning fires, breach fires, escalation notification logged; set Pending → clock stops (check remaining time endpoint); resume → resumes.

### WP-1.6 — Email-to-ticket + outbound mail
- IMAP (or Graph) ingestion job: new mail → ticket, replies thread by token in subject/headers; outbound notification templates (created/updated/resolved) through notification service (real SMTP now, MailHog container for dev).
- **Verify:** send mail to dev mailbox → ticket appears with body+attachment; reply to notification → lands as comment on same ticket, not a new one.

### WP-1.7 — Agent ticket UI
- Ticket list (TanStack Table: filters, sort, saved column state), ticket detail (timeline of comments/history, transition buttons honoring workflow, assignment, SLA countdown chip), quick-create modal.
- **Verify:** full lifecycle done entirely in UI; SLA chip counts down; illegal transition button disabled; internal note visually distinct.

### WP-1.8 — Self-service portal
- EndUser area: submit request (category picker), my tickets list/detail, comment, close-confirm; visually distinct from agent app.
- **Verify:** EndUser submits → appears in agent queue; agent reply (public) visible to user, internal note invisible; user cannot access agent routes.

### WP-1.9 — Categories + custom fields
- Category tree CRUD; per-category dynamic fields (text, number, date, select, required flag); rendered in create forms; values stored + shown on detail.
- **Verify:** add "Laptop issue" category with required "Asset tag" field → portal form shows it, blocks empty submit; value visible on agent detail.

### WP-1.10 — Search, saved views, canned responses
- Postgres full-text over tickets/comments; saved filters per user + shared team views; canned response CRUD + insert into reply box.
- **Verify:** search finds word inside an old comment; saved view persists across logout; canned response inserts with placeholders filled (ticket id, requester name).

### WP-1.11 — Seeder: helpdesk history
- Extend seeder: 200 tickets across statuses/ages/SLA states, comments, worklogs.
- **Verify:** dashboards/lists look "lived-in"; SLA breach examples exist; re-run idempotent.

**🏁 Phase 1 gate:** run your own helpdesk for one real day. Tag `v0.2-phase1`.

---

# PHASE 2 — Assets / CMDB

### WP-2.1 — CI registry + type schemas
- CI base entity + type system (Hardware/Server/NetworkDevice/Software/Virtual/Logical) with type-specific attributes + user-defined custom fields; CRUD + list with type filters; events on changes.
- **Verify:** create one CI of each type; type-specific fields enforced; custom field added at runtime appears on form.

### WP-2.2 — Lifecycle + ownership
- Lifecycle states (Ordered→InStock→Deployed→InRepair→Retired→Disposed) with guarded transitions + history; assignment to user/department/location; check-in/out log.
- **Verify:** illegal jump (Ordered→Disposed) blocked; assign laptop to seeded user → shows on user's page; history complete.

### WP-2.3 — Relationships + dependency graph
- Typed CI-to-CI relations (runs-on, connects-to, depends-on, hosted-on); recursive CTE endpoints: `ancestors`, `descendants`, `impacted-by(id)` with depth + cycle protection.
- **Verify:** build chain VM→Host→Switch→Router; `impacted-by(Router)` returns all three; create a cycle → rejected or safely traversed (documented choice in DECISIONS.md).

### WP-2.4 — Ticket ↔ asset linking + 360° pages
- Link/unlink CIs on tickets; asset page: details, relations mini-graph, full ticket history; ticket page: linked CI cards; user 360 page: their assets + tickets.
- **Verify:** link CI to ticket → both pages reflect it instantly; unlink audited; user page shows both worlds.

### WP-2.5 — Import wizard + bulk edit
- CSV/Excel upload → column mapping UI → dry-run preview with dedupe (by serial/asset tag) → commit report (created/updated/skipped); bulk edit selected rows (location, owner, state).
- **Verify:** import 100-row file twice → second run all "skipped/updated", zero dupes; malformed row reported with line number, rest import.

### WP-2.6 — Contracts, warranty, vendors
- Vendor + contract entities; warranty dates on CIs; scheduler job → renewal/expiry notifications (30/7 days); contract page listing covered CIs.
- **Verify:** set warranty expiring tomorrow → notification logged on next job run; contract shows its CIs.

### WP-2.7 — Barcode/QR
- Label generation (QR encoding asset URL) single + batch PDF; `/scan` mobile-friendly lookup page.
- **Verify:** print/scan QR with your phone on the LAN → lands on asset page.

### WP-2.8 — Seeder: infrastructure
- 60 devices (switches, routers, servers, VMs, laptops) with realistic relations forming 2-3 dependency trees, warranties, some linked tickets.
- **Verify:** graph endpoints return multi-level trees; asset lists look real.

### WP-2.9 — Relationship editor (UI)
- "Relate to…" on the CI page's Relations card: CI picker + relationship type select + optional description; per-edge remove with confirm; surface the WP-2.3 guards as field errors (self-relation 400, duplicate 409, disposed CI 409, delete-blocked 409). Read-only graph and the API itself already exist (WP-2.3/2.4) — this is the write surface only.
- **Verify:** build VM→Host→Switch→Router entirely in the browser; `impacted-by(Router)` returns all three; relate a CI to itself → inline field error, nothing written; delete an edge → it leaves both CIs' graphs.
- **Why it exists:** WP-4.2 discovery populates *network* topology automatically, but a logical edge ("Payroll service DependsOn its database") is a human statement no LLDP frame carries — and WP-5.1 suppression plus WP-5.2 blast radius are only as good as those edges. Until this ships, every relationship needs a REST client.

**🏁 Phase 2 gate:** asset lifecycle + linking demo end-to-end. Tag `v0.3-phase2`.

---

# PHASE 3 — Monitoring + Unified Loop

### WP-3.1 — Monitoring.Api: device + check config
- Monitored device registry (links to CMDB CI), check definitions (type, interval, thresholds), poller registration + config-fetch endpoint (versioned config, only deltas), maintenance windows model.
- **Verify:** create device+checks via API; config endpoint returns them; edit threshold → config version bumps.

### WP-3.2 — Python poller skeleton + heartbeat **[SENSITIVE — bus creds]**
- Python service (asyncio): pulls config from Monitoring.Api, publish-only RabbitMQ credentials, structured logging, heartbeat event every cycle, Dockerfile, added to AppHost.
- .NET consumer: poller registry + "missed N heartbeats" alert event.
- **Verify:** poller shows in Aspire dashboard; heartbeats visible; `docker stop` the poller → missed-heartbeat alert within 2 cycles; poller creds cannot consume other queues (attempt fails).

### WP-3.3 — ICMP + SNMP polling
- ICMP up/down; SNMP v2c/v3 (pysnmp): sysInfo, CPU, memory; telemetry batched to bus; per-device error handling (one dead device never blocks the cycle).
- **Verify:** against snmpsim (WP-3.12 can be pulled earlier if you prefer sim-first — recommended) or a lab device: metrics events flow; unplug/stop target → down event; other devices keep polling.

### WP-3.4 — Metrics storage (TimescaleDB)
- Hypertables for metrics; ingestion consumer; retention + downsampling (continuous aggregates: raw 30d, 5-min 1y); query API (range, aggregation) for charts.
- **Verify:** metrics rows accumulate; query API returns series; insert old-dated rows → retention policy drops them (test with short policy).

### WP-3.5 — Alert engine (state machine) **[SENSITIVE — core logic]**
- Redis-backed per-check state machine OK→Warning→Critical→Recovering→OK; threshold rules with hysteresis + "for N cycles" conditions; flap suppression; maintenance windows mute; `AlertRaised/Cleared` events.
- **Verify:** drive fake telemetry through: crossing threshold once ≠ alert (N-cycle rule); sustained → Critical raised exactly once; recovery → single Cleared; flapping series → suppressed with flap flag; window active → silence.

### WP-3.6 — Alert→ticket automation **[SENSITIVE]**
- Consumer: AlertRaised → ticket with dedupe key `alert:{deviceId}:{ruleId}` (reopen/annotate existing instead of duplicating); AlertCleared → auto-resolve with note; rate limit (max tickets/min per rule) + circuit breaker with admin notification.
- **Verify:** raise alert → one ticket; raise same alert 10× → still one; clear → ticket auto-resolves; storm of 50 distinct alerts → breaker trips, admin notified, no ticket flood.

### WP-3.7 — Alert enrichment
- Alert/ticket carries CMDB context: owner, location, warranty status, open related tickets, link to CI; shown on alert board + ticket detail.
- **Verify:** alert on seeded switch shows owner/location; its auto-ticket links the CI both ways.

### WP-3.8 — Service checks
- TCP port, HTTP(S) with status/content match + latency, cert-expiry-days check; configurable like SNMP checks.
- **Verify:** point at MailHog UI → OK; wrong port → Critical; cert check against an https endpoint reports days; kill target container → alert → ticket (loop works for service checks too).

### WP-3.9 — Real-time dashboards
- SignalR hub broadcasting alert/status changes; React: status board (device tiles), alert board (live list, ack button), device page with metric charts (range picker) via query API.
- **Verify:** two browsers open → stop a device → both boards update <1s without refresh; ack reflects everywhere; charts render 24h of seeded metrics.

### WP-3.10 — Notification channels + routing
- Email + Teams/Slack webhook channels; routing rules (severity/device group → channel + schedule); per-user notification preferences; wired into alert + SLA events.
- **Verify:** Critical alert → Teams message with deep link; Warning routed to email only; quiet-hours schedule suppresses then digests.

### WP-3.11 — Credential vault **[SENSITIVE — line-by-line review]**
- Encrypted-at-rest store (ASP.NET Data Protection, keys persisted) for SNMP communities/v3 creds, SSH, WMI; scoped per site; write-only API (create/rotate/delete; never returns secret material); poller fetches via short-lived scoped grant; every access audited.
- **Verify:** GET on a credential returns metadata only, never the secret; DB inspection shows ciphertext; poller polls successfully using vaulted cred; access appears in audit log; rotating cred → poller picks up next cycle.

### WP-3.12 — Simulator rig + E2E pipeline test
- Compose/AppHost additions: `snmpsim` with device profiles (healthy, degrading, down-able), mock HTTP target; one CI-runnable E2E test: sim degrades → alert → ticket → sim recovers → ticket resolved.
- **Verify:** run E2E locally and in CI green; break threshold config → E2E fails (it's a real guard).

**🏁 Phase 3 gate:** the killer demo — stop a sim device, watch ticket appear with asset context, revive, watch auto-resolve. Latency <5s. Tag `v0.4-phase3`.

---

# PHASE 4 — Discovery & Pipeline

### WP-4.1 — Discovery service (Python)
- Scheduled subnet scans (ICMP sweep + port fingerprint), SNMP identify (sysDescr/sysObjectID), LLDP/CDP neighbor walk; results published as `DeviceDiscovered` events; scan profiles configured via API.
- **Verify:** scan the sim network → discoveries flow; scan a range with nothing → clean empty result, no crash.

### WP-4.2 — Review queue → CMDB pipeline
- Consumer matches discoveries to existing CIs (MAC/serial/IP heuristics): auto-update matched, queue unmatched for human review (approve→create CI + optionally auto-enroll monitoring, reject→ignore-list).
- **Verify:** discovery of seeded device → CI updated (last-seen, firmware), no dupe; unknown sim device → review card → approve → CI + monitored device exist; reject → never reappears.

### WP-4.3 — Topology maps
- React Flow map from LLDP/CDP + relations: auto-layout, live status coloring via SignalR, manual maps (pin/arrange, save), click-through to device page.
- **Verify:** map matches seeded topology; stop a device → node goes red live; saved manual layout persists.

### WP-4.4 — Software inventory + licensing
- Agentless software collection (WMI/SSH where creds exist) or CSV/agent-import path; normalization catalog v1 (raw name → product); license pools + installed-vs-entitled compliance + expiry alerts.
- **Verify:** import inventory for 5 machines → normalized products listed; license pool of 3 with 5 installs → compliance flag + notification.

### WP-4.5 — Interface monitoring
- Discovery populates interfaces; SNMP ifTable polling (bps in/out, errors, discards, utilization %, oper status); interface alerts (down, utilization threshold); interface table + graphs on device page.
- **Verify:** sim interface counter profile → utilization graph draws; force ifOperStatus down → alert → enriched ticket.

### WP-4.6 — Drift + reconciliation
- Discovered-vs-recorded drift report (new/missing/changed fields); physical audit workflow (scan-to-confirm session, discrepancy report).
- **Verify:** change a CI's recorded location vs discovery data → drift report flags it; audit session marks 3 scanned, report lists the unscanned.

**🏁 Phase 4 gate:** empty subnet → populated, monitored, mapped CMDB with human approval. Tag `v0.5-phase4`.

---

# PHASE 5 — Correlation & Intelligence

### WP-5.1 — Correlation engine: root-cause suppression **[SENSITIVE]**
- Topology-aware correlation: on alert burst, walk dependency graph; ancestor down ⇒ mark descendants "impacted" (suppressed), open ONE root-cause ticket listing affected CIs; time-window grouping; releases suppression on recovery.
- **Verify:** stop sim "core switch" (with 5 dependents down) → exactly 1 ticket, 5 suppressed alerts visible under it; revive → all clear; stop a leaf only → normal single alert path unaffected.

### WP-5.2 — Impact analysis + blast radius
- `impact(ciId)` service (graph + open tickets + SLA exposure + affected users/departments); blast-radius panel on alert detail and on CI page ("what breaks if this dies").
- **Verify:** blast radius of seeded host lists its VMs, their tickets, and owning departments; matches graph fixture exactly (unit-tested against known tree).

### WP-5.3 — CI timeline
- Per-CI interleaved timeline: alerts, tickets, lifecycle changes, config/audit events on one axis with filters.
- **Verify:** device with seeded history shows correctly ordered mixed events; filter to "alerts only" works.

### WP-5.4 — Global search
- One search endpoint across tickets/CIs/devices/alerts/KB/users (Postgres FTS, per-type ranking); global UI search bar with grouped results + keyboard nav.
- **Verify:** serial number finds the CI; requester name finds tickets + user; gibberish → graceful empty state; results respect role visibility (EndUser doesn't see other users' tickets).

### WP-5.5 — Unified dashboards
- Widget framework (drag/resize/save per role); widgets: SLA health, open by priority, network status summary, license compliance, recent root-causes; executive default layout.
- **Verify:** Manager sees exec default; rearrange + save persists; every widget deep-links to its filtered list.

### WP-5.6 — Auto-remediation runbooks **[SENSITIVE — line-by-line]**
- Runbook registry: allowlisted scripts stored server-side, versioned, parameter schema, NO free-text execution path anywhere; trigger rules (alert type → runbook); execution via poller/agent channel with timeout; result attached to ticket + audit; failure → escalate to human; per-runbook rate limit.
- **Verify:** "restart service" runbook on sim → executes, output on ticket, alert clears, audit row; attempt to execute non-allowlisted script via API → 403; failing runbook → ticket escalates, no retry storm.

### WP-5.7 — Problem management + recurrence detection
- Problem entity linking incidents; known-error DB; nightly job flags CI/category with ≥N incidents in window → suggests problem creation.
- **Verify:** seed 5 similar incidents on one switch → suggestion appears; create problem → incidents linked; closing problem prompts KB article.

### WP-5.8 — Maintenance sync
- Change/maintenance request on CIs → on approval, auto-create monitoring maintenance window for those CIs (+ dependents optional); calendar view.
- **Verify:** approve maintenance on sim device for next 10 min → stopping it produces zero alerts/tickets; window expiry → alerting resumes (prove it).

### WP-5.9 — Knowledge base + suggestions
- KB articles (categories, versioning, draft/publish); agent-side suggestions while typing (FTS similarity vs ticket subject/body); portal KB search with deflection prompt before submit.
- **Verify:** typing a ticket matching an article surfaces it; portal search finds published only (drafts hidden); attach article to resolution.

**🏁 Phase 5 gate:** core-switch outage demo = one root-cause ticket + blast radius + runbook attempt. Tag `v0.6-phase5`.

---

# PHASE 6 — Production Deployment

*(These WPs are yours more than Codex's — use Codex for configs/scripts, but you execute on the real network.)*

### WP-6.1 — Production compose + hardening
- `aspire publish` / `aspire deploy` output refined: pinned digests, named volumes, restart policies, resource limits, prod env separation, no dev tools (MailHog, snmpsim) in prod profile.
- **Verify:** on a clean Linux VM: `docker compose up -d` → healthy from images alone (nothing built on the box); reboot VM → everything returns.

### WP-6.2 — Reverse proxy + TLS
- Caddy/Nginx front: internal-CA wildcard cert, TLS termination, HTTP→HTTPS redirect, security headers, WebSocket/SignalR pass-through; services only on internal Docker network.
- **Verify:** valid padlock on corporate browsers; direct container port access from LAN refused; SignalR live updates work through the proxy.

### WP-6.3 — Entra ID cutover **[SENSITIVE]**
- Swap OIDC to Entra ID (or Keycloak-federated-AD): app registration, AD groups → roles mapping, session 8-12h + silent refresh; document rollback to Keycloak.
- **Verify:** colleague logs in with corporate account, lands in correct role; disabled AD account cannot log in; role change in AD reflects on next login.

### WP-6.4 — Backups + restore proof
- Nightly `pg_dump` + MinIO/volume snapshot, shipped off-box, retention; Data Protection keys backed up separately; documented restore runbook.
- **Verify:** **execute a full restore onto a scratch VM and log in** — this WP is not done until restore is proven and timed.

### WP-6.5 — Real network rollout: pollers + firewall
- Firewall requests for SNMP/ICMP/SSH from poller nets; deploy central poller against first real VLAN; deploy one remote poller container in a restricted segment; UDP 162/514 mapping verified for future trap/syslog work.
- **Verify:** real devices polled with vaulted prod creds; pull cable on a lab port → alert → ticket end-to-end on prod; remote poller heartbeats visible centrally.

### WP-6.6 — Observability + self-monitoring
- OTel export → Grafana/Prometheus (or self-hosted stack); dashboards: API latency, bus depth, poller cycle time, DB health; alert on the system's own failures + one external uptime check on the login page.
- **Verify:** stop the ticket consumer → self-alert fires; queue-depth graph shows the backlog then drain.

### WP-6.7 — Pilot + tuning (2-4 weeks, timeboxed)
- Onboard your team; import real assets (WP-2.5 wizard); enable monitoring on one VLAN; run parallel to existing process; weekly tuning: threshold noise, SLA realism, notification fatigue; collect pilot feedback list.
- **Verify (exit = go-live):** 5 straight days where every ticket-worthy event made exactly one ticket, zero false-critical pages, and a colleague resolved a real issue start-to-finish without asking you how.

**🏁 Tag `v1.0`.**

---

# PHASE 7 — Post-Launch Backlog (promote to WPs on demand)

Each becomes a WP written in the same format when scheduled, prioritized by pilot pain:

- **7.A** Change management full (CAB approvals, calendar, freeze windows, rollback plans) — extends WP-5.8.
- **7.B** SNMP trap + syslog receivers with parsing rules (UDP mapping already proven in 6.5).
- **7.C** Network config backup (SSH pull, versioned diff, change alerts) **[SENSITIVE]**.
- **7.D** NetFlow/sFlow collection + top-talkers.
- **7.E** Anomaly detection service (Python/FastAPI baseline deviation) feeding the alert engine.
- **7.F** AI assists: auto-categorization, duplicate detection, KB deflection v2, NL report queries.
- **7.G** Report builder + scheduled reports; CSAT surveys.
- **7.H** Procurement, depreciation, stock/consumables.
- **7.I** Cloud connectors (Intune/Azure/M365 inventory).
- **7.J** Additional-site pollers; k3s migration only if the single VM is actually strained.
- **7.K** Localization, branding, public API keys + outbound webhooks.

---

## Sizing Reality Check

~65 packages. At a sustainable solo pace of 2-3 packages/week with AI assist (some are half-day, some — the [SENSITIVE] ones — are multi-day because review is the work), Phases 0-3 ≈ 3-4 months, matching the roadmap. If a package fights you for more than two sessions, split it — never let one WP swallow a week silently.
