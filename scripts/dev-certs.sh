#!/usr/bin/env bash
#
# dev-certs.sh — generate the local development certificate authority and the one leaf
# certificate the LAN-facing stack serves TLS with.
#
# Why this exists: a browser off this box cannot sign in over plain HTTP. `oidc-client-ts`
# generates its PKCE challenge with `crypto.subtle`, which the platform only exposes in a
# secure context, and the library hardcodes S256 — so there is no "fall back to plain PKCE"
# escape hatch. A phone scanning a printed asset label therefore loads the page and can
# never authenticate. Three origins have to be HTTPS for that to work: the SPA (the secure
# context itself), the API (an HTTPS page cannot fetch an HTTP one), and Keycloak (the token
# exchange is a fetch from that same page).
#
# One CA, one leaf. The CA is installed on the phone once; the leaf carries every name the
# stack is reached by, so it covers all three origins. Both are written to `certs/`, which
# is git-ignored — these are per-machine development credentials and belong to nobody else.
#
# Usage:
#   scripts/dev-certs.sh                          # SANs for the detected LAN address
#   scripts/dev-certs.sh 192.168.1.5              # SANs for an address given explicitly
#   scripts/dev-certs.sh itplatform.local         # ...or a hostname
#   scripts/dev-certs.sh itplatform.local 192.168.1.5   # both, so either reaches it
#
# Give every name the stack will be reached by. A certificate is checked against the string
# in the address bar, not against where it resolves to — so a host reached by name needs a
# DNS SAN for that name even though the IP SAN already covers the same machine.
#
# Re-running reuses an existing CA and reissues only the leaf, so a phone that already
# trusts the CA keeps working when this machine's address changes.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cert_dir="$repo_root/certs"

ca_key="$cert_dir/dev-ca.key"
ca_crt="$cert_dir/dev-ca.crt"
server_key="$cert_dir/dev-server.key"
server_crt="$cert_dir/dev-server.crt"
bundle="$cert_dir/dev-ca-bundle.crt"

# The address the phone dials. Taken from the default route rather than from `hostname -I`,
# which on a mirrored-mode WSL host lists loopback aliases ahead of the real interface.
if [[ $# -gt 0 ]]; then
  hosts=("$@")
else
  detected="$(ip -4 -o route get 1.1.1.1 2>/dev/null | sed -n 's/.* src \([0-9.]*\).*/\1/p')"
  if [[ -z "$detected" ]]; then
    echo "dev-certs.sh: could not detect a LAN address; pass one explicitly." >&2
    exit 1
  fi
  hosts=("$detected")
fi

# The first is what the certificate is named after; every one becomes a SAN. An address has to
# be declared IP: and a name DNS: — a name written as IP: produces a certificate no client will
# accept, and the failure reads as an untrusted CA rather than as a malformed SAN.
sans="DNS:localhost,IP:127.0.0.1,IP:::1"
for host in "${hosts[@]}"; do
  if [[ "$host" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ || "$host" == *:* ]]; then
    sans="$sans,IP:$host"
  else
    sans="$sans,DNS:$host"
  fi
done
lan_host="${hosts[0]}"

mkdir -p "$cert_dir"

# The CA is generated once and kept. Reissuing it would invalidate the copy already trusted
# on the phone, which is the one step in this whole arrangement that a human has to perform
# by hand.
if [[ -f "$ca_key" && -f "$ca_crt" ]]; then
  echo "Reusing existing CA at $ca_crt"
else
  echo "Generating development CA"
  openssl req -x509 -newkey rsa:4096 -sha256 -days 3650 -nodes \
    -keyout "$ca_key" -out "$ca_crt" \
    -subj "/CN=it-platform development CA/O=it-platform" \
    -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
    -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null
  chmod 600 "$ca_key"
fi

# 365 days, not the CA's ten years: iOS refuses to trust a server certificate with a long
# lifetime even when its root is user-installed, and a year outlives any dev machine's IP.
echo "Issuing leaf certificate for ${hosts[*]}"
openssl req -newkey rsa:2048 -sha256 -nodes \
  -keyout "$server_key" -out "$cert_dir/dev-server.csr" \
  -subj "/CN=$lan_host/O=it-platform" 2>/dev/null

cat > "$cert_dir/dev-server.ext" <<EXT
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=$sans
EXT

openssl x509 -req -in "$cert_dir/dev-server.csr" -sha256 -days 365 \
  -CA "$ca_crt" -CAkey "$ca_key" -CAcreateserial \
  -extfile "$cert_dir/dev-server.ext" -out "$server_crt" 2>/dev/null

rm -f "$cert_dir/dev-server.csr" "$cert_dir/dev-server.ext"

# World-readable on purpose: the Keycloak container reads this key as its own unprivileged
# user, whose uid need not match this one. It is a development key for a name that resolves
# only on this LAN, and the CA key beside it stays 600.
chmod 644 "$server_crt" "$server_key"

# What the API trusts when it calls Keycloak. .NET on Linux validates through OpenSSL, which
# honours SSL_CERT_FILE — so the API is pointed at the system bundle with this CA appended,
# rather than the CA being installed machine-wide with sudo.
cat /etc/ssl/certs/ca-certificates.crt "$ca_crt" > "$bundle"

echo
echo "Certificates written to $cert_dir"
echo "  CA (install this on the phone): $ca_crt"
echo "  Leaf, valid for: localhost, 127.0.0.1, ::1, ${hosts[*]}"
echo
echo "Run the stack with:"
echo "  env 'Parameters__public-host=$lan_host' aspire run"
