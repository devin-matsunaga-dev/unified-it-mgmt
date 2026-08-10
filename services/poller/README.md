# poller

The IT Platform polling agent (WP-3.2).

Every cycle it registers itself if it has not already, fetches its configuration from
`/api/pollers/{name}/config` — a full snapshot the first time, then only what changed since the
version it holds — and publishes a `PollerHeartbeat` to RabbitMQ. WP-3.3 adds the ICMP and SNMP
polling this skeleton exists to carry.

It has two credentials and both are deliberately narrow:

- a **Keycloak service account** (`it-platform-poller`, client credentials) carrying the `Poller`
  realm role, which reaches its own two endpoints and nothing else on the API;
- a **publish-only RabbitMQ account**, with no `configure` and no `read` permission at all, and
  write permission on exactly the heartbeat exchange. It cannot declare a queue and cannot consume
  from one.

Both are provisioned by the Aspire AppHost; nothing here needs a secret checked into the repository.

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
| `POLLER_HTTP_TIMEOUT_SECONDS` | no | `10` | Per-request timeout |

## Development

```bash
python -m venv .venv && . .venv/bin/activate
pip install -e '.[dev]'
pytest
ruff check . && mypy
```
