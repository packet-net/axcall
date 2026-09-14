#!/bin/sh
# deb-install-smoke.sh - prove a built .deb installs cleanly on a pristine base.
#
#   scripts/deb-install-smoke.sh <path-to.deb> [image ...]
#   e.g. scripts/deb-install-smoke.sh artifacts/axcall_0.3.0_amd64.deb
#
# For each base image (default: Debian-stable + Ubuntu-LTS) it runs a THROWAWAY
# container and asserts, on a bare base:
#   1. `apt install ./pkg.deb` resolves the declared Depends and installs.
#   2. dpkg state is 'installed', and the binary, man page, copyright and the
#      example ports file all landed where the package says they do.
#   3. the binary actually RUNS. This is the real payoff: a self-contained
#      publish that is missing a native dependency (libicu is the classic one,
#      which InvariantGlobalization is there to avoid) installs perfectly and
#      then fails on first exec, and only running it catches that.
#   4. it does the AX.25-specific thing it is for: resolves a ports file, and
#      reports a usage error with the documented exit code.
#   5. it takes over /usr/bin/axcall from ax25-apps, which owns that path, via
#      the declared Conflicts + Replaces.
#   6. `apt purge` removes it cleanly.
#
# Container-isolated by design: nothing is installed onto the (self-hosted,
# non-ephemeral) runner.
set -eu

[ $# -ge 1 ] || { echo "usage: $0 <path-to.deb> [image ...]" >&2; exit 2; }
DEB_PATH=$(cd "$(dirname "$1")" && pwd)/$(basename "$1"); shift
[ -f "$DEB_PATH" ] || { echo "no such .deb: $DEB_PATH" >&2; exit 2; }
if [ $# -ge 1 ]; then IMAGES="$*"; else IMAGES="debian:stable-slim ubuntu:24.04"; fi

DEB_DIR=$(dirname "$DEB_PATH")
DEB_BASE=$(basename "$DEB_PATH")

# The assertions, run inside the container. Fully single-quoted: every $VAR here
# is expanded by the container's /bin/sh, not the host. The .deb basename
# arrives via -e env so this stays interpolation-free.
INNER='
set -u
fail() { echo "SMOKE_FAIL: $1"; exit 1; }
cd /work

# Debian and Ubuntu container images ship a dpkg config that discards man pages
# and /usr/share/doc to save space. Remove it before installing, or the payload
# assertions below would be testing the image policy rather than the package.
rm -f /etc/dpkg/dpkg.cfg.d/docker /etc/dpkg/dpkg.cfg.d/excludes

echo "--- 1. apt install ./$DEB_BASE"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq || fail "apt-get update"
apt-get install -y -qq "./$DEB_BASE" || fail "apt install of the .deb"

echo "--- 2. package state and payload"
dpkg -s axcall | grep -q "Status: install ok installed" || fail "dpkg state"
[ -x /usr/bin/axcall ] || fail "no /usr/bin/axcall"
[ -f /usr/share/man/man1/axcall.1.gz ] || fail "no man page"
gzip -t /usr/share/man/man1/axcall.1.gz || fail "man page is not valid gzip"
[ -f /usr/share/doc/axcall/copyright ] || fail "no copyright"
[ -f /usr/share/doc/axcall/examples/ports ] || fail "no example ports file"
[ -e /etc/axcall/ports ] && fail "shipped an /etc conffile (it would prompt on upgrade)"

echo "--- 3. the binary runs on a bare base"
axcall --version || fail "axcall --version did not run (missing native dependency?)"
axcall --version | grep -q "^axcall " || fail "unexpected --version output"

echo "--- 4. it behaves like axcall"
# No ports file: a bare name is a usage error (exit 2), not a crash.
axcall radio gb7rdg >/dev/null 2>&1; rc=$?
[ "$rc" -eq 2 ] || fail "unknown port: expected exit 2, got $rc"
install -d /etc/axcall
printf "radio  M0LTE-7  /dev/ttyUSB0:57600  256  4  smoke\n" > /etc/axcall/ports
# The port now resolves and supplies the callsign, so this gets as far as
# opening the device and fails with exit 3 (no such device), not 2 (usage).
# That is the whole ports-file path exercised against the real installed binary.
axcall radio gb7rdg >/dev/null 2>&1; rc=$?
[ "$rc" -eq 3 ] || fail "named port did not resolve: expected exit 3, got $rc"
rm -rf /etc/axcall

echo "--- 5. takes over /usr/bin/axcall from ax25-apps"
apt-get purge -y -qq axcall >/dev/null || fail "purge before the conflict test"
apt-get install -y -qq ax25-apps >/dev/null 2>&1 || { echo "    (ax25-apps not in this suite, skipping)"; SKIP_CONFLICT=1; }
if [ "${SKIP_CONFLICT:-0}" != "1" ]; then
  [ -x /usr/bin/axcall ] || fail "ax25-apps did not provide /usr/bin/axcall"
  apt-get install -y -qq "./$DEB_BASE" || fail "install over ax25-apps (Conflicts/Replaces)"
  dpkg -s ax25-apps 2>/dev/null | grep -q "Status: install ok installed" \
    && fail "ax25-apps should have been removed by the Conflicts"
  axcall --version | grep -q "^axcall " || fail "wrong axcall won after the takeover"
fi

echo "--- 6. apt purge"
apt-get purge -y -qq axcall >/dev/null || fail "apt purge"
[ -e /usr/bin/axcall ] && fail "binary survived purge"
[ -e /usr/share/man/man1/axcall.1.gz ] && fail "man page survived purge"

echo "SMOKE_OK"
'

status=0
for image in $IMAGES; do
  echo "================================================================"
  echo "== smoke: $image"
  echo "================================================================"
  if docker run --rm -v "$DEB_DIR":/work:ro -e DEB_BASE="$DEB_BASE" \
       "$image" /bin/sh -c "$INNER"; then
    echo "== $image: OK"
  else
    echo "== $image: FAILED" >&2
    status=1
  fi
done

exit $status
