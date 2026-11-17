#!/usr/bin/env bash
set -euo pipefail

root="$1"
collector="$2"
mkdir -p "$root/tap-watch"
sed "s|__ROOT__|$root|g" .github/interop/dns-collector.yml > "$root/collector.yml"
"$collector" -config "$root/collector.yml" -test-config > "$root/config-check.log" 2>&1
"$collector" -config "$root/collector.yml" > "$root/collector.log" 2>&1 &
pid=$!
trap 'kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true' EXIT

ready=0
for i in {1..100}; do
  if ss -ltn | grep -qE ':16055[[:space:]]'; then ready=1; break; fi
  kill -0 "$pid" || { cat "$root/collector.log"; exit 1; }
  sleep 0.1
done
[[ "$ready" -eq 1 ]] || { cat "$root/collector.log"; exit 1; }

# Exercise two independent Serilog TCP sessions, including STOP/FINISH at shutdown.
for i in 1 2; do
  dotnet run --project tests/PackageConsumer/PackageConsumer.csproj -c Release --no-restore -- tcp 127.0.0.1 16055
done
# Give the collector an atomic, completed Frame Streams file instead of a growing file.
dotnet run --project tests/PackageConsumer/PackageConsumer.csproj -c Release --no-restore -- file "$root/tap-watch/capture.tmp"
mv "$root/tap-watch/capture.tmp" "$root/tap-watch/capture.fstrm"

check_records() {
  local output="$1"
  local count="$2"
  [[ -s "$output" ]] && jq -s -e --argjson count "$count" '
    length == $count and
    all(.[]; .dns.id == 1 and
      .dnstap.operation == "CLIENT_QUERY" and
      .dnstap.identity == "serilog-dnstap-consumer" and
      .network."query-ip" == "192.0.2.10" and
      .network."response-port" == "53" and
      ((.dns.qname | rtrimstr(".")) == "example.com"))
  ' "$output" > /dev/null
}

ok=0
for i in {1..120}; do
  if check_records "$root/tcp.jsonl" 2 && check_records "$root/file.jsonl" 1; then
    ok=1
    break
  fi
  kill -0 "$pid" || { cat "$root/collector.log"; exit 1; }
  sleep 0.5
done
if [[ "$ok" -ne 1 ]]; then
  echo "DNS-collector did not decode the expected Serilog TCP and file records" >&2
  cat "$root/tcp.jsonl" "$root/file.jsonl" 2>/dev/null || true
  tail -100 "$root/collector.log"
  exit 1
fi

# Detect duplicates or late protocol/queue errors after the collector's flush tick.
sleep 3
if ! check_records "$root/tcp.jsonl" 2 || ! check_records "$root/file.jsonl" 1; then
  echo "DNS-collector output changed unexpectedly after flush" >&2
  exit 1
fi
if grep -Eiq 'framestream reader error|unable to decode|worker.*busy|buffer is full|unexpected control framestream' "$root/collector.log"; then
  echo "DNS-collector reported a protocol or queue error" >&2
  tail -100 "$root/collector.log"
  exit 1
fi
echo "DNS-collector decoded 2 Serilog TCP records and 1 Serilog file record"
