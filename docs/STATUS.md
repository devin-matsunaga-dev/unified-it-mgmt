# STATUS.md — Project State

> Updated at the end of every work-package session (Codex's final task, alongside DECISIONS.md). This file + the code itself is how a fresh session knows where the project stands.

## Current position

- **Phase:** 1 — Helpdesk
- **Last completed WP:** WP-1.10 (Search, saved views, canned responses)
- **Current WP:** WP-1.11 (Seeder: helpdesk history)
- **Current branch:** feat/wp-1.11-seeder-ticket-history
- **Last tag:** —

## Platform versions (law — see WORKFLOW.md table for EOL dates)

.NET 10 LTS · Aspire 13.x latest (`aspire update` at phase gates) · Node 24 LTS · Python 3.13+ · React 19 + latest Vite · Tailwind + shadcn/ui + lucide · PostgreSQL (newest Timescale-supported major) · Ubuntu LTS

## In flight / carry-over notes

<!-- Anything unfinished, known-broken, or deferred from the last session that the next session must know. Keep to a few lines; delete when resolved. -->

- `npm audit` reports two high findings inherited through React Router 7.18.2 for RSC action handling; this Vite SPA does not enable RSC/server actions. The offered fix is a forced downgrade to 7.11.0, so Dependabot will monitor for a non-breaking patched release.
- The portal has no attachment upload; requesters can only attach files by replying to the ticket email. Add it to a later helpdesk WP if it is wanted in the browser.
- Categories and their custom fields are managed through the API only (`/api/ticket-categories`, AdminOnly) — there is no admin screen. Add one if the tree ever needs editing outside a REST client.
- SLA policies still match on their own free-text `Category` string and are looked up by priority alone; they are not wired to the WP-1.9 category tree. Worth a dedicated WP.
- Custom fields are not inherited from parent categories: a field attaches to exactly the category it was created on.
- The ticket list is now filtered and searched on the server, but it still requests a single page of 200 (footer arrows remain disabled). Real pagination is still owed — it matters once WP-1.11 seeds 200 tickets.
- Full-text search uses the `english` dictionary for every ticket; there is no per-locale text-search configuration. Terms are prefix-matched and AND-ed, so it narrows as you type, but it is not fuzzy — a typo inside a word ("aurroa") still finds nothing. Trigram/`pg_trgm` similarity is the fix if that becomes a complaint.
- Canned responses are managed from the "Manage" dialog in the ticket reply composer (any agent); saved views are managed inline on the ticket list. Neither has an admin screen under a settings area.
- Status and priority list filters are single-select in the UI, although the API and the saved-view payload accept several of each.
- Dev Postgres resets between `aspire run`s whenever AppHost configuration changes (the unnamed `.WithDataVolume()` name embeds a config hash, so a new empty volume is mounted). This is **intentional** — see DECISIONS.md. Consequence: anything created through the API by hand is gone after a restart, so demo/verification fixtures must live in the seeder.

## Completed work packages

<!-- Flip as you go. One line each; add date + anything notably deviated from the WP text. -->

### Phase 0 — Foundation
- [x] WP-0.1 Solution skeleton (net10.0) + docs verified (2026-08-06)
- [x] WP-0.2 Aspire AppHost + infrastructure (2026-08-06)
- [x] WP-0.3 Auth + RBAC (2026-08-06)
- [x] WP-0.4 Audit + platform services (2026-08-06)
- [x] WP-0.5 Bus + outbox (2026-08-06)
- [x] WP-0.6 React shell + auth (styled per DESIGN.md) (2026-08-06)
- [x] WP-0.7 CI + test harness (2026-08-06)
- [x] WP-0.8 Seeder v1 (2026-08-06)

### Phase 1 — Helpdesk
- [x] WP-1.1 Ticket domain + CRUD (2026-08-06)
- [x] WP-1.2 Status workflow (2026-08-06)
- [x] WP-1.3 Queues + assignment (2026-08-06)
- [x] WP-1.4 Comments/worklogs/attachments (2026-08-06)
- [x] WP-1.5 SLA engine (2026-08-07)
- [x] WP-1.6 Email-to-ticket + outbound mail (2026-08-07)
- [x] WP-1.7 Agent ticket UI (2026-08-07)
- [x] WP-1.8 Self-service portal (2026-08-07)
- [x] WP-1.9 Categories + custom fields (2026-08-07)
- [x] WP-1.10 Search/views/canned responses (2026-08-07)
- [ ] WP-1.11 Seeder: ticket history

### Phase 2 — Assets/CMDB
- [ ] WP-2.1 CI registry
- [ ] WP-2.2 Lifecycle + ownership
- [ ] WP-2.3 Relationships + graph
- [ ] WP-2.4 Ticket↔asset + 360 pages
- [ ] WP-2.5 Import + bulk edit
- [ ] WP-2.6 Contracts/warranty
- [ ] WP-2.7 Barcode/QR
- [ ] WP-2.8 Seeder: infrastructure

### Phase 3 — Monitoring + Unified Loop
- [ ] WP-3.1 Device/check config API
- [ ] WP-3.2 Poller skeleton + heartbeat
- [ ] WP-3.3 ICMP/SNMP polling
- [ ] WP-3.4 Metrics storage
- [ ] WP-3.5 Alert state machine
- [ ] WP-3.6 Alert→ticket automation
- [ ] WP-3.7 Alert enrichment
- [ ] WP-3.8 Service checks
- [ ] WP-3.9 Real-time dashboards
- [ ] WP-3.10 Notification routing
- [ ] WP-3.11 Credential vault
- [ ] WP-3.12 Simulator rig + E2E

### Phase 4 — Discovery
- [ ] WP-4.1 Discovery service
- [ ] WP-4.2 Review queue → CMDB
- [ ] WP-4.3 Topology maps
- [ ] WP-4.4 Software inventory + licensing
- [ ] WP-4.5 Interface monitoring
- [ ] WP-4.6 Drift + reconciliation

### Phase 5 — Correlation
- [ ] WP-5.1 Root-cause suppression
- [ ] WP-5.2 Impact + blast radius
- [ ] WP-5.3 CI timeline
- [ ] WP-5.4 Global search
- [ ] WP-5.5 Unified dashboards
- [ ] WP-5.6 Runbooks
- [ ] WP-5.7 Problem mgmt + recurrence
- [ ] WP-5.8 Maintenance sync
- [ ] WP-5.9 Knowledge base

### Phase 6 — Production
- [ ] WP-6.1 Prod compose
- [ ] WP-6.2 Proxy + TLS
- [ ] WP-6.3 Entra ID cutover
- [ ] WP-6.4 Backups + restore proof
- [ ] WP-6.5 Real network rollout
- [ ] WP-6.6 Observability
- [ ] WP-6.7 Pilot + tuning
