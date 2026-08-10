# STATUS.md — Project State

> Updated at the end of every work-package session (Codex's final task, alongside DECISIONS.md). This file + the code itself is how a fresh session knows where the project stands.

## Current position

- **Phase:** 3 — Monitoring + Unified Loop
- **Last completed WP:** WP-3.5 (Alert engine, state machine) — automated tests green (618 .NET solution-wide, up from 534); the poller is unchanged, so its 109 pytest, ruff and mypy results stand from WP-3.3. **The manual checklist has not been walked yet.**
- **Current WP:** WP-3.6 (Alert→ticket automation) **[SENSITIVE]**
- **Current branch:** `feat/wp-3.6-alert-ticket-automation`
- **After it:** WP-3.7 (Alert enrichment)
- **Last tag:** `v.0.2-phase1` (Phase 1 gate; note the stray dot — Phase 0 was tagged `v0.1-phase0`). **`v0.3-phase2` is not cut yet — see In flight.**

## Platform versions (law — see WORKFLOW.md table for EOL dates)

.NET 10 LTS · Aspire 13.x latest (`aspire update` at phase gates) · Node 24 LTS · Python 3.13+ · React 19 + latest Vite · Tailwind + shadcn/ui + lucide · PostgreSQL (newest Timescale-supported major) · Ubuntu LTS

## In flight / carry-over notes

<!-- Anything unfinished, known-broken, or deferred from the last session that the next session must know. Keep to a few lines; delete when resolved. -->

- **`DeviceReachabilityChanged` is now deliberately *not* consumed, and this reverses what the WP-3.4 note predicted.** It is edge-triggered — one message per transition — so a "for N cycles" condition cannot be evaluated against it without re-deriving cycles it does not carry. Every WP-3.5 rule is therefore per *check* and driven from `DeviceTelemetryReported`, and the ICMP check's **availability** rule answers the reachability question from telemetry that does arrive every cycle. The event remains a fact with no consumer; a device-level roll-up (one dead switch instead of four alerts) belongs to **WP-5.1 root-cause suppression**, which is the package that exists for it.

- **A check has exactly two rules and it matters which metric the second one watches.** `check:{checkId}:availability` (did the check complete — every check has one, needs no configuration) and `check:{checkId}:{metric}` (only where a threshold is configured, and only on `AlertRules.PrimaryMetric` — `icmp.rtt_ms`, `cpu.utilisation_percent`, `memory.used_percent`, a raw OID's own name, `check.latency_ms` for WP-3.8's TCP/HTTP). An SNMP CPU read reports a figure **per core** beside the average, so judging every metric would turn one busy host into nine alerts. **`PrimaryMetric` mirrors the poller's naming by hand — if `services/poller/src/poller/checks/*.py` renames a metric, this silently stops alerting on it** and `AlertRulesTests` is the only thing that would notice.

- **Rule ids are the WP-3.6 contract and are derived, not allocated.** WP-3.6's dedupe key is `alert:{deviceId}:{ruleId}`, which only works because the id comes from the check id and is identical across a restart and on every recurrence. Changing the format is a breaking change to a package that has not been written yet.

- **Redis holds the state machine; Postgres holds the truth, and the seam is tested.** `monitoring:alert-state:{deviceId}:{ruleId}` (JSON, 7-day TTL) carries the N-cycle counters, the flap history and what was last published. On a miss `AlertEngine.LoadStateAsync` rebuilds from the open `monitoring.alerts` row, so a Redis flush costs counters rather than re-raising every alert in the estate. `Evaluate_AfterRedisIsFlushed_DoesNotRaiseTheSameAlertAgain` fails if that rebuild is lost. A Redis outage is logged and swallowed — an alert engine that stops evaluating because a cache is unavailable turns one outage into two.

- **"Raised exactly once" is a database constraint, not only a state-machine invariant**: a filtered unique index on `(device_id, rule_id) WHERE status = 'Open'`. Two consumers racing on one rule cannot both open an alert; the loser's transaction fails and its message is retried against the state the winner left.

- **Publication is driven by a *disagreement*, never by a transition.** The state carries `Severity` (what is true) and `PublishedSeverity` (what anyone was told); a raise or a clear happens exactly when they differ and nothing is suppressing. That single rule is what makes "raised once", "cleared once" and "reconcile after a maintenance window or a flap cooldown ends" one mechanism instead of three — and it is the line to look at first if an alert ever goes quiet or repeats.

- **Suppression withholds the message, not the evaluation.** Inside a maintenance window or while flapping, rules still advance and the alert row is still written and updated (with `suppression` = `Maintenance`/`Flapping` and `is_flapping`), so what happened during a change is legible afterwards. Consequence to expect on a board: **a muted rule that recovers leaves an `Open` row at severity `Ok` until the next reading after suppression lifts**, which is when it reconciles and closes. Also: **the state change that trips the flap threshold is itself withheld** — the message proving a rule is flapping is the kind the policy exists to stop.

- **Alert tuning is five nullable columns on `check_definitions` plus `Monitoring:Alerting` defaults (sustain 3, recovery 2, hysteresis 5%, flap 4-in-600s, cooldown 600s, state TTL 7d).** Null means "the platform default" — a check never stores a copy, so raising the platform's sustain count raises it everywhere nobody asked otherwise. They are set through `alertTuning` on the WP-3.1 check create/update payload, and **that block is a complete statement: omitting it restores the defaults** rather than keeping the previous overrides (the WP-2.2 assignment-endpoint rule).

- **There is no `/api/alerts`.** WP-3.9 owns the alert board and this package is engine-only, so alerts are reachable from SQL and from the events. WP-3.6 consumes `AlertRaised`/`AlertCleared` and does not need a read endpoint; WP-3.9 will.

- **A second consumer now binds `Contracts.Events:DeviceTelemetryReported`.** `AlertTelemetryConsumer` sits beside WP-3.4's `DeviceTelemetryConsumer` on its own queue, so a faulting alert engine cannot stop metrics being written or the reverse. It **does** use the Platform dedupe helper (unlike WP-3.2's heartbeat) because the state machine counts consecutive readings and a redelivery would advance every N-cycle counter twice.

- **`InfrastructureFixture` now starts a Redis container** and `AlertEngineIntegrationTests` sets `ConnectionStrings__redis` as an **environment variable** as well as in configuration: Aspire's `AddRedisClient` reads the builder's configuration while the host is being built, which is before `WebApplicationFactory`'s `ConfigureAppConfiguration` sources exist — the same reason the WP-3.4 tests set `ConnectionStrings__database` that way. In-memory config alone gives "No endpoints specified".

- **🐞 A WP-3.1 defect found by WP-3.3 and deliberately NOT fixed here: moving a device between poller groups throws a 500.** `MonitoredDeviceService.UpdateAsync` calls `IMonitoringConfigLog.RecordAsync` **twice** in one transaction when `pollerGroup` changes (the upsert against the new group and the removal against the old — the behaviour DECISIONS.md records as deliberate). `RecordAsync` allocates its version from `max(version)` *in the database*, so both calls compute the same number and EF refuses the second with `InvalidOperationException: another instance with the same key value for {'Version'} is already being tracked` — before anything reaches Postgres. **Confirmed with a throwaway integration probe against a real database on 2026-08-10**, and the same collision is what made this session's monitoring seeder fail on its second device. Nothing tests a group move today. **The fix is in `MonitoringConfigLog.RecordAsync`**: allocate from the greater of the database's `max(version)` and the highest version already added to the change tracker (or call `SaveChangesAsync` between records, which is what the seeder does). It was left alone because it is WP-3.1 code and SESSION.md §3.1 says the default is to record and stay in scope — it wants a `fix/` branch and a test that moves a device between groups.

- **The poller's ICMP works through a sysctl, not a capability, and this was the opposite of the plan.** `--cap-add=NET_RAW` does not give a **non-root** container a usable raw socket: Docker puts the capability in the permitted set, and a process with no file capability on its binary has an empty effective set, so icmplib still fails with "Root privileges are required". Probed both ways against the built image. AppHost instead passes `--sysctl net.ipv4.ping_group_range=10001 10001` and sets `POLLER_ICMP_PRIVILEGED=false`; **that number is the Dockerfile's uid and nothing but a failing ping says so if the two drift.**

- **Telemetry is now ingested; `DeviceReachabilityChanged` still has no consumer.** WP-3.4 bound `DeviceTelemetryConsumer` to `Contracts.Events:DeviceTelemetryReported` and — exactly as the WP-3.3 note predicted — it started receiving with no poller change at all. `Contracts.Events:DeviceReachabilityChanged` is still a fanout with nothing bound — **and WP-3.5 decided not to bind it either; see the note above.** Up/down history is nevertheless queryable, because ingestion derives a `check.success` 0/1 series from every check result.

- **⚠️ WP-3.4 broke a WP-0.5 test and the fix was to narrow that test's assertion, not to change the consumer.** `MessageBusOutboxIntegrationTests.Outbox_BusStartsAfterPublish_...` asserted `platform.consumer_dedupe_entries` was **globally empty** before the bus started — true only while `SystemPingConsumer` was the helper's one user. `DeviceTelemetryConsumer` now writes a row per poller cycle into the database the whole test collection shares, so the assertion started failing on test *order*. It passed alone and failed in the suite, which is the same signature as the WP-3.2 topology trap and is worth recognising on sight. It now asserts only its own `system-ping:` key. **Any future consumer using the dedupe helper is fine; any future test asserting that a shared Platform table is empty is not.**

- **Nothing in application code ever deletes a metric.** Retention (raw 30 days, 5-minute rollup 1 year) and the rollup's refresh are **Timescale background jobs**, installed by the WP-3.4 migration as raw SQL and owned by the database's own scheduler. Consequence: they keep running when the host is down, they do not appear in any C# you can grep for, and `timescaledb_information.jobs` is where you look when data is missing or not aging out. Chunks are **one day** wide, which is also the granularity at which the 30-day policy actually removes anything.

- **`monitoring.device_metrics_5m` is a continuous aggregate with real-time aggregation deliberately turned back on.** Timescale defaults `materialized_only` to true from 2.13; the refresh policy runs every five minutes with a five-minute end offset, so the default would leave the newest 5–10 minutes missing from every long-range chart — the part somebody is actually watching. The `ALTER MATERIALIZED VIEW ... SET (timescaledb.materialized_only = false)` in the migration is load-bearing and easy to lose in a rewrite.

- **The `check.` metric-name prefix is reserved and a poller-supplied sample using it is dropped.** Ingestion derives `check.success` (0/1, and the average over a bucket is availability) and `check.latency_ms` from every check result, including a failed one — a failed check carries no samples of its own, so without this an unreachable device would be indistinguishable from one nobody polls. If a future poller wants to report its own success metric, it needs a different name.

- **🐞 A WP-3.4 defect found by hand-verification and fixed (2026-08-10): a metric name is not a series — a metric name *plus a check* is.** Ingestion derives `check.success` and `check.latency_ms` from **every** check, so the seeded four-check router reports each of those names four times. The first query API filtered on device + metric name only, and returned all four interleaved: on the live estate, ICMP latency of 0.03 ms plotted next to SNMP latency of 522 ms as one line. Storage was never wrong (the natural key carries `check_id`, and the rollup groups by it) — only the read path was. **Now:** `GET .../metrics/series` takes an optional `checkId`, refuses with 400 naming the candidates when the metric is ambiguous and no check is named, and resolves it silently when only one check reports it; the picker lists one entry per (metric, check) with the check's name. **Every test had used one check per device, which is exactly why the suite was green.** `Series_ForAMetricSeveralChecksReport_IsRefusedUntilOneIsNamed` fails if this comes back.

- **A raw-resolution series longer than 24 hours is a 400, not a truncated chart.** `resolution=Auto` (the default) reads raw up to 6 hours and the 5-minute rollup beyond it; asking for `Raw` over a month is refused with a sentence naming the alternative. Same rule as WP-2.3's traversal cap: a partial answer must never look like a complete one.

- **The metrics hypertable has no foreign key to `monitored_devices`, and `device_inventory_facts` does.** A reading is a fact about a moment and stays true after the device row is deleted (and Timescale drops chunks out from under the table anyway); a "what model is this switch" fact is current state and goes with its device. The consequence is that ingestion filters inventory facts against existing devices before writing — a device deleted between the poll and its ingestion would otherwise fail the whole batch.

- **The metric picker is discovered from the data, not declared.** `GET /api/monitored-devices/{id}/metrics` is a `DISTINCT ON (metric_name, check_id)` over the last two days, so a metric that has stopped being reported disappears from the picker rather than charting a flat line. Nothing in the SPA reads any of the three new endpoints yet — WP-3.9 is their intended caller.

- **The poller's broker credential now grants three exchanges, not one.** `RabbitMqDefinitions.PollerExchanges` is the whole permission model in one list, and the write pattern is an anchored alternation of those literal names. A prefix pattern (`^Contracts\.Events:.*$`) is the obvious shortcut next time an exchange is added and is a licence to forge a `TicketCreated`; `Render_PollerWritePattern_DoesNotMatchAnotherEventsExchange` fails if anybody reaches for it. `configure` and `read` are still empty.

- **A monitoring seeder now exists (3 devices, 8 checks) and it is the first seeder that must write through a service-layer helper.** A device written without an `IMonitoringConfigLog.RecordAsync` in the same transaction is invisible to every poller forever — including to a full snapshot, which is built from the same log — and looks from the outside exactly like a broken poller. `MonitoringDemoSeederIntegrationTests` asserts a config-change row per device. This is a departure from WP-2.8's "seeders write through the DbContext"; the two are not separable here.

- **snmpsim was pulled forward from WP-3.12.** One container (`tandrup/snmpsim`, `src/AppHost/snmpsim/*.snmprec`) serves two device profiles by community string — `healthy` and `degraded` — so the seeded estate has a quiet device and a struggling one without a second container. `docker stop` on it is the "target goes away" step. The recordings are plain text and **must stay sorted by OID** or snmpsim refuses to index them. WP-3.12 still owns the mock HTTP target and the E2E test.

- **The simulator is addressed as `snmpsim:161` on the Aspire session network, and publishing a host port for it is the wrong answer.** The first live walk of this package seeded `host.docker.internal:1161` and every SNMP check timed out: Aspire proxies a published endpoint through DCP, which binds **loopback**, so the port answered from the host and not from the poller's container — the same trap WP-2.7 hit with the pinned SPA/API ports. The resource therefore declares **no endpoint at all**; the poller and the simulator are containers on one network and Aspire's resource name resolves there. **Anything else that needs to reach a dev-only container from another container should do the same rather than reach for `WithEndpoint`.** Consequence for verification: stopping the simulator now takes its ICMP *and* SNMP checks down together, because the name stops resolving — a cleaner "the device went away" than the host-address version, which kept answering pings.

- **Nothing in this package evaluates a threshold.** Warning and critical values travel to the poller and are not read by it. WP-3.5's state machine consumes the telemetry instead, which is also why maintenance windows are still fetched and still ignored: a poller that stopped measuring during a window would leave a hole in the metrics exactly where a change was being made.

- **⚠️ The Phase 2 gate's two non-demo tasks are still outstanding.** The manual checklists were walked and accepted (2026-08-10), and Phase 3 has started, but **`v0.3-phase2` was never tagged** and **`aspire update` was never run** — both were part of the gate. Tagging retroactively means picking a commit; `04e0f82` (WP-2.11) is the last Phase 2 commit and is the honest choice. The `aspire update` should happen on its own branch, not folded into a feature package.

- **A monitored device is a CI plus an address, and nothing stops that CI being deleted (WP-3.1).** `assets.ci` delete is guarded against relationships (WP-2.3) and ticket links (WP-2.4), but not against `monitoring.monitored_devices` — that would need an `IMonitoredDeviceDirectory` port in `Platform/Integration` implemented by Monitoring, and the WP text did not call for it. Consequence today: deleting a monitored CI leaves the device pointing at nothing, and its `CiName` reads null on the device list and in the poller config. **The port is the fix, and it belongs to whichever WP first cares.**

- **Config deltas are per *device*, not per field or per check.** A check edit re-sends its whole device; the poller's unit of work is a device. `PollerConfigDeltaPlanner` decides "changed" against "removed" purely by whether the device is *currently* enabled and in the poller's group — so deleting, disabling and moving a device between groups all come out right without the planner reading the change kind. A device that moves groups deliberately writes **two** rows (upsert against the new group, removal against the old).

- **Config versions are allocated by the application under `pg_advisory_xact_lock`, not by a sequence or identity column.** This is deliberate and load-bearing: identity values can commit out of order, so a poller reading `max(version)` while a lower version was still uncommitted would never see that change again. Every write in the module must therefore call `IMonitoringConfigLog.RecordAsync` **inside its own transaction** — the lock is transaction-scoped. Anything that writes devices or checks outside these services and skips it will be invisible to every poller.

- **`GET /api/pollers/{name}/config` writes.** It records `last_config_version`/`last_config_fetched_at` on the poller row. It is the only write behind a GET in the module and it is deliberately **not** audited — a poller reading its own config has no before/after entity state. WP-3.2's heartbeat is the consumer of those two columns.

- **⚠️ Two real defects in WP-3.2 were found by verification and fixed (2026-08-10). Both had shipped looking correct and both passed the original test suite.** (1) **The Python poller's MassTransit envelope carried `sourceAddress`/`destinationAddress`, and MassTransit parses those as absolute URIs during deserialisation, before any consumer runs.** `exchange://Contracts.Events:PollerHeartbeat` reads as a host and a port, so every heartbeat dead-lettered with `System.UriFormatException: Invalid URI: Invalid port specified` — the poller logged "Heartbeat published", the broker accepted it, and 36 beats went to `PollerHeartbeat_error` while nothing on either side looked wrong. **Both fields are now omitted** (they are optional and carry nothing the consumer needs; the poller names itself in a `poller-source` header). The gap that let it through was that no test ever read the other language's output — now `services/poller/tests/fixtures/heartbeat-envelope.json` is a committed fixture asserted on the Python side by `test_bus.py` and on the .NET side by `PollerEnvelopeTests`, which checks the message URN, deserialises the payload with MassTransit's own serializer options, requires every property of the contract to be present, and requires every address field to be absent or an absolute URI. **Any future field on that envelope has to survive both.** (2) **The heartbeat's monotonic guard compared a `DateTimeOffset` at 100ns tick precision against a `timestamptz` truncated to microseconds**, so a redelivered beat looked newer than itself and was applied twice — the same precision trap the WP-3.1 note records, in production code rather than in a test. Both sides are now truncated to stored precision before comparing.

- **`PollerBusCredentialIntegrationTests` starts a RabbitMQ container of its own and must keep doing so.** It imports the rendered definitions document, and an import is a broker-wide act — run against the shared `InfrastructureFixture` broker it silently took the topology out from under `PollerHeartbeatBusIntegrationTests`, whose MassTransit endpoint then never finished starting and whose heartbeats never arrived. The symptom was a test that passed alone and failed in the full suite with no error queue and no dead letters, which is what "the topology moved" looks like from the outside. **Nothing else may import definitions into the shared broker**; the helper that used to make that easy was deliberately removed from `InfrastructureFixture`.

- **The poller now has an identity of its own, and `CanPoll` is disjoint from every operator policy.** `POST /api/pollers/registrations` and `GET /api/pollers/{name}/config` require the `Poller` realm role — **not Admin, not Technician** — while `GET /api/pollers` stays on `CanManageMonitoring` and is refused to a poller. Consequence for hand-verification: those two endpoints can no longer be curled with a user token; get a poller token with `client_credentials` against `it-platform-poller` (secret: `dotnet user-secrets list --project src/AppHost | grep poller-client-secret`).

- **Keycloak's issuer is now pinned with `KC_HOSTNAME`, derived from `public-host`.** Before WP-3.2 every client called Keycloak by the same name it was configured with, so the issuer was right by accident; the poller runs in a container and must dial `host.docker.internal`, which would otherwise mint a token the API rejects as `invalid_token`. If a future resource cannot reach Keycloak, this is the setting to look at first — and it must stay in step with `public-host`, exactly like the realm's redirect URIs.

- **The RabbitMQ broker is now provisioned from a rendered definitions file, and the poller's account is publish-only.** AppHost writes `src/AppHost/obj/rabbitmq/definitions.json` (accounts, permissions, and the heartbeat exchange) plus a `conf.d` snippet pointing at it. The renderer is `Platform/Messaging/RabbitMqDefinitions.cs` **so the tests import the shipped document rather than a copy** — `PollerBusCredentialIntegrationTests` proves the poller can publish the heartbeat and cannot declare, consume, or publish anywhere else. The document contains the *platform's* account too: a definitions import can otherwise suppress the image's default-user creation, and both accounts have to survive a boot.

- **The heartbeat exchange is declared by the definitions file, not by the poller**, because the poller has no `configure` permission. Its name, type (`fanout`) and durability must match what MassTransit declares for `Contracts.Events.PollerHeartbeat` or the API's bus start-up fails with PRECONDITION_FAILED. `RabbitMqDefinitionsTests` compares it against `MessageUrn.ForType<PollerHeartbeat>()`, so renaming or re-namespacing that event breaks a test rather than the running stack.

- **The heartbeat consumer does not use the Platform dedupe helper, and that is deliberate.** It is idempotent by construction: the stored heartbeat only ever moves forward, so a redelivery or an overtaking message is a no-op. A dedupe row per beat would be one row every 15 seconds per poller, forever, to protect an update that is already safe to repeat. **This is a departure from ARCHITECTURE.md §4's "use the Platform dedupe helper" and the only one in this package** — a future consumer of poller telemetry should not copy it without the same argument.

- **Module consumers are registered through a new optional `configureBus` callback on `AddPlatformServices`.** MassTransit is configured once per host, but a consumer belongs to the module that reacts, and Platform may not reference a module. `Web.Host/Program.cs` passes `MonitoringServiceCollectionExtensions.AddMonitoringConsumers`; the next module with a consumer adds its own the same way.

- **`PollerHeartbeatMissed` is a fact, not an alert.** Nothing consumes it yet. **WP-3.5 did not consume it either** — its rules are all per monitored *check*, and a poller going quiet is a fact about the platform rather than about a device. WP-3.6's ticket automation remains its intended consumer; the evaluator deliberately does not raise an alert, open a ticket or notify anyone. What it does do is write an audit entry (`Poller` / `HeartbeatMissed`), which is also what flushes the outbox.

- **Detection is "two of the poller's own intervals, plus up to one evaluation tick".** Defaults: poller cycle 15s, `Monitoring:Heartbeat:MissedThreshold` 2, `Monitoring:Heartbeat:EvaluationIntervalSeconds` 10 — so a stopped poller is reported 30–40s after its last beat; the live walk measured **37.7s, i.e. 2.5 cycles**. **The WP text asks for "within 2 cycles" and that is not literally achievable with a two-beat threshold** — the threshold is not crossed until 2 cycles have elapsed, so any detection is necessarily later than that. Dropping the evaluation interval to 5s narrows it to 30–35s; going below two cycles means lowering the threshold, which is what makes a single slow cycle an outage. The interval is the poller's, reported on each beat, so a slow poller is judged by its own cycle. **A poller that has never sent a heartbeat is never reported**: it has no interval to be late by, so a registration that never started is silent rather than alerting.

- **The poller is a container built from `services/poller/Dockerfile` via `AddDockerfile`, not an Aspire Python resource**, so `docker stop` is a real verification step. It exposes no endpoint and holds two credentials (the OIDC client secret and the AMQP URL), both injected by AppHost from generated persisted parameters. It was run under a live `aspire run`, stopped with `docker stop`, and restarted — registration, config fetch, heartbeat, silence detection and recovery all confirmed end to end.

- **The poller never crashes on a failed cycle, and never clears its configuration because of one.** A failed registration, config fetch or publish is logged and the cycle continues with what it already held. The one exception is start-up: a missing environment variable is fatal by design. A rejected `sinceVersion` makes it forget everything and take a full snapshot in the *same* cycle rather than the next.

- **The `.venv` under `services/poller` is gitignored, and CI now installs, lints, type-checks and tests the poller** between the .NET and web steps. `ruff` and `mypy --strict` are both clean; there is no `uv` in this repo yet, so it is plain `pip install -e '.[dev]'`.

- **Nothing in the SPA touches monitoring yet.** WP-3.1 is API-only: no React, no nav item, no screen. The endpoints are reachable only from a REST client until WP-3.9 builds the dashboards. Same position `/api/ci-custom-fields` and `/api/ticket-categories` are in.

- **The monitoring surface publishes one event and seeds no data.** Devices, checks and windows are audited but nothing is published (the WP-2.6 contracts/vendors precedent); the single exception is WP-3.2's `PollerHeartbeatMissed`, which nothing consumes yet. There is no seeder — so a fresh `aspire run` has an empty `monitoring` schema and every demo device has to be created by hand or by WP-3.12's simulator rig. If Phase 3's demos need a fixture that survives an AppHost restart, it needs a seeder like the CMDB got in WP-2.8.

- **Never assert `Assert.Equal` on a timestamp that crosses Postgres.** A create response is built from the in-memory entity at .NET's 100ns tick precision; the same row read back carries `timestamptz`, which keeps microseconds. An exact compare therefore passes about one time in ten — it cost a flaky `RegisterPoller_Twice_KeepsOneRegistration` in this WP before it was caught. Compare with a tolerance (a millisecond cleanly separates truncation from any real difference) or assert the ordering instead.

- **Device list search covers the address only.** A CI's name lives in the Assets schema, which Monitoring may not query, and `ICiDirectory` answers by id rather than by search term. Searching devices by CI name would mean widening the port.

- **The asset table sorts in the browser only, and says so (WP-2.11).** `/api/cis` orders by `Name` then `Id` (`CiService.cs:96`) and takes no sort parameter, while the list pages at 25 — so a header click reorders the page on screen and nothing beyond it. The footer states "Sorted within this page of N — not across all M" whenever a sort is active and more than one page exists. **Sorting the whole estate needs a `sort` parameter on `/api/cis`**, which is a backend change no WP owns yet. The ticket list has the same client-side mechanism and gets away with it only because it fetches a single page of 200.

- **The CI page's Relations card is now three sections, and the trees are built in the browser from the traversal edges.** `buildDependencyTree` in `web/src/features/assets/relationships.ts` walks `ancestors`/`impacted-by` edges in the traversal's own direction to recover which CI a far one is reached *through* — the endpoints return hop distance, never the route. A CI reachable two ways is drawn once and marked; that is what keeps a diamond finite and a cycle terminating. Depth starts at 3 per direction and "Show deeper" adds 3 up to the server's ceiling of 10.

- **Lifecycle pills on the direct-relationship cards are read out of the traversal nodes, not the edge.** `CiRelationship` carries both ends' names and types but no lifecycle state, and every directly related CI is a depth-1 node of one walk or the other. If the relationships endpoint is ever consumed somewhere without a traversal beside it, that pill has no source.

- **`formatDateOnly` (`web/src/lib/utils.ts`) is the only correct way to render a `DateOnly` field.** `new Date('2026-09-14')` is UTC midnight and renders as the 13th anywhere west of Greenwich. WP-2.11 applied it to the CI page's coverage dates only; **`/contracts` still renders raw ISO strings** at `ContractListPage.tsx:114` and `ContractDetailPage.tsx:117-118` and should adopt it when that screen is next touched.

- **The `/assets` KPI tiles cost four extra list calls per page load** (`pageSize=1`, read `total`). They share the `['cis']` query key, so the existing invalidation refreshes them after any edit and there is no second source of truth. A tile that fails to count reads "Unavailable" rather than 0, and there is deliberately no "from last week" delta because nothing records a historical count.

- **Two known gaps were deliberately left out of WP-2.11 because both need backend work.** (1) Lifecycle history and the check-in/out log print raw `actorId` values — `technician1` for seeded rows, a Keycloak `sub` for anything done in the UI; Helpdesk solved this by snapshotting display names in WP-1.7 and Assets never did, so fixing it is a schema plus service change. The frontend-only workaround does not work: seeded ids are usernames, not directory ids, which is the same identity mismatch the People page already has. (2) Cross-page sorting, above.

- **The import wizard now accepts one sheet of everything (WP-2.10).** "Mixed — read from a column" sits beside the six CI types; the mapping step then offers the union of every type's attributes and custom fields, each labelled with the types that need it, plus a mappable **CI type** column. A row reads only the columns its own type declares and ignores the rest — but **only in mixed mode**: a single-type import still rejects a foreign attribute loudly, because there the operator declared the whole file to be one shape.

- **A mapped type column is authoritative and a blank cell in it is an error, not a fallback to guessing.** Inference only runs when no type column is mapped. It reads the attribute keys exactly one type declares — derived from `CiTypeSchema`, not a hand-written list, so `hostname`, `vendor` and `ramGb` (shared by two types each) identify nothing. A row that matches two types, or none, is refused by line number rather than resolved by priority.

- **A guessed type cannot be committed unseen: `CommitAsync` refuses (400) unless the mapping carries `acceptInferredTypes`.** The wizard sets it only on the button that follows the dry run, so the flag means "the operator read the guesses". This is a server-side rule, not a UI convention — a REST client scripting the commit has to send it too. The reason is that TPH type is permanent: fixing a wrong guess means deleting the CI, which the WP-2.3/2.4 delete guards block as soon as a relationship or ticket names it.

- **The CMDB now seeds itself: 60 CIs, 61 relationships, 6 vendors, 6 contracts, 2 CI custom fields and 18 ticket links, all from `dotnet run --project src/Seeder`.** The estate is three site-local dependency trees — Primary Data Centre (6 levels deep), Head Office (4) and Regional Branch (6) — each rooted at that site's router. There are deliberately **no WAN edges between the sites**, so `impacted-by` on a router answers for one site rather than the whole estate; a real network would have them and WP-4.3's topology maps are where they belong.

- **The estate is a hand-written table in `AssetsEstate`, and ids come from array position.** Appending a CI, contract or relationship is safe; **reordering or inserting in the middle renumbers everything after it**, which on a live database means duplicates rather than updates. The dev database is recreated constantly so this is cheap to recover from — but it is the one way to break the seeder's idempotency.

- **The seeder writes through `AssetsDbContext`, so seeded CIs are not audited and publish no events.** That is deliberate (they are reference data, not operator actions), and it is the opposite of the WP-2.5 importer, which routes every row through `ICiService`. The consequence is that `CiTypeSchema` is not applied automatically: `AssetsInfrastructureSeeder.Materialise` binds through it by hand, and `AssetsEstateTests` asserts the whole estate conforms. Anything else that ever writes CIs outside `CiService` has to do the same.

- **Seeded warranties and contracts are on purpose close to expiry, so the WP-2.6 renewal job fires on its first pass.** Two contracts (Dell ProSupport at +21 days, Northwind ERP support at +5) and several warranties sit inside the 30/7/0 windows, and one contract plus four warranties are already expired. Expect a handful of `assets.contract_notifications` rows and log lines within a minute of the first `aspire run` — that is the feature working, not a fault. Retired and Disposed CIs are skipped by the job, so the two dated-out user assets stay quiet.

- **All dates are offsets from the day the seeder runs**, not fixed calendar dates, because a fresh database is minutes old and fixed dates drift. Re-running on a later day does **not** move the dates of rows already written — only newly created ones use the new "today".

- **The seeded estate depends on the platform demo data and fails loudly without it.** `AssetsInfrastructureSeeder` checks every username, department code and site code the estate names up front and throws one sentence before writing anything. Run order in `src/Seeder/Program.cs` is fixed: platform → helpdesk reference → helpdesk history → assets estate → ticket links.

- **Ticket↔CI links are seeded by a second class, `HelpdeskCiLinkSeeder`, which takes the CI ids as an argument** — Helpdesk owns the link and may not reference Assets. It links the 6 newest tickets in each of three categories (Laptop or desktop → hardware, Network and connectivity → network devices, Business applications → business services), 18 in total. Tickets that are on order, retired or disposed are never linked.

- Both seeded CI custom fields (`purchase_order` on Hardware, `backup_schedule` on Server) are **optional**. A required one would make every CI created by hand — and the existing API tests that post one — fail validation. If the required-field path ever needs demonstrating, that is a deliberate change with test fallout.

- The seeded estate has **no cycle** in its graph, so `containsCycle` is false everywhere. Cycles are supported (WP-2.3) but a seeded one would make every demo traversal report a cycle, which reads as a fault.

- **⏳ WP-2.7 owes one check to WP-6.2: signing in on a phone.** A printed label was scanned with a real phone and resolved to the correct asset URL, and label generation, batch sheets, code lookup (asset tag / serial / label URL / unknown) and the 401 and 404 failure paths were all confirmed against the running stack. What could **not** be completed is the sign-in that follows: `oidc-client-ts` always uses PKCE for `response_type: 'code'`, PKCE needs `crypto.subtle`, and browsers expose it only in a secure context — HTTPS or `localhost`. On `http://<lan-ip>:5173` the sign-in button throws before it navigates and looks dead. **This is a platform rule, not a bug, and no configuration fixes it.** When WP-6.2 (Proxy + TLS) lands, re-run the phone scan end to end; that is the last unproven claim in Phase 2.

- **Three real defects in WP-2.7's LAN plumbing were found by verification and fixed (2026-08-08).** All three had shipped looking correct: (1) the pinned ports bound to loopback only, because pinning a port does not make it reachable — Aspire's DCP proxies to `localhost` unless `EndpointAnnotation.TargetHost` says otherwise, so the LAN address was refused; (2) `public-host` is now read from `builder.Configuration["Parameters:public-host"]`, because `AddParameter(name, value)` takes a *given* value; (3) the realm's `${PUBLIC_HOST}` placeholder never substituted anything — see the next note.

- **Keycloak's realm import does not resolve placeholders from the environment, so AppHost renders the realm itself.** Probed directly against Keycloak 26.3: `${PUBLIC_HOST}` stays literal and fails the import with "A redirect URI is not a valid URI", while `${PUBLIC_HOST:localhost}` and `${env.PUBLIC_HOST:localhost}` both silently collapse to their default — whether the value arrives as a container environment variable or a `-D` system property. AppHost now substitutes `${PUBLIC_HOST}` into `src/AppHost/obj/keycloak/it-platform-realm.json` and bind-mounts that; the template keeps the bare token. Confirmed by importing a rendered copy into a throwaway Keycloak and reading the client back. **Do not reintroduce a `:default` placeholder** — it fails silently and looks configured.

- **Reaching the stack from a phone needs two things outside this repo, both on the Windows host.** WSL must be in mirrored mode (already set in `C:\Users\localadmin\.wslconfig`), and WSL's Hyper-V firewall blocks all inbound by default — `Get-NetFirewallHyperVVMSetting` shows `DefaultInboundAction: Block`, which drops LAN packets silently. A targeted rule opens only what is needed: `New-NetFirewallHyperVRule -Name "WSL-it-platform-lan" -Direction Inbound -VMCreatorId '{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}' -Protocol TCP -LocalPorts 5000,5173,8080 -Action Allow` (elevated). Remove it when not verifying — it exposes the Keycloak admin console and the API to the LAN.

- **During a LAN run the *desktop* browser cannot reach the stack at all.** Windows reaching its own mirrored WSL IP is treated as loopback and needs `hostAddressLoopback=true` under an `[experimental]` section in `.wslconfig` plus `wsl --shutdown`. Until that is set, a LAN run is phone-only: the desktop cannot even load `/assets`, so PDFs have to be fetched another way (this session pulled them to the Windows desktop with `curl` from WSL). A plain `aspire run` on `localhost` is unaffected.

- **Reaching the stack from another device is now one parameter: `env 'Parameters__public-host=<lan-ip>' aspire run`.** It must be `env` (or `dotnet user-secrets set "Parameters:public-host"`) — the hyphen makes it an illegal shell identifier, so a bare `Parameters__public-host=… aspire run` prefix is refused by bash before Aspire ever sees it. It feeds the Keycloak authority, the API base URL, the CORS origin, the label base URL and the realm's redirect URI together, because Keycloak stamps its token issuer from the host it was called on and the API demands an exact match — mixing `localhost` and the LAN address anywhere in that chain gives `invalid_token`. Default is `localhost`, which is exactly the previous behaviour.

- **Aspire ports are now pinned** — Keycloak 8080, API 5000, SPA 5173 — because the values above have to be predictable before the run starts. This replaces dynamic allocation, so the `ss -lntp | grep Web.Host` dance in the API-token recipe is no longer needed, and a port clash now fails the run instead of being routed around.

- **The `it-platform-keycloak-data` volume was recreated on 2026-08-10** so WP-3.2's `it-platform-poller` client and `Poller` role would import; any change made by hand in the Keycloak admin console before that date is gone. The rule below is why, and it bit again — the first live run of the poller 401'd on every token request because the client simply was not in the realm.

- **The realm import only runs against an empty Keycloak volume.** The redirect-URI change is therefore invisible on an existing database: `docker volume rm it-platform-keycloak-data` (with the stack down) before the first LAN run, or add the URI by hand in the admin console.

- **The printed QR carries an absolute URL, so it is only as good as `Assets:Labels:PublicBaseUrl`.** AppHost now sets it from `public-host`; it otherwise defaults to `http://localhost:5173`, falling back to `WebClient:Origin` when blank. A sticker outlives the config, so a label printed against the wrong base URL is wrong until it is reprinted — set `public-host` before printing anything real.

- WSL2 here runs in NAT mode (`.wslconfig` has no `networkingMode`), so the LAN cannot reach WSL at all until `networkingMode=mirrored` is set and `wsl --shutdown` run. Without it the `public-host` parameter is correct but unreachable.

- `/scan` is inside the authenticated agent shell, so a phone has to complete a Keycloak sign-in once before a scan resolves. The redirect after login lands on the scanned asset.

- The scan box takes a typed or wedge-scanned code — there is **no in-browser camera scanner**, because `getUserMedia` needs a secure context and a LAN address over plain HTTP is not one. The phone's own camera app is the camera scanner; this page is for handheld scanners and codes read by eye.

- Label printing writes nothing and is **not audited**, unlike every other CI surface. If "who printed what, when" is ever wanted it needs its own table — the audit log will not have it retroactively.

- QuestPDF is used under its **Community licence** (free below $1M revenue) and is the second file-format dependency after ClosedXML. `QuestPDF.Settings.License` is set in `CiLabelDocument`'s static constructor; nothing else in the solution may render PDFs without doing the same.

- A batch sheet is capped at 200 labels and refuses the whole sheet (404) if any selected CI has since been deleted. The list page's selection is per-page, so "print labels" only ever covers what is on screen — the same WP-2.5 rule bulk edit follows.

- Label layout is fixed in code (`LabelSpec`): Standard 63.5 × 33.9 mm three-up, Small 45.7 × 21.2 mm four-up, both on A4 at 100% scale. There is no custom-size or custom-content option, and "fit to page" printing will misalign both against label stock.

- WP-2.6's 21 integration tests and 19 unit tests pass against a real Postgres (Testcontainers); the manual checklist has **not** been walked yet.

- The expiry pass skips inactive contracts and Retired/Disposed CIs, so a warranty on an asset that has left the estate never raises a notice.

- The expiry job runs daily **and at host start-up** (`StartNow` + 24h), and `POST /api/contract-notifications/runs` ("Check renewals now" on the contracts page) triggers the same pass by hand. Both are safe because a notice is deduped by its own recorded row; the manual trigger exists because the dev database resets on most AppHost restarts, so a hand-made fixture never survives to the next scheduled run.

- A notice fires at the **tightest crossed threshold only** — 30, then 7, then the expiry day. Consequence: a CI whose warranty is already 3 days out when it is first entered gets one notice (at 7), not three. Moving an end date starts a fresh cycle because the due date is part of the dedupe key.

- Notification *delivery* is still whatever `INotificationService` does: with `Email:Smtp:Enabled` false it writes a log line and nothing more. The `assets.contract_notifications` row is the durable record, and `GET /api/contract-notifications` reads it back.

- A CI's coverage (contract, purchase date, warranty end) is set through `PUT /api/cis/{id}/coverage`, **not** the CI create/update payload, so the WP-2.5 importer cannot touch or clear it. Importing warranty dates would need that endpoint wiring into the importer — nobody owns it yet.

- Coverage is a complete statement: `PUT` with an empty body releases the CI and clears both dates. The UI dialog always sends all three fields, so this only bites a REST client sending partial JSON.

- `assets.vendors` is a new entity and is **not** connected to the free-text `vendor`/`manufacturer` attributes on Network device, Software and Hardware CIs. Two places now say "Cisco"; reconciling them was deliberately left out of scope.

- Contracts and vendors are audited but publish **no** events — nothing consumes them yet. If Phase 3 wants to react to an expiry, add a `ContractExpiring` event to `Contracts` rather than reading the notification table.

- A contract cannot be deleted while any CI names it (409, `Restrict` FK), and a vendor cannot be deleted while it has contracts (409). Both mirror the CI delete guard.

- Contract and warranty status ("Active / Expiring soon / Expired") is computed against today at read time, never stored, and "expiring soon" is the same 30 days the job notices on (`ContractExpiryCalculator`).

- `/api/cis` now also accepts `contractId` and `warrantyExpiringWithinDays`. The contract page's covered-asset list uses the first; the second has no UI yet and exists for WP-2.8's seeded estate and any future "warranties expiring" board.

- WP-2.5's branch is `feat/wp-2.5-import-wizard-bulk-edit`, not the `feat/wp-2.5-import-bulk-edit` recorded here before the session. Its 20 integration tests and 21 unit tests pass against a real Postgres (Testcontainers); the manual checklist has **not** been walked yet.

- **ClosedXML 0.105.1 is the first third-party file-format dependency in the solution** (`Modules.Assets`), pulled in only to read `.xlsx`. It brings `DocumentFormat.OpenXml` transitively. CSV is parsed by `CiImportFileReader`'s own RFC 4180 reader, so a CSV-only deployment never touches it. Uploads are untrusted input, so the workbook reader is wrapped in a catch-all that turns any parser failure into one 400 sentence.

- The importer writes exclusively through `ICiService`, which means one transaction, one audit entry and one outbox message **per row**. Fine for the 5000-row ceiling; a bulk-load path (a future seeder, WP-2.8) should not reuse it.

- Import dedupe is serial-first, asset-tag-second, both case-insensitive. A row carrying neither is always a create — the mapping validator refuses a mapping that maps neither, which is what makes "run it twice, get no duplicates" true.

- A mapped **blank** cell means "leave the stored value alone", never "clear it". Consequence: an import can never empty a field. Clearing still needs the form or the API.

- A **single-type** import is still the default and is unchanged: one CI type for the whole file, mapping form derived from the type. WP-2.10 added mixed mode beside it, not in place of it.

- The importer does not touch lifecycle state, ownership, custom-field *definitions*, or relationships. Created CIs land in InStock; moving them on is bulk edit's job. Relationship import is still nobody's (WP-2.9 owns the manual write surface).

- `POST /api/cis/bulk-edit` answers **200 with a per-CI report even when rows failed** — callers must read `failed`/`rows`, not just the status code. It is capped at 200 ids, matching the list page size.

- WP-2.4's branch was `feat/wp-2.4-ticket-asset-linking-360-pages`, not the name recorded here before that session. Its 10 integration tests pass against a real Postgres; its manual checklist has **not** been walked yet either.

- Cross-module reads now go through **ports** in `src/Platform/Integration/ModulePorts.cs`: `ICiDirectory` (implemented by Assets) and `ITicketLinkDirectory` (implemented by Helpdesk). Neither module references the other; both reference Platform. Anything future that needs a cross-module read belongs here, and it stays read-only — writes and reactions still go through events. ARCHITECTURE.md §3 records the rule.

- A ticket link stores only the CI id. Card fields (name, type, lifecycle state, owner, site) are read live per request, so N linked CIs cost one extra query per ticket page. Fine at CMDB scale; if a ticket list ever wants to show linked assets inline, that read has to be batched.

- The CI delete guard now refuses relationships **and** ticket links (409, same message). The ticket-link half has no foreign key behind it — it is only the port call in `CiService.DeleteAsync`. Anything that deletes CIs outside `CiService` (a future importer, a bulk delete in WP-2.5) has to repeat that check.

- The user 360° page keys assets by directory user id and tickets by username, because that is how the two halves were seeded. A real Keycloak login presents a random `sub`, so a live requester's tickets will not appear on their People page until user ids are pinned in the realm import (the auth-configuration WP the WP-1.11 notes already ask for).

- `/api/tickets` now also accepts `ciId` and `requester`. Both are members of `TicketListFilter`, so a saved view could in principle persist them, but no UI offers them — the saved-view editor only exposes the WP-1.10 filters.

- **Relationships are now created and removed in the browser (WP-2.9).** The asset page's Relations card lists the CI's *direct* edges above the hop-banded graph, with "Relate to…" and a per-edge two-step remove. The graph itself is unchanged: still read-only, still capped at 3 hops each way, still showing nodes rather than editable edges.

- **The relate dialog always writes the open CI as the edge's source** — one sentence, "this CI ⟨runs on / connects to / depends on / is hosted on⟩ that CI". There is no direction toggle: to record the reverse, open the other CI and relate from there. Consequence: the edge list shows both directions but only ever *creates* upstream ones.

- **The picker deliberately does not filter out illegal choices.** The CI itself, a disposed CI and an already-related pair are all selectable, and the server's refusal (400 `TargetCiId`, or a 409 detail) renders as an inline error under the chosen CI. That is what makes the WP-2.3 guards demonstrable by hand; it also means every guard message the operator sees is the server's exact words, so changing one in `CiRelationshipService` changes the UI with no frontend edit.

- **A disposed CI's "Relate to…" button is disabled with a note**, mirroring the coverage dialog's freeze — the server would 409 anyway, but a dead-looking button with no explanation reads as a fault.

- **The "delete-blocked 409" named in the WP-2.9 text has no browser surface, because there is no delete-CI button anywhere in the SPA.** `assetsApi.deleteCi` exists in `web/src/api/assets.ts` and is called by nothing; the guard is real and integration-tested but reachable only from a REST client. If CI deletion is ever wanted in the UI, that 409 is the message to surface — and the relations list is where the operator will look for the reason.

- People (`/people`, `/people/:userId`) is a new agent-only nav section over `/api/directory/users`. The list filters in the browser over all users — fine for 20 seeded people, not for a real directory.

- The CI list's name column now opens the 360° page; editing moved to a per-row "Edit" button beside "Lifecycle".

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
- CI custom fields are managed through the API only (`POST/DELETE /api/ci-custom-fields`, AdminOnly) — there is no admin screen, same as the WP-1.9 ticket categories. The form reads them from `/api/ci-type-schemas`.
- CI attributes are enforced above the database: TPH makes every per-type column nullable, so `CiTypeSchema` is the only thing stopping a Server row with no hostname. Anything writing CIs outside `CiService` (a seeder, an importer) must bind through `CiTypeSchema.Bind` or it will write half-populated rows.
- `/api/cis` list search is `ILIKE` over name/asset tag/serial only — there is no full-text index on the assets schema like the one WP-1.10 added for tickets. Revisit if the CMDB grows past a few thousand rows.
- Deleting a CI now returns 409 if any relationship names it on either end (WP-2.3), backed by `Restrict` foreign keys, or if any ticket still links it (WP-2.4). Deleting an unreferenced CI still cascades away its lifecycle history and check-in/out log.
- The graph endpoints are `GET /api/cis/{id}/{ancestors|descendants|impacted-by}?maxDepth=`. Direction convention: a relationship reads source→target as "source depends on target", so ancestors walk up to what a CI needs and descendants walk down to what needs it. `impacted-by` is the descendants walk with the CI itself at depth 0.
- Traversal depth is capped at 10 (default 5) and the value is clamped, not rejected; the response echoes the effective `maxDepth` and a `maxDepthReached` flag. A graph deeper than 10 hops silently ends there apart from that flag.
- Cycles are allowed in the data and traversed safely; responses carry `containsCycle`. Nothing warns an operator at creation time that they have just closed a loop — the flag only appears once someone runs a traversal.
- The CTE's cycle guard is a per-path visited array, so a very wide diamond-heavy graph does more work than a plain visited-set walk would. Fine at CMDB scale with a 10-hop cap; revisit if WP-2.8's seeded estate or a real import makes traversals slow.
- Lifecycle and ownership live in a right-side drawer, reachable from the "Lifecycle" button on both the assets list and the CI detail page.
- `/api/directory/users|departments|sites` are new agent-only (`CanManageAssets`) read endpoints over the Platform demo directory. They are the picker source for CI ownership; if another surface needs them for EndUsers, the policy has to be revisited.
- A CI's owner/department/site are ids plus name snapshots. Renaming a department in `platform.departments` will not update the CIs already assigned to it — the same trade-off WP-1.7 made for ticket requesters.
- Disposed CIs are frozen: `PUT /api/cis/{id}`, the assignment endpoint, and any further transition all return 409. Nothing in the UI can un-dispose a CI, which is deliberate.
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
- [x] WP-2.4 Ticket↔asset + 360 pages (2026-08-07) — branch was `feat/wp-2.4-ticket-asset-linking-360-pages`, not the name previously recorded here; added `Platform/Integration` read ports and a People list + nav item, neither of which the WP text called for but the module boundary and hand-verification required
- [x] WP-2.5 Import + bulk edit (2026-08-08) — branch was `feat/wp-2.5-import-wizard-bulk-edit`, not the name previously recorded here; added ClosedXML (the first file-format dependency, `.xlsx` only) after asking, and `apiUpload`/`ApiError.errors` in the web client, neither of which the WP text called for but multipart upload and field-level mapping errors required
- [x] WP-2.6 Contracts/warranty (2026-08-08) — added a `/api/cis/{id}/coverage` endpoint rather than extending the CI payload, and a manual `POST /api/contract-notifications/runs` trigger, neither of which the WP text called for but the WP-2.5 importer and the resetting dev database respectively required
- [x] WP-2.7 Barcode/QR (2026-08-08) — **accepted with the phone sign-in leg deferred to WP-6.2; everything else verified on real hardware.** Added QRCoder and QuestPDF after asking, plus `apiDownload` in the web client, a "Scan" nav item, and a `public-host` AppHost parameter with pinned ports and a rendered realm redirect URI — none of which the WP text called for, but QR encoding, PDF layout, authorized binary downloads, a reachable `/scan` and a phone-reachable login respectively required. Verification found and fixed three defects in that LAN plumbing (endpoint bind address, parameter source, realm substitution) — see In flight
- [x] WP-2.8 Seeder: infrastructure (2026-08-08) — added a `HelpdeskCiLinkSeeder` in the Helpdesk module and a `CiIds` map on the seed result, neither of which the WP text called for, but "some linked tickets" crosses a module boundary Assets may not cross; also seeded two CI custom fields, which the WP text did not mention but the WP-2.1 notes asked for
- [x] WP-2.9 Relationship editor (UI) (2026-08-08) — frontend only; no API, migration or backend change. The WP's "delete-blocked 409" was not surfaced because the SPA has no delete-CI button to surface it on — see In flight
- [x] WP-2.10 Mixed-type import (2026-08-08) — added an `acceptInferredTypes` confirmation to the commit payload, which the WP text did not call for but "never commit an inferred type the operator has not seen" required as a server-side rule rather than a wizard convention; no API route, migration or entity change
- [x] WP-2.11 Assets UI polish (2026-08-09) — frontend only; no API, migration, entity or contract change. Scope was written item by item during the session rather than up front, and grew from three placeholders to six items; item (6) (column sorting) was added after the session flagged it as needing a backend change for a complete answer, and shipped browser-side with its limitation stated in the UI

### Phase 3 — Monitoring + Unified Loop
- [x] WP-3.1 Device/check config API (2026-08-10) — first real code in `Modules.Monitoring`, which was an empty skeleton. Added a `CanManageMonitoring` policy and a `monitoring.config_changes` change log with application-allocated versions, neither of which the WP text named but "versioned config, only deltas" required as a stored history rather than a computed diff. The poller endpoints sit behind the operator policy as an interim — WP-3.2 owns moving them (see In flight)
- [x] WP-3.2 Poller skeleton + heartbeat (2026-08-10) — added a `Poller` realm role, an `it-platform-poller` service-account client and a `CanPoll` policy; pinned Keycloak's issuer with `KC_HOSTNAME`; added a rendered RabbitMQ definitions file with a publish-only account (renderer in `Platform/Messaging` so the tests import the shipped document); and added a `configureBus` hook to `AddPlatformServices` so a module can own its consumer. The WP text named none of these, but "publish-only credentials" and the WP-3.1 carry-over about the interim policy required each. The heartbeat consumer is idempotent by construction rather than through the Platform dedupe helper — see In flight
- [x] WP-3.3 ICMP/SNMP polling (2026-08-10) — added two `Contracts` events and widened the poller's broker credential to their exchanges (SESSION.md §3.4 forbids touching bus topology; approved before implementation), added `icmplib` after asking, pulled snmpsim forward from WP-3.12 after asking, and added a monitoring seeder — none of which the WP text named, but "telemetry batched to bus" needed the first two and "verify against snmpsim: metrics flow, stop the target → down event, other devices keep polling" needed the last two. The planned `--cap-add=NET_RAW` was replaced by a `ping_group_range` sysctl after probing showed the capability does not work for a non-root container. **A WP-3.1 defect was found and left unfixed — see In flight**
- [x] WP-3.4 Metrics storage (2026-08-10) — first TimescaleDB-specific code in the solution: the migration carries raw `create_hypertable`, continuous-aggregate and policy SQL because none of it has an EF expression. Added a `monitoring.device_inventory_facts` table for text samples and two derived `check.*` metrics, neither of which the WP text named, but WP-3.3's value/text split and "a failed check is a fact" respectively required each. Narrowed one WP-0.5 outbox test's assertion, and hand-verification found and fixed a query-API defect (a metric name is not a series) — both in In flight
- [x] WP-3.5 Alert state machine (2026-08-10) — added a durable `monitoring.alerts` table and five nullable alert-tuning columns on `check_definitions` (with an `alertTuning` block on the WP-3.1 check payload), a `Monitoring:Alerting` options section, two `Contracts` events, a Redis container in the shared test fixture and a `StackExchange.Redis` reference in the module — none of which the WP text named, but "Redis-backed state machine" that must survive a flush, "hysteresis + for N cycles" that must be configurable, and `AlertRaised`/`AlertCleared` respectively required each. **`DeviceReachabilityChanged` is deliberately left unconsumed, reversing what the WP-3.4 notes predicted — see In flight.** No read endpoint: WP-3.9 owns the alert board
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
