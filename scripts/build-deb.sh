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

# NativeAOT does not cross-link, so an arm64 package has to be built on arm64.
# The release workflow does that on a native runner; locally this is a clearer
# failure than several screens of clang output.
if [ "$rid" = "linux-arm64" ] && [ "$(uname -m)" != "aarch64" ]; then
  echo "linux-arm64 is a NativeAOT build and must be built on an arm64 machine." >&2
  echo "The release workflow uses an arm64 runner for it." >&2
  exit 2
fi

# How to publish is the csproj's business, not this script's: NativeAOT
# everywhere with a native runner, trimmed single-file plus ReadyToRun for
# armhf. Passing PublishSingleFile here would fight the AOT builds, which
# produce a native binary with nothing to bundle.
#
# This script does not cross-compile NativeAOT. linux-x64 and linux-arm64 are
# each built on a runner of their own architecture; only armhf is cross-built,
# and it is the one that does not use AOT.
echo "==> publish $rid (strategy from the csproj, invariant globalization)"
rm -rf "$pub"
dotnet publish "$proj" -c Release -r "$rid" --self-contained true \
  -p:DebugType=none -p:DebugSymbols=false \
  -p:Version="$version" -p:InformationalVersion="$version" \
  -v minimal -o "$pub"

[ -f "$pub/axcall" ] || { echo "publish produced no axcall binary" >&2; exit 1; }

# Guard the architecture: a .deb carrying a binary for the wrong machine would
# install cleanly and then fail to exec, which is a miserable way to find out.
want_arch="$(case "$arch" in amd64) echo x86-64 ;; arm64) echo aarch64 ;; armhf) echo ARM ;; esac)"
file "$pub/axcall" | grep -q "$want_arch" || {
  echo "published binary is not $want_arch: $(file -b "$pub/axcall")" >&2; exit 1; }

echo "==> stage .deb tree for $arch"
rm -rf "$stage"
# No /etc/axcall/ports is shipped. It would be a dpkg conffile and would prompt
# on every upgrade; the example goes to /usr/share/doc instead and the user
# copies it. axcall treats a missing ports file as "no ports configured", not
# as an error, so the package works out of the box with a device path.
install -d "$stage/usr/bin" \
           "$stage/usr/lib/axcall" \
           "$stage/usr/share/man/man1" \
           "$stage/usr/share/doc/axcall/examples" \
           "$stage/DEBIAN"

# The binary lives in /usr/lib/axcall rather than /usr/bin, with /usr/bin/axcall
# a symlink to it, because a NativeAOT build is not always one file.
# System.IO.Ports is out-of-band from the shared framework and ships its native
# helper only as a .so, so AOT cannot static-link it; it is dlopened at runtime
# from the directory holding the executable. Alone in /usr/bin the lookup fails
# and every serial port is unopenable, which is how v0.5.0 and v0.6.0 shipped.
#
# The runtime resolves that directory through /proc/self/exe, so it follows the
# symlink to the real location and finds the library next to it. A private
# directory under /usr/lib is also what Debian policy asks for; the alternative,
# dropping a Microsoft-named .so straight into the multiarch directory, is a
# collision waiting for the second AOT .NET package on the system.
install -m 0755 "$pub/axcall" "$stage/usr/lib/axcall/axcall"
ln -s ../lib/axcall/axcall "$stage/usr/bin/axcall"

# Whatever native libraries the publish could not link in. AOT builds have at
# least libSystem.IO.Ports.Native.so; the armhf single-file build has none,
# because IncludeNativeLibrariesForSelfExtract bundles them inside the binary.
shopt -s nullglob
native_libs=("$pub"/*.so)
shopt -u nullglob
for lib in "${native_libs[@]}"; do
  echo "==> bundling native library $(basename "$lib")"
  install -m 0644 "$lib" "$stage/usr/lib/axcall/$(basename "$lib")"
done

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

# Dependencies, read off the binary and any bundled native libraries rather
# than assumed, because the two publish
# strategies differ: a NativeAOT build links libgcc, libstdc++ and zlib
# statically and needs only libc, while the trimmed single-file build still
# wants all of them. Declaring the union on an AOT package would pull in
# libraries it never opens.
declare -A soname_to_pkg=(
  # The dynamic loader ships inside libc6, whichever architecture names it.
  [ld-linux-x86-64.so.2]=libc6
  [ld-linux-aarch64.so.1]=libc6
  [ld-linux-armhf.so.3]=libc6
  [libc.so.6]=libc6
  [libm.so.6]=libc6
  [libgcc_s.so.1]=libgcc-s1
  [libstdc++.so.6]="libstdc++6"
  [libz.so.1]=zlib1g
)
depends=""
while read -r soname; do
  pkg="${soname_to_pkg[$soname]:-}"
  [ -n "$pkg" ] || { echo "unmapped shared library dependency: $soname" >&2; exit 1; }
  case ",$depends," in *",$pkg,"*) ;; *) depends="${depends:+$depends,}$pkg" ;; esac
done < <(objdump -p "$pub/axcall" "${native_libs[@]}" | awk '/NEEDED/ {print $2}' | sort -u)
depends="${depends//,/, }"
echo "==> depends: $depends"

# Installed-Size is in KiB, and dpkg-deb does not compute it for us.
installed_size="$(du -s -k --apparent-size "$stage" | cut -f1)"

sed -e "s/@VERSION@/$deb_version/" \
    -e "s/@ARCH@/$arch/" \
    -e "s/@INSTALLED_SIZE@/$installed_size/" \
    -e "s/@DEPENDS@/$depends/" \
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
