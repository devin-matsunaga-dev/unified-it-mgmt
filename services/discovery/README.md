# discovery

The IT Platform discovery agent (WP-4.1).

Every cycle it fetches the scan profiles for its group from
`/api/discovery/{group}/scan-profiles`, runs whichever of them are due, collects any scan a person has
asked for from `/api/discovery/{group}/scan-runs`, and publishes one `DeviceDiscovered` per device it
found. Matching a discovery to a CI and queueing the rest for human review is WP-4.2's.

It has two credentials and both are deliberately narrow:

- a **Keycloak service account** (`it-platform-discovery`, client credentials) carrying the
  `Discovery` realm role, which reaches those endpoints and nothing else on the API. It is
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
4. **Resolve** — reverse DNS for the addresses that were found, then mDNS and NetBIOS for whichever
   of those reverse DNS could not name. See [Naming an address](#naming-an-address).
5. **Identify** — SNMP v2c `GET` of the system group (`sysDescr`, `sysObjectID`, `sysName`,
   `sysLocation`, `sysContact`, `sysUpTime`), trying each configured community in order and stopping
   at the first that answers.
6. **Neighbours** — LLDP (`lldpRemTable`) and CDP (`cdpCacheTable`) walks, using the community that
   already worked. Both protocols are reported additively: a switch with both switched on reports the
   same link twice, and deciding they are one edge is WP-4.3's topology work.

Nothing here writes to the CMDB, and nothing decides what a discovery *means*.

## When a scan runs

Three switches decide, and they are deliberately not one:

| Switch | Where it lives | Off means |
|---|---|---|
| `isEnabled` | the profile | It leaves every scanner's configuration. It cannot be scanned at all. |
| `scheduleEnabled` | the profile | It is still sent and still runnable on demand, but no cycle starts it. |
| `scheduledScanningEnabled` | `monitoring.discovery_settings`, one row | No profile in any group runs on a timer. On-demand runs are unaffected. |

All three arrive on the config document and **absent means on** in every case: a response from a
platform older than these fields must not read as an estate asking to stop scanning.

### On-demand scans

An operator presses "Scan now" and the platform writes a `monitoring.scan_runs` row. This service
**collects** it on its next cycle — it is never pushed one. ARCHITECTURE §4 gives this process
publish-only bus credentials and says agents never consume commands, so the same rule that shapes the
poller's runbook channel (WP-5.6) shapes this one: the platform decides, the agent fetches, the agent
reports back.

Consequences worth knowing before promising anything:

- **A run is queued, not started.** It begins within one `DISCOVERY_INTERVAL_SECONDS`, which is thirty
  seconds in the dev stack, and requested runs are collected *after* the scheduled ones so a queue of
  requests cannot starve the estate's own sweeps.
- **A claimed run is one scanner's.** Claiming is a conditional update on the server, so two scanners
  in a group never sweep the same request.
- **Nothing is retried.** A run this service could not report on is timed out by the platform's own
  sweeper — which is the only thing that can notice a scanner has died, because this service still has
  no heartbeat.
- **A run carries its whole profile**, not an id, so a profile whose schedule is off — or which is not
  in this scanner's scheduled set at all — can still be run on request.

### `local`

`local` resolves to the subnet the scanner is attached to, read from `/proc/net/route`, **narrowed to
at most a /24 around the scanner's own address**. Docker hands a user-defined network a /16, so the
interface genuinely reports one — and a /16 is 65,534 probes to find the handful of containers in the
first /24 of it. The narrowing is logged with both numbers. For a wider sweep, write the CIDR out.

### Naming an address

A home or small-office LAN usually has no PTR records at all, so reverse DNS answers nothing for every
real device on it and a review queue fills with bare IPv4 addresses. Two protocols still answer on such
a network and are asked, concurrently, for the addresses DNS could not name:

| Protocol | Port | Names |
|---|---|---|
| mDNS | UDP 5353 | Apple devices, printers, Chromecasts, smart TVs, modern Linux, Windows 10+ |
| NetBIOS name service | UDP 137 | Windows machines, Samba shares, most NAS boxes |

Both are spoken directly rather than through a library — neither needs more than a packet out and a
packet parsed. mDNS wins a tie, because it is the name a device chose to advertise while a NetBIOS name
is frequently a truncated, upper-cased relic.

Every discovery carries **which protocol named it** (`hostnameSource`: `dns`, `mdns` or `netbios`), and
the review card shows it. The three are not equally trustworthy: a PTR record is what the network's own
administrator published, while an mDNS or NetBIOS name is whatever the device says about itself, and a
device is free to say anything. A name is length-checked and character-checked before it is published —
it travels the bus, lands in a review queue and can become a CI name.

Consequences worth knowing:

- **A well-run network reaches none of this.** With proper reverse DNS every address is already named
  and the step does nothing.
- **A network that does not carry multicast gets NetBIOS only**, and that is not an error — the mDNS
  query simply times out.
- **Only addresses that answered are asked**, and at the identify step's concurrency rather than the
  sweep's: each unnamed address is two datagrams and a timeout.

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
