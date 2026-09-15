#!/bin/sh
# deb-install-smoke.sh - prove the built .debs install cleanly on a pristine base.
#
#   scripts/deb-install-smoke.sh <path-to.deb> [path-to.deb ...]
#   e.g. scripts/deb-install-smoke.sh artifacts/axcall_0.7.0_amd64.deb \
#                                     artifacts/axsocks_0.7.0_amd64.deb
#
# Base images come from $SMOKE_IMAGES, defaulting to Debian-stable and
# Ubuntu-LTS. For each, it runs a THROWAWAY container and asserts, on a bare
# base:
#   1. `apt install ./pkg.deb ...` resolves the declared Depends and installs.
#   2. each package's dpkg state is 'installed', and its binary, man page,
#      copyright and examples all landed where the package says they do.
#   3. each binary actually RUNS. This is the real payoff: a self-contained
#      publish that is missing a native dependency (libicu is the classic one,
#      which InvariantGlobalization is there to avoid) installs perfectly and
#      then fails on first exec, and only running it catches that.
#   4. each does the AX.25-specific thing it is for: resolves a ports file, and
#      reports the documented exit code - and gets far enough into opening a
#      serial port to prove the native serial library is present, which an exit
#      code alone does not show.
#   5. axcall takes over /usr/bin/axcall from ax25-apps, which owns that path,
#      via the declared Conflicts + Replaces.
#   6. `apt purge` removes them cleanly.
#
# The packages are installed together because that is the interesting case: one
# per program, each carrying its own copy of the native library, so a path
# collision between them would show up here.
#
# Container-isolated by design: nothing is installed onto the (self-hosted,
# non-ephemeral) runner.
set -eu

[ $# -ge 1 ] || { echo "usage: $0 <path-to.deb> [path-to.deb ...]" >&2; exit 2; }

DEB_DIR=""
DEB_BASES=""
PACKAGES=""
for deb in "$@"; do
  [ -f "$deb" ] || { echo "no such .deb: $deb" >&2; exit 2; }
  dir=$(cd "$(dirname "$deb")" && pwd)
  if [ -z "$DEB_DIR" ]; then
    DEB_DIR="$dir"
  elif [ "$dir" != "$DEB_DIR" ]; then
    echo "all .debs must be in one directory (it is the only one mounted)" >&2
    exit 2
  fi
  DEB_BASES="${DEB_BASES:+$DEB_BASES }$(basename "$deb")"
  PACKAGES="${PACKAGES:+$PACKAGES }$(dpkg-deb -f "$deb" Package)"
done

IMAGES="${SMOKE_IMAGES:-debian:stable-slim ubuntu:24.04}"

# Pin the container platform to this machine's. Without it, a base image left
# in the local cache for another architecture is used silently, and the run
# fails with "exec /bin/sh: exec format error", which says nothing about why.
# That is easy to do by accident: one `docker run --platform linux/arm64
# debian:stable-slim` replaces the tag for everything afterwards.
case "$(uname -m)" in
  x86_64)  PLATFORM=linux/amd64 ;;
  aarch64) PLATFORM=linux/arm64 ;;
  armv7l)  PLATFORM=linux/arm/v7 ;;
  *) echo "unknown host architecture: $(uname -m)" >&2; exit 2 ;;
esac

# The assertions, run inside the container. Fully single-quoted: every $VAR here
# is expanded by the container's /bin/sh, not the host. The basenames and
# package names arrive via -e env so this stays interpolation-free.
INNER='
set -u
fail() { echo "SMOKE_FAIL: $1"; exit 1; }
has() { case " $PACKAGES " in *" $1 "*) return 0 ;; *) return 1 ;; esac; }
cd /work

# Debian and Ubuntu container images ship a dpkg config that discards man pages
# and /usr/share/doc to save space. Remove it before installing, or the payload
# assertions below would be testing the image policy rather than the package.
rm -f /etc/dpkg/dpkg.cfg.d/docker /etc/dpkg/dpkg.cfg.d/excludes

local_debs=""
for base in $DEB_BASES; do local_debs="$local_debs ./$base"; done

echo "--- 1. apt install$local_debs"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq || fail "apt-get update"
# shellcheck disable=SC2086
apt-get install -y -qq $local_debs || fail "apt install of the .debs"

echo "--- 2. package state and payload"
for pkg in $PACKAGES; do
  dpkg -s "$pkg" | grep -q "Status: install ok installed" || fail "$pkg: dpkg state"
  [ -x "/usr/bin/$pkg" ] || fail "no /usr/bin/$pkg"
  [ -L "/usr/bin/$pkg" ] || fail "/usr/bin/$pkg is not a symlink into /usr/lib/$pkg"
  [ -x "/usr/lib/$pkg/$pkg" ] || fail "no /usr/lib/$pkg/$pkg"
  [ -f "/usr/share/man/man1/$pkg.1.gz" ] || fail "no man page for $pkg"
  gzip -t "/usr/share/man/man1/$pkg.1.gz" || fail "$pkg man page is not valid gzip"
  [ -f "/usr/share/doc/$pkg/copyright" ] || fail "no copyright for $pkg"
  [ -f "/usr/share/doc/$pkg/changelog.Debian.gz" ] || fail "no changelog for $pkg"
  [ -f "/usr/share/doc/$pkg/examples/ports" ] || fail "no example ports file for $pkg"
done
[ -e /etc/axcall/ports ] && fail "shipped an /etc conffile (it would prompt on upgrade)"
has axsocks && { [ -f /usr/share/doc/axsocks/examples/hosts ] || fail "no example hosts file"; }

echo "--- 3. the binaries run on a bare base"
for pkg in $PACKAGES; do
  "$pkg" --version || fail "$pkg --version did not run (missing native dependency?)"
  "$pkg" --version | grep -q "^$pkg " || fail "unexpected $pkg --version output"
done

echo "--- 4. they behave like AX.25 tools"
if has axcall; then
  # No ports file: a bare name is a usage error (exit 2), not a crash.
  axcall radio gb7rdg >/dev/null 2>&1; rc=$?
  [ "$rc" -eq 2 ] || fail "unknown port: expected exit 2, got $rc"
fi

install -d /etc/axcall
printf "radio  M0LTE-7  /dev/ttyUSB0:57600  256  4  smoke\n" > /etc/axcall/ports

if has axcall; then
  # The port now resolves and supplies the callsign, so this gets as far as
  # opening the device and fails with exit 3 (no such device), not 2 (usage).
  # That is the whole ports-file path exercised against the real installed
  # binary.
  axcall radio gb7rdg >/dev/null 2>&1; rc=$?
  [ "$rc" -eq 3 ] || fail "named port did not resolve: expected exit 3, got $rc"
fi

# And each has to fail for the RIGHT reason. A NativeAOT build dlopens
# libSystem.IO.Ports.Native from the directory holding the binary, so a package
# that does not ship it next to the binary cannot open any serial port at all.
# It still exits 3, which is why the assertion above passed all through v0.5.0
# and v0.6.0 while every serial port was broken. Read the message, not the code.
for pkg in $PACKAGES; do
  # axcall dials a destination; axsocks takes the port alone and waits for a
  # SOCKS client to name one. Both reach the modem, which is the point here.
  case "$pkg" in
    axcall) args="radio gb7rdg" ;;
    *)      args="radio" ;;
  esac
  set +e
  # shellcheck disable=SC2086
  out=$("$pkg" $args 2>&1); rc=$?
  set -e
  [ "$rc" -eq 3 ] || { echo "$out"; fail "$pkg: expected exit 3 opening the port, got $rc"; }
  case "$out" in
    *"Unable to load shared library"*)
      echo "$out"; fail "$pkg is missing its native serial library" ;;
  esac
  case "$out" in
    *"/dev/ttyUSB0"*) ;;
    *) echo "$out"; fail "$pkg: expected a complaint about the missing device" ;;
  esac
done

rm -rf /etc/axcall

if has axcall; then
  echo "--- 5. axcall takes over /usr/bin/axcall from ax25-apps"
  apt-get purge -y -qq axcall >/dev/null || fail "purge before the conflict test"
  apt-get install -y -qq ax25-apps >/dev/null 2>&1 || { echo "    (ax25-apps not in this suite, skipping)"; SKIP_CONFLICT=1; }
  if [ "${SKIP_CONFLICT:-0}" != "1" ]; then
    [ -x /usr/bin/axcall ] || fail "ax25-apps did not provide /usr/bin/axcall"
    apt-get install -y -qq ./axcall_*.deb || fail "install over ax25-apps (Conflicts/Replaces)"
    dpkg -s ax25-apps 2>/dev/null | grep -q "Status: install ok installed" \
      && fail "ax25-apps should have been removed by the Conflicts"
    axcall --version | grep -q "^axcall " || fail "wrong axcall won after the takeover"
  fi
fi

echo "--- 6. apt purge"
# shellcheck disable=SC2086
apt-get purge -y -qq $PACKAGES >/dev/null || fail "apt purge"
for pkg in $PACKAGES; do
  [ -e "/usr/bin/$pkg" ] && fail "$pkg binary survived purge"
  [ -e "/usr/lib/$pkg" ] && fail "$pkg private directory survived purge"
  [ -e "/usr/share/man/man1/$pkg.1.gz" ] && fail "$pkg man page survived purge"
done

echo "SMOKE_OK"
'

status=0
for image in $IMAGES; do
  echo "================================================================"
  echo "== smoke: $image"
  echo "================================================================"
  if docker run --rm --platform "$PLATFORM" -v "$DEB_DIR":/work:ro \
       -e DEB_BASES="$DEB_BASES" -e PACKAGES="$PACKAGES" \
       "$image" /bin/sh -c "$INNER"; then
    echo "== $image: OK"
  else
    echo "== $image: FAILED" >&2
    status=1
  fi
done

exit $status
