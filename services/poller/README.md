# poller

The IT Platform polling agent (WP-3.2, WP-3.3).

Every cycle it registers itself if it has not already, fetches its configuration from
`/api/pollers/{name}/config` — a full snapshot the first time, then only what changed since the
version it holds — polls whichever checks are due, and publishes a `PollerHeartbeat` to RabbitMQ.

It has two credentials and both are deliberately narrow:

- a **Keycloak service account** (`it-platform-poller`, client credentials) carrying the `Poller`
  realm role, which reaches its own two endpoints and nothing else on the API;
- a **publish-only RabbitMQ account**, with no `configure` and no `read` permission at all, and
  write permission on exactly the three exchanges it publishes to. It cannot declare a queue and
  cannot consume from one.

Both are provisioned by the Aspire AppHost; nothing here needs a secret checked into the repository.

## What it polls, and what it publishes

Each check carries its own interval, so a five-minute CPU check on a fifteen-second cycle costs
twenty cycles of nothing. All of a cycle's due checks run concurrently, each inside its own timeout
and its own `except`: one dead device costs its own timeout and nothing else waits for it.

| Check type | Reads |
|---|---|
| `Icmp` | Reachability, round-trip time, packet loss. Parameter: `count` (default 3) |
| `Snmp` | `metric=sysinfo` (description, name, location, contact, uptime), `cpu`, `memory`, or `oid` |

SNMP parameters: `version` (`2c` or `3`), `port` (161), `community` for v2c; `securityName`,
`authProtocol`, `authKey`, `privProtocol`, `privKey` for v3; `retries` (1). For `metric=oid`, also
`oid`, and optionally `metricName` and `unit`. Credentials live in the check's parameters until the
credential vault lands in WP-3.11.

`cpu` reads `hrProcessorLoad` and averages it, falling back to UCD-SNMP's `ssCpuIdle`; `memory`
reads the physical-memory rows of `hrStorage`, falling back to `memTotalReal`/`memAvailReal`. A
device that answers neither source reports a failed check rather than a zero.

Two messages come out:

- **`DeviceTelemetryReported`** — one batch per cycle, carrying every check's result including the
  failed ones. A timeout is a fact about the device.
- **`DeviceReachabilityChanged`** — on the transition only, so a device down for an hour says so
  once. An ICMP check decides reachability where a device has one; otherwise any check that
  completed proves the device is there. The recovery event carries the length of the outage.

Nothing here evaluates a threshold: thresholds travel with the configuration, and the alert state
machine is WP-3.5.

## ICMP and privileges

ICMP needs a raw socket or an ICMP datagram socket. The container runs as a non-root user and takes
the second: `--cap-add=NET_RAW` does *not* work for a non-root process — Docker adds the capability
to the permitted set and a process with no file capability on its binary has an empty effective set.
AppHost sets `net.ipv4.ping_group_range` to the Dockerfile's uid instead, which is both narrower and
the one that works. `POLLER_ICMP_PRIVILEGED=true` selects the raw socket for running outside a
container as root.

## Configuration

All of it comes from the environment, is read once at start-up, and a missing value is fatal.

| Variable | Required | Default | Meaning |
|---|---|---|---|
| `POLLER_NAME` | yes | — | This poller's stable name; registration upserts on it |
| `POLLER_GROUP` | no | `default` | The device group it is responsible for |
| `POLLER_AGENT_VERSION` | no | `0.0.0` | Reported at registration and on every beat |
| `POLLER_INTERVAL_SECONDS` | no | `15` | Cycle length, and the unit "missed N heartbeats" counts in |
| `POLLER_API_BASE_URL` | yes | — | The platform API |
| `POLLER_OIDC_TOKEN_URL` | yes | — | Keycloak's token endpoint |
| `POLLER_OIDC_CLIENT_ID` | yes | — | `it-platform-poller` |
| `POLLER_OIDC_CLIENT_SECRET` | yes | — | The service account's secret |
| `POLLER_AMQP_URL` | yes | — | The publish-only broker credential |
| `POLLER_HEARTBEAT_EXCHANGE` | no | `Contracts.Events:PollerHeartbeat` | Where the beat is published |
| `POLLER_TELEMETRY_EXCHANGE` | no | `Contracts.Events:DeviceTelemetryReported` | Where a cycle's measurements go |
| `POLLER_REACHABILITY_EXCHANGE` | no | `Contracts.Events:DeviceReachabilityChanged` | Where up/down transitions go |
| `POLLER_HTTP_TIMEOUT_SECONDS` | no | `10` | Per-request timeout |
| `POLLER_MAX_CONCURRENT_CHECKS` | no | `50` | Checks in flight at once |
| `POLLER_ICMP_PRIVILEGED` | no | `true` | Raw ICMP socket; AppHost sets this `false` |

## Development

```bash
python -m venv .venv && . .venv/bin/activate
pip install -e '.[dev]'
pytest
ruff check . && mypy
```
