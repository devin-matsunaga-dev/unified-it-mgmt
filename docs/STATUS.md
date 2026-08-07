# STATUS.md — Project State

> Updated at the end of every work-package session (Codex's final task, alongside DECISIONS.md). This file + the code itself is how a fresh session knows where the project stands.

## Current position

- **Phase:** 2 — Assets/CMDB
- **Last completed WP:** WP-2.3 (Relationships + dependency graph)
- **Current WP:** WP-2.4 (Ticket↔asset + 360° pages)
- **Current branch:** feat/wp-2.4-ticket-asset-360-pages
- **Next WP once WP-2.4 verifies:** WP-2.5 (Import + bulk edit), branch `feat/wp-2.5-import-bulk-edit`
- **Last tag:** `v.0.2-phase1` (Phase 1 gate; note the stray dot — Phase 0 was tagged `v0.1-phase0`)

## Platform versions (law — see WORKFLOW.md table for EOL dates)

.NET 10 LTS · Aspire 13.x latest (`aspire update` at phase gates) · Node 24 LTS · Python 3.13+ · React 19 + latest Vite · Tailwind + shadcn/ui + lucide · PostgreSQL (newest Timescale-supported major) · Ubuntu LTS

## In flight / carry-over notes

<!-- Anything unfinished, known-broken, or deferred from the last session that the next session must know. Keep to a few lines; delete when resolved. -->

- WP-2.3's integration tests have now been run against a real Postgres (Testcontainers) and pass on the first attempt — the recursive CTEs, the keyless `CiGraphHop` mapping, the `FromSqlInterpolated` aliases and the `uuid[]` path guard are all proven. The manual checklist was then walked end to end against a live `aspire run` stack (VM→Host→Switch→Router built by hand, cycle closed, every failure path exercised) and every step matched. No code changes were needed. The branch is still **uncommitted** — commit it before starting WP-2.4.

- `npm audit` reports two high findings inherited through React Router 7.18.2 for RSC action handling; this Vite SPA does not enable RSC/server actions. The offered fix is a forced downgrade to 7.11.0, so Dependabot will monitor for a non-breaking patched release.
- The portal has no attachment upload; requesters can only attach files by replying to the ticket email. Add it to a later helpdesk WP if it is wanted in the browser.
- Categories and their custom fields are managed through the API only (`/api/ticket-categories`, AdminOnly) — there is no admin screen. Add one if the tree ever needs editing outside a REST client.
- SLA policies still match on their own free-text `Category` string and are looked up by priority alone; they are not wired to the WP-1.9 category tree. Worth a dedicated WP.
- Custom fields are not inherited from parent categories: a field attaches to exactly the category it was created on.
- The ticket list is now filtered and searched on the server, but it still requests a single page of 200 (footer arrows remain disabled). WP-1.11 has now seeded exactly 200 tickets, so the list is at its ceiling: the next ticket anyone creates falls off the end of an unfiltered list. Real pagination is the first thing to fix in Phase 2.
- Seeded requester and assignee ids are usernames (`enduser1`, `technician1`), but a real Keycloak login presents a random `sub`. So the 200 seeded tickets fill the agent list, dashboards and search, while a requester logging into `/portal` sees only tickets they created themselves in this session. Fixing it means pinning user ids in the realm import (auth configuration — needs its own WP).
- SLA policies and a business-hours calendar are now seeded, so every ticket created by hand starts an SLA clock. Before WP-1.11 a fresh database had no policy and `SlaService.StartAsync` silently did nothing.
- Seeded SLA rows carry pre-set warning/breach flags so `SlaEvaluationJob` will not re-escalate them. If the seeded targets are ever changed, the flags must be recomputed with them or the job will reassign seeded tickets on its next pass.
- Full-text search uses the `english` dictionary for every ticket; there is no per-locale text-search configuration. Terms are prefix-matched and AND-ed, so it narrows as you type, but it is not fuzzy — a typo inside a word ("aurroa") still finds nothing. Trigram/`pg_trgm` similarity is the fix if that becomes a complaint.
- Canned responses are managed from the "Manage" dialog in the ticket reply composer (any agent); saved views are managed inline on the ticket list. Neither has an admin screen under a settings area.
- Status and priority list filters are single-select in the UI, although the API and the saved-view payload accept several of each.
- The CMDB has no seeder yet, and the dev database resets on most AppHost restarts, so every CI and CI custom field must be created by hand for a demo. WP-2.8 (Seeder: infrastructure) owns fixing this; until then, verification of the assets screens starts from an empty list.
- CI custom fields are managed through the API only (`POST/DELETE /api/ci-custom-fields`, AdminOnly) — there is no admin screen, same as the WP-1.9 ticket categories. The form reads them from `/api/ci-type-schemas`.
- CI attributes are enforced above the database: TPH makes every per-type column nullable, so `CiTypeSchema` is the only thing stopping a Server row with no hostname. Anything writing CIs outside `CiService` (a seeder, an importer) must bind through `CiTypeSchema.Bind` or it will write half-populated rows.
- `/api/cis` list search is `ILIKE` over name/asset tag/serial only — there is no full-text index on the assets schema like the one WP-1.10 added for tickets. Revisit if the CMDB grows past a few thousand rows.
- Deleting a CI now returns 409 if any relationship names it on either end (WP-2.3), backed by `Restrict` foreign keys. WP-2.4 (ticket↔asset links) still has to add its own in-use check to the same `CiService.DeleteAsync` guard, or deletes will silently orphan ticket links. Deleting an unrelated CI still cascades away its lifecycle history and check-in/out log.
- Relationships and the graph traversals are API-only — there is no UI. The relations mini-graph is explicitly WP-2.4's scope, so verification of WP-2.3 is done with a REST client.
- The graph endpoints are `GET /api/cis/{id}/{ancestors|descendants|impacted-by}?maxDepth=`. Direction convention: a relationship reads source→target as "source depends on target", so ancestors walk up to what a CI needs and descendants walk down to what needs it. `impacted-by` is the descendants walk with the CI itself at depth 0.
- Traversal depth is capped at 10 (default 5) and the value is clamped, not rejected; the response echoes the effective `maxDepth` and a `maxDepthReached` flag. A graph deeper than 10 hops silently ends there apart from that flag.
- Cycles are allowed in the data and traversed safely; responses carry `containsCycle`. Nothing warns an operator at creation time that they have just closed a loop — the flag only appears once someone runs a traversal.
- The CTE's cycle guard is a per-path visited array, so a very wide diamond-heavy graph does more work than a plain visited-set walk would. Fine at CMDB scale with a 10-hop cap; revisit if WP-2.8's seeded estate or a real import makes traversals slow.
- There is still no CMDB seeder (WP-2.8), so the VM→Host→Switch→Router chain has to be built by hand for a demo, and it is gone after an AppHost restart.
- Lifecycle and ownership live in a right-side drawer on the assets list ("Lifecycle" button per row); there is no CI detail page yet, and the "shows on the user's page" half of the WP-2.2 verify is served by the new owner filter on the list. WP-2.4 owns the real 360° pages.
- `/api/directory/users|departments|sites` are new agent-only (`CanManageAssets`) read endpoints over the Platform demo directory. They are the picker source for CI ownership; if another surface needs them for EndUsers, the policy has to be revisited.
- A CI's owner/department/site are ids plus name snapshots. Renaming a department in `platform.departments` will not update the CIs already assigned to it — the same trade-off WP-1.7 made for ticket requesters.
- Disposed CIs are frozen: `PUT /api/cis/{id}`, the assignment endpoint, and any further transition all return 409. Nothing in the UI can un-dispose a CI, which is deliberate.
- The CMDB still has no seeder (WP-2.8), so lifecycle and ownership must be verified against hand-created CIs — but the ownership pickers do work on a fresh database, because the Platform demo seeder already provides the 20 users, 4 departments, and 3 sites.
- `CanManageAssets` (Admin/Technician/Manager) is a new authorization policy added by WP-2.1. It is additive — the existing `AdminOnly` and `CanManageTickets` policies are untouched.
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
- [x] WP-1.11 Seeder: helpdesk history (2026-08-07) — branch was `feat/wp-1.11-seeder-helpdesk-history`, not the name recorded here

### Phase 2 — Assets/CMDB
- [x] WP-2.1 CI registry (2026-08-07) — added the `CanManageAssets` policy (additive; existing policies untouched), which the WP text did not call for but the endpoints required
- [x] WP-2.2 Lifecycle + ownership (2026-08-07) — added a Platform `IDirectoryService` + `/api/directory/*` endpoints, which the WP text did not call for but the module boundary required (Assets may not read `platform.user_profiles`)
- [x] WP-2.3 Relationships + graph (2026-08-07) — branch was `feat/wp-2.3-relationship-dependencies-graph`, not the name previously recorded here; also added the CI-delete in-use guard the WP-2.2 notes flagged, which the WP text did not call for
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
