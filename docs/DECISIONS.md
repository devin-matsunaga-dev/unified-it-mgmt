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
