# DECISIONS.md — Decision Log

> One line per settled choice: "chose X over Y because Z (WP-N.N, date)". Appended by Codex at the end of every session; never relitigated without a work package.

- Bootstrap: modular monolith over microservices; extract Python pollers/ML only (see ARCHITECTURE.md §9)
- Bootstrap: platform versions pinned to LTS/latest-supported — .NET 10, Aspire 13.x, Node 24, Python 3.13+ (see WORKFLOW.md table)
- Bootstrap: UI = Tailwind + shadcn/ui + lucide per DESIGN.md reference screenshot
- WP-0.1: no new architectural decisions; the skeleton follows the existing steering documents (2026-08-06)
- WP-0.2: no new architectural decisions; infrastructure follows the resource topology already settled in ARCHITECTURE.md (2026-08-06)
- WP-0.3: no new architectural decisions; bearer authentication, policy authorization, and provider-neutral configuration follow the existing authentication architecture (2026-08-06)
- WP-0.4: no new architectural decisions; append-only auditing, notification logging, and scheduled jobs follow the existing platform-service architecture (2026-08-06)
- WP-0.5: chose MassTransit 8.5.10 over 9.x because 9.x requires a commercial runtime license; EF transactional outbox and deterministic consumer deduplication remain unchanged (2026-08-06)
- WP-0.6: chose oidc-client-ts with authorization-code PKCE and automatic silent renewal because it keeps the SPA provider-neutral and stores tokens only in session storage (2026-08-06)
- WP-0.7: chose GitHub Actions with GHCR tag publishing because the repository is hosted on GitHub and can authenticate image pushes without additional credentials (2026-08-06)
- WP-0.8: no new architectural decisions; the console seeder and realm-import users follow the existing Platform and Keycloak development topology (2026-08-06)
- WP-1.1: no new architectural decisions; ticket storage, auditing, and event publication follow the existing module-schema and Platform outbox architecture (2026-08-06)
- WP-1.2: no new architectural decisions; persisted workflow configuration, guarded transitions, history, auditing, and outbox publication follow the existing Helpdesk and Platform patterns (2026-08-06)
- WP-1.3: no new architectural decisions; team-owned queues, persisted round-robin state, assignment history, auditing, and technician-scoped queries follow the existing Helpdesk patterns (2026-08-06)
- WP-1.4: chose a 25 MB attachment ceiling with an explicit document/image/archive extension and media-type allowlist because the required 10 MB upload needs headroom while executable and unknown formats must fail closed (2026-08-06)
- WP-1.4: chose private MinIO objects served through authorized API downloads because ticket visibility must be enforced for every attachment access (2026-08-06)
- WP-1.5: chose persisted elapsed business seconds plus an active interval over wall-clock deadlines because Pending pauses and calendar changes must not consume SLA time (2026-08-07)
- WP-1.5: chose a per-policy warning percentage constrained to 1–99 because different service tiers need configurable lead time before breach (2026-08-07)
- WP-1.5: chose priority-only matching for currently uncategorized tickets while persisting an optional category selector because ticket categories are introduced by WP-1.9 (2026-08-07)
- WP-1.5: chose resolution-breach reassignment to the next ordered queue technician because it extends the established round-robin ownership model without adding a second escalation topology (2026-08-07)
- WP-1.6: chose MailKit 4.17.0 for IMAP, MIME parsing, and SMTP because it provides maintained protocol support and passes the repository vulnerability gate (2026-08-07)
- WP-1.6: chose unique RFC Message-ID persistence with stable ticket Message-ID headers and subject-token fallback because retries must be idempotent and replies must thread across mail clients (2026-08-07)
- WP-1.6: chose MailHog for outbound capture plus GreenMail for the development IMAP inbox because MailHog does not provide IMAP retrieval (2026-08-07)
