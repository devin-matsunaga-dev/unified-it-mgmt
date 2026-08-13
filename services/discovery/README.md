# discovery

The IT Platform discovery agent (WP-4.1).

Every cycle it fetches the scan profiles for its group from
`/api/discovery/{group}/scan-profiles`, runs whichever of them are due, and publishes one
`DeviceDiscovered` per device it found. Nothing consumes those events yet — matching a discovery to a
CI and queueing the rest for human review is WP-4.2.

It has two credentials and both are deliberately narrow:

- a **Keycloak service account** (`it-platform-discovery`, client credentials) carrying the
  `Discovery` realm role, which reaches that one endpoint and nothing else on the API. It is
  deliberately *not* the `Poller` role: a scanner has no devices to configure and no credential scope
  to redeem, so a stolen scanner token buys nothing the credential vault protects;
- a **publish-only RabbitMQ account**, with no `configure` and no `read` permission at all, and write
  permission on exactly one exchange. It cannot declare a queue and cannot consume from one.

Both are provisioned by the Aspire AppHost; nothing here needs a secret checked into the repository.

## What a scan does

Each profile carries its own interval, so an hourly sweep on a thirty-second cycle costs a hundred
and nineteen cycles of nothing. Profiles run one at a time — each is already hundreds of probes in
flight, and the concurrency bound stops meaning anything if two run together.

1. **Expand** the profile's ranges into addresses. Four forms are accepted: `local`, a CIDR block, an
   inclusive span (`10.0.0.5-40` or `10.0.0.5-10.0.0.40`), and a single address. A block wider than a
   /31 omits its network and broadcast addresses.
2. **Sweep** — one ICMP packet per address, bounded concurrently. A /24 finishes in seconds.
3. **Fingerprint** — a TCP connect to each of the profile's ports. Run against *every* address rather
   than only the ones that answered a ping, because that is the only way a host that filters ICMP is
   ever found. With no ports configured, the step is skipped entirely.
4. **Resolve** — reverse DNS, for the addresses that were found and no others.
5. **Identify** — SNMP v2c `GET` of the system group (`sysDescr`, `sysObjectID`, `sysName`,
   `sysLocation`, `sysContact`, `sysUpTime`), trying each configured community in order and stopping
   at the first that answers.
6. **Neighbours** — LLDP (`lldpRemTable`) and CDP (`cdpCacheTable`) walks, using the community that
   already worked. Both protocols are reported additively: a switch with both switched on reports the
   same link twice, and deciding they are one edge is WP-4.3's topology work.

Nothing here writes to the CMDB, and nothing decides what a discovery *means*.

### `local`

`local` resolves to the subnet the scanner is attached to, read from `/proc/net/route`, **narrowed to
at most a /24 around the scanner's own address**. Docker hands a user-defined network a /16, so the
interface genuinely reports one — and a /16 is 65,534 probes to find the handful of containers in the
first /24 of it. The narrowing is logged with both numbers. For a wider sweep, write the CIDR out.

### SNMP communities

The communities to try are this service's own configuration, not the credential vault's. A scan meets
devices that are not monitored yet, so there is no check for the vault to scope a credential to and
nothing for WP-3.11 to release. Anything found this way is *identified*, never polled: the credential
a device is monitored with is still the vault's, and this service cannot reach the vault at all.

The community that answered is **never published**. It is the one thing a scan learns that is a secret
in a real estate, and `DeviceDiscovered` travels the bus and lands in a review queue. The log line
carries the community's *position* in the configured list instead.

## ICMP and privileges

Identical to the poller's arrangement. ICMP needs a raw socket or an ICMP datagram socket; the
container runs as a non-root user and takes the second, because `--cap-add=NET_RAW` does not give a
non-root process an *effective* capability. AppHost sets `net.ipv4.ping_group_range` to the
Dockerfile's uid. `DISCOVERY_ICMP_PRIVILEGED=true` selects the raw socket for running outside a
container as root.

## Configuration

All of it comes from the environment, is read once at start-up, and a missing value is fatal.

| Variable | Required | Default | Meaning |
|---|---|---|---|
| `DISCOVERY_NAME` | yes | — | This scanner's name; carried on every event it publishes |
| `DISCOVERY_GROUP` | no | `default` | Which group's scan profiles it runs |
| `DISCOVERY_AGENT_VERSION` | no | `0.0.0` | Reported in the start-up log line |
| `DISCOVERY_INTERVAL_SECONDS` | no | `30` | How often it wakes to see what is due — not how often it scans |
| `DISCOVERY_API_BASE_URL` | yes | — | The platform API |
| `DISCOVERY_OIDC_TOKEN_URL` | yes | — | Keycloak's token endpoint |
| `DISCOVERY_OIDC_CLIENT_ID` | yes | — | `it-platform-discovery` |
| `DISCOVERY_OIDC_CLIENT_SECRET` | yes | — | The service account's secret |
| `DISCOVERY_AMQP_URL` | yes | — | The publish-only broker credential |
| `DISCOVERY_DISCOVERED_EXCHANGE` | no | `Contracts.Events:DeviceDiscovered` | Where a discovery goes |
| `DISCOVERY_HTTP_TIMEOUT_SECONDS` | no | `10` | Per-request timeout for the API |
| `DISCOVERY_MAX_CONCURRENT_PROBES` | no | `256` | Probes in flight at once across a sweep |
| `DISCOVERY_ICMP_PRIVILEGED` | no | `true` | Raw ICMP socket; AppHost sets this `false` |
| `DISCOVERY_SNMP_COMMUNITIES` | no | `public` | Comma-separated, tried in order |

## Development

```bash
python -m venv .venv && . .venv/bin/activate
pip install -e '.[dev]'
pytest
ruff check . && mypy
```

`tests/fixtures/discovered-envelope.json` is the committed contract between this service and the .NET
consumer: `test_bus.py` asserts that `build_envelope` still produces it, and `DiscoveryEnvelopeTests`
reads the same file with MassTransit's own serializer options. Each side testing its own idea of the
envelope is exactly how WP-3.2's dead-lettering bug passed a green suite.
