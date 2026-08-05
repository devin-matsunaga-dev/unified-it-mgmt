# CONVENTIONS.md — Code Standards

> Generated code must look like it was written by the same person as the rest of the repo. When in doubt, find an existing example in the codebase and copy its shape.

## Solution layout

```
src/
  AppHost/                  # Aspire orchestration
  Web.Host/                 # ASP.NET Core host (endpoints thin — delegate to modules)
  Modules/{Helpdesk,Assets,Monitoring}/
    Features/<FeatureName>/ # vertical slice: endpoint(s), service, models together
    Data/                   # DbContext, entity configs, migrations
  Platform/                 # cross-cutting services
  Contracts/                # events + shared DTOs only
services/
  poller/                   # Python
  discovery/                # Python (Phase 4+)
web/                        # React app
tests/
  <Project>.Tests/          # mirrors src structure
docs/                       # ARCHITECTURE, CONVENTIONS, DECISIONS, STATUS
```

## .NET

- Target **net10.0** (current LTS) in every project; `LangVersion` latest. Never scaffold net8.0/net9.0 — both EOL Nov 2026.
- Nullable enabled, implicit usings on, warnings as errors. File-scoped namespaces.
- Naming: `PascalCase` types/methods, `camelCase` locals, `_camelCase` private fields. Async methods end in `Async`.
- **API:** REST, plural nouns (`/api/tickets/{id}`), kebab-case multi-word routes. Verbs: GET/POST/PUT/DELETE; POST for actions that aren't CRUD (`/api/tickets/{id}/transitions`).
- **Errors:** RFC 7807 `ProblemDetails` for every non-2xx. Validation → 400 with field errors; auth → 401/403; missing → 404; workflow conflict → 409. Never leak stack traces.
- **DTOs:** records; request/response types per endpoint (`CreateTicketRequest`, `TicketResponse`). Never expose EF entities from an API.
- Validation with FluentValidation at the edge; domain guards throw domain exceptions mapped centrally.
- IDs: `Guid` (v7). Timestamps: `DateTimeOffset`, UTC in storage; convert at the UI only.
- Pagination: `?page=&pageSize=` (default 25, max 200) returning `{ items, total, page, pageSize }`.
- EF: entity configs in separate `IEntityTypeConfiguration` classes; snake_case table/column names; migrations named `WPxy_ShortDescription`.

## Events (Contracts)

- Names are past-tense facts: `TicketCreated`, `AlertRaised`, `DeviceDiscovered`.
- Records with: `EventId` (Guid), `OccurredAt` (DateTimeOffset), aggregate id, minimal payload (ids + facts, not whole entities). Versioning by new type (`TicketCreatedV2`), never mutation.
- Consumers live in the consuming module, named `<Event>Consumer`, always use the Platform idempotency helper.

## Python services

- Python 3.13 minimum, 3.14 preferred where dependencies allow; `asyncio` throughout, type hints mandatory, `ruff` + `mypy` clean.
- Layout: `services/<name>/src/<name>/`, `pyproject.toml`, `Dockerfile`, `tests/`.
- Config via environment variables only (injected by Aspire/compose); a `Settings` dataclass reads them at startup; crash fast on missing config.
- Structured JSON logging to stdout. Every published message includes `event_id`, `occurred_at`, `source`.
- One failing target must never abort a cycle: per-target try/except, error counted, cycle continues.

## React

- TypeScript strict. Function components + hooks only.
- Server state: TanStack Query (keys as `['tickets', filters]`); client state: Zustand only when genuinely shared; otherwise local state.
- Files: `PascalCase.tsx` components, `useThing.ts` hooks, feature folders mirroring modules (`features/tickets/`, `features/assets/`).
- API layer: one typed client per module in `api/`; components never call `fetch` directly.
- Forms: react-hook-form + zod schemas shared with API expectations.
- Errors: query/mutation errors surface via the shared toast; destructive actions get a confirm dialog.
- Live data: shared SignalR hook per hub; components subscribe, never open raw connections.

## Testing

- Naming: `MethodOrFeature_Scenario_Expectation`.
- Unit tests for domain logic (state machines, SLA math, dedupe) — no infra.
- Integration tests via the shared Testcontainers fixture (Postgres + RabbitMQ) for anything touching DB/bus.
- Every WP adds at least one failure-path test. E2E pipeline test (sim → alert → ticket) must stay green from WP-3.12 onward.

## Git

- Branch: `feat/wp-X.Y-short-name` (also `fix/`, `chore/`). Squash-merge to `main`; main is always releasable.
- Conventional commits: `feat(helpdesk): add SLA pause on pending (WP-1.5)`.
- Phase gates tagged `vX.Y-phaseN`.

## Documentation duties (every session)

- New decisions → one line each in `docs/DECISIONS.md`.
- WP completion → update `docs/STATUS.md`.
- Architecture-affecting change → update `ARCHITECTURE.md` in the same WP, never later.
