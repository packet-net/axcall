#!/usr/bin/env bash
#
# peer-interop.sh - run the interop tests against implementations other than
# LinBPQ, on demand.
#
#   scripts/peer-interop.sh
#
# These are not in CI, and the reason is not cost. The kernel leg of this
# campaign cannot run in a container at all: AF_AX25 is refused inside a
# non-init user namespace, which every runner and every container on this
# project's infrastructure is, whatever capabilities it is granted. So a peer
# suite that CI could run in full does not exist, and given that, running the
# containerised half nightly buys little. The peers change about once a year;
# our code changes daily.
#
# Run this when the IP code changes or when a peer is updated, and put what it
# says into docs/ip-over-ax25.md, which is the record. A result in a document
# with a date on it is visibly stale when it is stale; a green tick nobody
# looks at is not.
#
# What runs here:
#   XRouter   ghcr.io/packethacking/xrouter, containerised, and the only peer
#             available that can send virtual-circuit IP correctly.
#
# What does not, and needs the ax25-lab VM:
#   Linux kernel AX.25   kissattach onto a socat pty. See docs/ip-over-ax25.md.
#
# Not yet written:
#   JNOS      the peer that would settle the Van Jacobson compression question.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! docker info >/dev/null 2>&1; then
  echo "docker is not available, and every peer here runs in a container" >&2
  exit 2
fi

echo "==> building"
dotnet build "$root/tests/Axcall.Tests/Axcall.Tests.csproj" --nologo

echo "==> peer interop (first run also builds the XRouter image, which needs the network)"
dotnet test "$root/tests/Axcall.Tests/Axcall.Tests.csproj" \
  --nologo --no-build --filter "Category=Peers" \
  --logger "console;verbosity=detailed"
