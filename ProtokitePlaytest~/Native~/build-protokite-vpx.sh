#!/usr/bin/env bash
# Rebuilds Runtime/Plugins/x86_64/protokite_vpx.dll: libvpx 1.17.0 (VP8 and VP9, static, SIMD, static CRT, realtime only)
# linked into the flat C wrapper protokite_vpx.c. Maintainers only: studios use the DLL checked in beside the package.
#
# Needs, on 64-bit Windows: Git Bash, Visual Studio 2022 (any edition, or its Build Tools) with the C++ x64 tools, and the
# internet the first time (libvpx's source, nasm and make are downloaded into the work folder and checked against the
# hashes below).
#
# Usage: bash "ProtokitePlaytest~/Native~/build-protokite-vpx.sh" [work folder]   (default: %LOCALAPPDATA%/protokite-vpx-build)
#        bash "ProtokitePlaytest~/Native~/build-protokite-vpx.sh" --check-dll <dll>   checks a DLL without building
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
PACKAGE="$(cd "$HERE/.." && pwd)"
OUT="$PACKAGE/Runtime/Plugins/x86_64"
VSWHERE="/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe"
WINDOWS_TAR="/c/Windows/System32/tar.exe"   # reads the .zst make package; Git Bash's own tar cannot

LIBVPX_URL="https://github.com/webmproject/libvpx/archive/refs/tags/v1.17.0.tar.gz"
LIBVPX_SHA256="1020f184046187baa2985dbde38e0691f49c44088bca7a1842b0236c6081dc0a"
NASM_URL="https://www.nasm.us/pub/nasm/releasebuilds/2.16.03/win64/nasm-2.16.03-win64.zip"
NASM_SHA256="3ee4782247bcb874378d02f7eab4e294a84d3d15f3f6ee2de2f47a46aa7226e6"
MAKE_URL="https://mirror.msys2.org/msys/x86_64/make-4.4.1-2-x86_64.pkg.tar.zst"
MAKE_SHA256="2408af61717dae87b00c855b132769a125c708907fc94a46bb16dae076113e5c"

# Every function the package calls.
EXPORTS="pk_vpx_wrapper_version pk_vpx_version pk_vpx_has_codec pk_vpx_encoder_create pk_vpx_encode_i420 pk_vpx_flush
         pk_vpx_next_packet pk_vpx_encoder_error pk_vpx_encoder_destroy pk_vpx_decoder_create pk_vpx_decode_to_i420
         pk_vpx_decoder_error pk_vpx_decoder_destroy"

fail() { echo "error: $*" >&2; exit 1; }

# The newest Visual Studio 2022 with the C++ x64 tools, whichever edition.
[ -f "$VSWHERE" ] || fail "vswhere.exe is missing: install Visual Studio 2022 (any edition, or its Build Tools) with the C++ x64 tools."
VS="$("$VSWHERE" -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | tr -d '\r')"
[ -n "$VS" ] || fail "no Visual Studio 2022 with the C++ x64 tools was found. Add 'Desktop development with C++' in the Visual Studio Installer."
LINK="$(cygpath -w "$HERE/link-protokite-vpx.bat")"

# What a DLL must look like: every function the package calls, and nothing to load beside it but KERNEL32.
check_dll() {
  local dll=$1 inspection dependencies
  [ -f "$dll" ] || fail "$dll does not exist"
  # dumpbin writes CR LF through cmd.exe; the CRs go before anything is matched.
  inspection=$(cmd.exe //c "$LINK" "$VS" --inspect "$(cygpath -w "$dll")" | tr -d '\r') || fail "dumpbin could not read $dll"
  for export in $EXPORTS; do
    echo "$inspection" | grep -qE "[[:space:]]$export\$" || fail "$(basename "$dll") does not export $export"
  done
  dependencies=$(echo "$inspection" | grep -iE '^[[:space:]]+[a-z0-9_.-]+\.dll[[:space:]]*$' | tr -d ' ' | sort -u)
  [ "$dependencies" = "KERNEL32.dll" ] || fail "$(basename "$dll") must load nothing but KERNEL32.dll; it loads: $(echo $dependencies)"
}

if [ "${1:-}" = "--check-dll" ]; then
  check_dll "${2:?give the DLL to check}"
  echo "$2: exports every function the package calls, and loads nothing but KERNEL32.dll."
  exit 0
fi

# A Unix-style path: GNU tar reads the "C:" of a Windows path as a remote host.
WORK="$(cygpath -u "${1:-${LOCALAPPDATA:-$HOME}/protokite-vpx-build}")"
MSBUILD="$(cygpath -u "$VS")/MSBuild/Current/Bin/amd64/MSBuild.exe"
for tool in "$MSBUILD" "$WINDOWS_TAR"; do
  [ -f "$tool" ] || fail "$tool is missing."
done

mkdir -p "$WORK/downloads"

# Downloads a file once and refuses it when its hash differs from the one pinned here.
fetch() {
  local url=$1 sha=$2 file="$WORK/downloads/$(basename "$1")"
  [ -f "$file" ] || curl -fsSL -o "$file" "$url" || fail "could not download $url"
  local got
  got=$(sha256sum "$file" | cut -d' ' -f1)
  [ "$got" = "$sha" ] || fail "$file has SHA-256 $got, expected $sha. Delete it and run again, or check the pinned hash."
  echo "$file"
}

libvpx_archive=$(fetch "$LIBVPX_URL" "$LIBVPX_SHA256")
nasm_archive=$(fetch "$NASM_URL" "$NASM_SHA256")
make_archive=$(fetch "$MAKE_URL" "$MAKE_SHA256")

[ -d "$WORK/libvpx-1.17.0" ] || tar -xzf "$libvpx_archive" -C "$WORK"
[ -d "$WORK/nasm-2.16.03" ] || unzip -q "$nasm_archive" -d "$WORK"
if [ ! -f "$WORK/make/usr/bin/make.exe" ]; then
  mkdir -p "$WORK/make"
  (cd "$WORK/make" && "$WINDOWS_TAR" -xf "$(cygpath -w "$make_archive")")
fi
export PATH="$WORK/nasm-2.16.03:$WORK/make/usr/bin:$PATH"

# libvpx: a fresh configure each time, so a changed option is never mixed with an old build.
BUILD="$WORK/build"
rm -rf "$BUILD" && mkdir -p "$BUILD"
(
  cd "$BUILD"
  "$WORK/libvpx-1.17.0/configure" \
    --target=x86_64-win64-vs17 \
    --as=nasm \
    --enable-static-msvcrt \
    --enable-vp8 \
    --enable-vp9 \
    --enable-realtime-only \
    --disable-examples --disable-tools --disable-docs --disable-unit-tests > configure.log 2>&1 || { tail -20 configure.log; fail "libvpx's configure failed"; }
  make > make.log 2>&1 || { tail -20 make.log; fail "libvpx's make failed"; }
  "$MSBUILD" vpx.sln -m -p:Configuration=Release -p:Platform=x64 -v:minimal > msbuild.log 2>&1 || { tail -20 msbuild.log; fail "libvpx's MSBuild failed"; }
)
[ -f "$BUILD/x64/Release/vpxmt.lib" ] || fail "libvpx built no vpxmt.lib"

# The wrapper, linked against the static libvpx with the static CRT.
cmd.exe //c "$LINK" "$VS" "$(cygpath -w "$WORK/libvpx-1.17.0")" "$(cygpath -w "$BUILD")" "$(cygpath -w "$BUILD/wrapper")" \
  || fail "linking protokite_vpx.dll failed"
check_dll "$BUILD/wrapper/protokite_vpx.dll"

mkdir -p "$OUT"
cp "$BUILD/wrapper/protokite_vpx.dll" "$OUT/protokite_vpx.dll"
cp "$WORK/libvpx-1.17.0/LICENSE" "$OUT/libvpx-LICENSE.txt"
cp "$WORK/libvpx-1.17.0/PATENTS" "$OUT/libvpx-PATENTS.txt"
echo "Built $OUT/protokite_vpx.dll ($(stat -c %s "$OUT/protokite_vpx.dll") bytes, libvpx 1.17.0, VP8 and VP9), with libvpx's LICENSE and PATENTS."
echo "Open the package in a Unity project (Libraries/Unity/packages/com.protokite.playtest links to it) so Unity writes any new .meta."
