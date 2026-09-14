#!/usr/bin/env bash
#
# build-deb.sh - publish axcall self-contained for one RID and package it as a
# Debian .deb. Used by release.yml, and runnable by hand for a local check.
#
#   scripts/build-deb.sh <rid> <version>
#   e.g. scripts/build-deb.sh linux-arm64 0.3.0
#
# Cross-publishes from x64, so all three arches build on the one runner. The
# binary is the same self-contained single file the GitHub Release carries, so
# the .deb and the loose download are not two different programs.
#
# Produces artifacts/axcall_<version>_<arch>.deb.
set -euo pipefail

rid="${1:?usage: build-deb.sh <rid> <version>}"
version="${2:?usage: build-deb.sh <rid> <version>}"

case "$rid" in
  linux-x64)   arch=amd64 ;;
  linux-arm64) arch=arm64 ;;
  linux-arm)   arch=armhf ;;
  *) echo "unknown rid: $rid (want linux-x64 | linux-arm64 | linux-arm)" >&2; exit 2 ;;
esac

# The Debian version. A prerelease tag like 0.3.0-rc1 has to become 0.3.0~rc1:
# dpkg sorts `~` before everything including the empty string, so the rc
# correctly precedes 0.3.0. Left as a hyphen it would be read as Debian
# revision `rc1` of upstream 0.3.0, and would sort AFTER the real release.
#
# Note that GitHub rewrites `~` to `.` in RELEASE ASSET names on upload, so a
# prerelease downloads as axcall_0.3.0.rc1_amd64.deb. That is cosmetic: dpkg
# reads the version out of the control file, not the filename, and the control
# file still says 0.3.0~rc1. Verified on v0.3.0-rc1.
deb_version="${version/-/\~}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
proj="$root/src/Axcall/Axcall.csproj"
pub="$root/artifacts/publish/$rid"
stage="$root/artifacts/deb/$rid"
out="$root/artifacts/axcall_${deb_version}_${arch}.deb"

echo "==> publish $rid (self-contained, single-file, invariant globalization)"
rm -rf "$pub"
dotnet publish "$proj" -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -p:DebugSymbols=false \
  -p:Version="$version" -p:InformationalVersion="$version" \
  -v minimal -o "$pub"

[ -f "$pub/axcall" ] || { echo "publish produced no axcall binary" >&2; exit 1; }

echo "==> stage .deb tree for $arch"
rm -rf "$stage"
# No /etc/axcall/ports is shipped. It would be a dpkg conffile and would prompt
# on every upgrade; the example goes to /usr/share/doc instead and the user
# copies it. axcall treats a missing ports file as "no ports configured", not
# as an error, so the package works out of the box with a device path.
install -d "$stage/usr/bin" \
           "$stage/usr/share/man/man1" \
           "$stage/usr/share/doc/axcall/examples" \
           "$stage/DEBIAN"

install -m 0755 "$pub/axcall" "$stage/usr/bin/axcall"
install -m 0644 "$root/packaging/ports.example" "$stage/usr/share/doc/axcall/examples/ports"

# Man pages must be gzipped with no timestamp (-n), or the .deb is not
# reproducible and lintian complains.
gzip -9nc "$root/man/axcall.1" > "$stage/usr/share/man/man1/axcall.1.gz"
chmod 0644 "$stage/usr/share/man/man1/axcall.1.gz"

# Debian wants a machine-readable copyright and a changelog. The changelog is
# generated rather than tracked: the GitHub Release notes are the real record,
# and a hand-maintained duplicate would only drift.
cat > "$stage/usr/share/doc/axcall/copyright" <<'COPYRIGHT'
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: axcall
Source: https://github.com/packet-net/axcall

Files: *
Copyright: Tom Fanning M0LTE
License: AGPL-3.0-or-later
 This program is free software: you can redistribute it and/or modify it
 under the terms of the GNU Affero General Public License as published by
 the Free Software Foundation, either version 3 of the License, or (at your
 option) any later version.
 .
 This program is distributed in the hope that it will be useful, but WITHOUT
 ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
 FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
 for more details.
 .
 On Debian systems the full text of the GNU Affero General Public License
 version 3 can be found in /usr/share/common-licenses/AGPL-3.
COPYRIGHT
chmod 0644 "$stage/usr/share/doc/axcall/copyright"

printf 'axcall (%s) unstable; urgency=medium\n\n  * Release %s. See https://github.com/packet-net/axcall/releases/tag/v%s\n\n -- Tom Fanning <tom@m0lte.uk>  %s\n' \
  "$deb_version" "$version" "$version" "$(date -R)" \
  | gzip -9nc > "$stage/usr/share/doc/axcall/changelog.Debian.gz"
chmod 0644 "$stage/usr/share/doc/axcall/changelog.Debian.gz"

# Installed-Size is in KiB, and dpkg-deb does not compute it for us.
installed_size="$(du -s -k --apparent-size "$stage" | cut -f1)"

sed -e "s/@VERSION@/$deb_version/" \
    -e "s/@ARCH@/$arch/" \
    -e "s/@INSTALLED_SIZE@/$installed_size/" \
    "$root/packaging/control.in" > "$stage/DEBIAN/control"

# md5sums is optional but expected; dpkg --verify and debsums use it.
( cd "$stage" && find usr -type f -print0 | sort -z | xargs -0 md5sum > DEBIAN/md5sums )
chmod 0644 "$stage/DEBIAN/md5sums"

echo "==> build .deb"
mkdir -p "$root/artifacts"
# --root-owner-group (dpkg >= 1.19): root:root files without fakeroot.
dpkg-deb --build --root-owner-group "$stage" "$out"

echo "==> built $out"
dpkg-deb --info "$out"
# Pure diagnostics from here. pipefail off: a `… | grep` that does not match, or
# a `head` closing early, would otherwise abort the build on a debug echo.
set +o pipefail
echo "--- contents ---"
dpkg-deb --contents "$out" | awk '{print $1, $3, $6}'
echo "--- conffiles (must be EMPTY: no upgrade prompt) ---"
dpkg-deb --info "$out" | grep -i 'conffiles' || echo "    (no Conffiles - correct)"
set -o pipefail

if command -v lintian >/dev/null 2>&1; then
  # Informational only. A self-contained .NET single-file binary trips several
  # tags by construction (it is a statically-bundled runtime with no shared-lib
  # dependency information dpkg can read), and none of them are actionable.
  lintian "$out" || true
else
  echo "(lintian not installed - skipping deb-lint)"
fi
