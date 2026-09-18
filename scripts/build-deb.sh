#!/usr/bin/env bash
#
# build-deb.sh - publish the axcall tools self-contained for one RID and
# package each as its own Debian .deb. Used by release.yml, and runnable by
# hand for a local check.
#
#   scripts/build-deb.sh <rid> <version>
#   e.g. scripts/build-deb.sh linux-arm64 0.7.0
#
# One package per program, not one package holding the set. "apt install
# axcall" should give you axcall and nothing else: the package is named after a
# program, so shipping others alongside it would break the promise the name
# makes. It also lets a headless node take the proxy without the terminal.
#
# They are built together because they share a source tree and a version, not
# because they belong in one package.
#
# Produces artifacts/<package>_<version>_<arch>.deb for each.
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

# One package per program, each named after its binary.
packages=(axcall axsocks axinetd axtun)

# Example config a package ships in /usr/share/doc/<pkg>/examples. They all
# read the ports file, so they all carry that example; the rest is each one's
# own.
declare -A examples=(
  [axcall]="ports"
  [axsocks]="ports hosts"
  [axinetd]="ports inetd"
  [axtun]="ports axtun"
)

pub="$root/artifacts/publish/$rid"

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
for pkg in "${packages[@]}"; do
  proj="$root/src/${pkg^}/${pkg^}.csproj"
  [ -f "$proj" ] || { echo "no project for $pkg at $proj" >&2; exit 1; }
  dotnet publish "$proj" -c Release -r "$rid" --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false \
    -p:Version="$version" -p:InformationalVersion="$version" \
    -v minimal -o "$pub"
done

# Guard the architecture: a .deb carrying a binary for the wrong machine would
# install cleanly and then fail to exec, which is a miserable way to find out.
want_arch="$(case "$arch" in amd64) echo x86-64 ;; arm64) echo aarch64 ;; armhf) echo ARM ;; esac)"
for pkg in "${packages[@]}"; do
  [ -f "$pub/$pkg" ] || { echo "publish produced no $pkg binary" >&2; exit 1; }
  file "$pub/$pkg" | grep -q "$want_arch" || {
    echo "published $pkg is not $want_arch: $(file -b "$pub/$pkg")" >&2; exit 1; }
done

# Whatever native libraries the publish could not link in. AOT builds have at
# least libSystem.IO.Ports.Native.so; the armhf single-file build has none,
# because IncludeNativeLibrariesForSelfExtract bundles them inside the binary.
#
# Every package gets its own copy. It is 15 kB, and a shared package to hold
# one small file would buy a dependency edge and an upgrade-ordering problem in
# exchange for nothing.
shopt -s nullglob
native_libs=("$pub"/*.so)
shopt -u nullglob

# Shared library to Debian package, for the Depends line. Read off the binaries
# rather than assumed, because the two publish strategies differ: a NativeAOT
# build links libgcc, libstdc++ and zlib statically and needs only libc, while
# the trimmed single-file build still wants all of them. Declaring the union on
# an AOT package would pull in libraries it never opens.
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

# Stage and build one package.
package_one() {
  local pkg="$1"
  local stage="$root/artifacts/deb/$rid/$pkg"
  local out="$root/artifacts/${pkg}_${deb_version}_${arch}.deb"

  echo "==> stage $pkg for $arch"
  rm -rf "$stage"
  # No /etc/axcall/ports is shipped. It would be a dpkg conffile and would
  # prompt on every upgrade; the example goes to /usr/share/doc instead and the
  # user copies it. A missing ports file means "no ports configured", not an
  # error, so the package works out of the box with a device path.
  install -d "$stage/usr/bin" \
             "$stage/usr/lib/$pkg" \
             "$stage/usr/share/man/man1" \
             "$stage/usr/share/doc/$pkg/examples" \
             "$stage/DEBIAN"

  # The binary lives in /usr/lib/<pkg> rather than /usr/bin, with /usr/bin/<pkg>
  # a symlink to it, because a NativeAOT build is not always one file.
  # System.IO.Ports is out-of-band from the shared framework and ships its
  # native helper only as a .so, so AOT cannot static-link it; it is dlopened at
  # runtime from the directory holding the executable. Alone in /usr/bin the
  # lookup fails and every serial port is unopenable, which is how v0.5.0 and
  # v0.6.0 shipped.
  #
  # The runtime resolves that directory through /proc/self/exe, so it follows
  # the symlink to the real location and finds the library next to it. A private
  # directory under /usr/lib is also what Debian policy asks for; the
  # alternative, dropping a Microsoft-named .so straight into the multiarch
  # directory, is a collision waiting for the second AOT .NET package.
  install -m 0755 "$pub/$pkg" "$stage/usr/lib/$pkg/$pkg"
  ln -s "../lib/$pkg/$pkg" "$stage/usr/bin/$pkg"

  local lib
  for lib in "${native_libs[@]}"; do
    install -m 0644 "$lib" "$stage/usr/lib/$pkg/$(basename "$lib")"
  done

  local example
  for example in ${examples[$pkg]}; do
    install -m 0644 "$root/packaging/$example.example" "$stage/usr/share/doc/$pkg/examples/$example"
  done

  # Man pages must be gzipped with no timestamp (-n), or the .deb is not
  # reproducible and lintian complains.
  gzip -9nc "$root/man/$pkg.1" > "$stage/usr/share/man/man1/$pkg.1.gz"
  chmod 0644 "$stage/usr/share/man/man1/$pkg.1.gz"

  # Debian wants a machine-readable copyright and a changelog. The changelog is
  # generated rather than tracked: the GitHub Release notes are the real record,
  # and a hand-maintained duplicate would only drift.
  cat > "$stage/usr/share/doc/$pkg/copyright" <<COPYRIGHT
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: $pkg
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
  chmod 0644 "$stage/usr/share/doc/$pkg/copyright"

  printf '%s (%s) unstable; urgency=medium\n\n  * Release %s. See https://github.com/packet-net/axcall/releases/tag/v%s\n\n -- Tom Fanning <tom@m0lte.uk>  %s\n' \
    "$pkg" "$deb_version" "$version" "$version" "$(date -R)" \
    | gzip -9nc > "$stage/usr/share/doc/$pkg/changelog.Debian.gz"
  chmod 0644 "$stage/usr/share/doc/$pkg/changelog.Debian.gz"

  local depends="" soname dep
  while read -r soname; do
    dep="${soname_to_pkg[$soname]:-}"
    [ -n "$dep" ] || { echo "unmapped shared library dependency: $soname" >&2; exit 1; }
    case ",$depends," in *",$dep,"*) ;; *) depends="${depends:+$depends,}$dep" ;; esac
  done < <(objdump -p "$pub/$pkg" "${native_libs[@]}" | awk '/NEEDED/ {print $2}' | sort -u)
  depends="${depends//,/, }"
  echo "==> $pkg depends: $depends"

  # Installed-Size is in KiB, and dpkg-deb does not compute it for us.
  local installed_size
  installed_size="$(du -s -k --apparent-size "$stage" | cut -f1)"

  sed -e "s/@VERSION@/$deb_version/" \
      -e "s/@ARCH@/$arch/" \
      -e "s/@INSTALLED_SIZE@/$installed_size/" \
      -e "s/@DEPENDS@/$depends/" \
      "$root/packaging/$pkg.control.in" > "$stage/DEBIAN/control"

  # md5sums is optional but expected; dpkg --verify and debsums use it.
  ( cd "$stage" && find usr -type f -print0 | sort -z | xargs -0 md5sum > DEBIAN/md5sums )
  chmod 0644 "$stage/DEBIAN/md5sums"

  mkdir -p "$root/artifacts"
  # --root-owner-group (dpkg >= 1.19): root:root files without fakeroot.
  # -Zxz: pin xz - dpkg-deb's zstd default (dpkg >= 1.21.18) can't be unpacked by
  # Debian Bullseye's dpkg, so a zstd .deb refuses to install there.
  dpkg-deb --build --root-owner-group -Zxz "$stage" "$out"

  echo "==> built $out"
  # Pure diagnostics from here. pipefail off: a `… | grep` that does not match,
  # or a `head` closing early, would otherwise abort the build on a debug echo.
  set +o pipefail
  dpkg-deb --info "$out"
  echo "--- contents ---"
  dpkg-deb --contents "$out" | awk '{print $1, $3, $6}'
  echo "--- conffiles (must be EMPTY: no upgrade prompt) ---"
  dpkg-deb --info "$out" | grep -i 'conffiles' || echo "    (no Conffiles - correct)"
  set -o pipefail

  if command -v lintian >/dev/null 2>&1; then
    # Informational only. A self-contained .NET binary trips several tags by
    # construction (it is a statically-bundled runtime with no shared-library
    # dependency information dpkg can read), and none of them are actionable.
    lintian "$out" || true
  else
    echo "(lintian not installed - skipping deb-lint)"
  fi
}

for pkg in "${packages[@]}"; do
  package_one "$pkg"
done
