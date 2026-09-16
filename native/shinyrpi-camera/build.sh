#!/usr/bin/env bash
#
# Builds libshinyrpi_camera.so against whatever libcamera is installed.
#
#     ./build.sh [--output DIR] [--build-dir DIR] [--debug]
#
# Run this either on the Pi itself or inside a container matching the Pi's OS release. It must
# NOT be run on a build machine with a different libcamera than the target: this library links
# against a C++ library with no stable ABI, so the libcamera it compiles against and the one it
# loads at runtime have to be the same.
#
# See README.md.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_DIR="$SCRIPT_DIR/build"
OUTPUT_DIR=""
BUILD_TYPE="Release"

log()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mwarning\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merror\033[0m %s\n' "$*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --output)    OUTPUT_DIR="$2"; shift 2 ;;
        --build-dir) BUILD_DIR="$2";  shift 2 ;;
        --debug)     BUILD_TYPE="Debug"; shift ;;
        -h|--help)   sed -n '2,15p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) die "Unknown option: $1" ;;
    esac
done

[[ "$(uname -s)" == "Linux" ]] || die "libcamera exists only on Linux. Build this on the Pi, or in an arm64 container - see Dockerfile alongside this script."

command -v cmake        >/dev/null 2>&1 || die "cmake not found. Install it with: sudo apt install cmake build-essential"
command -v pkg-config   >/dev/null 2>&1 || die "pkg-config not found. Install it with: sudo apt install pkg-config"

# Checked here as well as in CMake so the failure names the apt package rather than arriving as
# a pkg_check_modules error the reader has to decode.
if ! pkg-config --exists libcamera; then
    die "libcamera development headers not found. Install them with: sudo apt install libcamera-dev"
fi

LIBCAMERA_VERSION="$(pkg-config --modversion libcamera)"
log "libcamera $LIBCAMERA_VERSION on $(uname -m)"

cmake -S "$SCRIPT_DIR" -B "$BUILD_DIR" -DCMAKE_BUILD_TYPE="$BUILD_TYPE"
cmake --build "$BUILD_DIR" --parallel "$(nproc)"

ARTIFACT="$BUILD_DIR/libshinyrpi_camera.so"
[[ -f "$ARTIFACT" ]] || die "Build reported success but $ARTIFACT is missing"

# A quick sanity pass on what actually came out. A shim that builds but exports nothing - the
# classic -fvisibility=hidden mistake - would otherwise only be discovered on the device, as an
# EntryPointNotFoundException with no useful context.
EXPORTED="$(nm -D --defined-only "$ARTIFACT" 2>/dev/null | grep -c ' T shinyrpi_camera_' || true)"
[[ "$EXPORTED" -ge 18 ]] || die "Only $EXPORTED shinyrpi_camera_* symbols were exported; expected 18. Check SHINYRPI_API in the header."

log "Exported $EXPORTED entry points"

if [[ -n "$OUTPUT_DIR" ]]; then
    mkdir -p "$OUTPUT_DIR"
    cp "$ARTIFACT" "$OUTPUT_DIR/"

    # Stamped next to the library so the appliance, the installer and anyone reading a support
    # bundle can all see which libcamera this binary is tied to without running it.
    cat > "$OUTPUT_DIR/libshinyrpi_camera.so.buildinfo" <<EOF
{
  "libcamera": "$LIBCAMERA_VERSION",
  "machine": "$(uname -m)",
  "os": "$( . /etc/os-release 2>/dev/null && echo "${PRETTY_NAME:-unknown}" )",
  "buildType": "$BUILD_TYPE"
}
EOF
    log "Installed to $OUTPUT_DIR/libshinyrpi_camera.so"
else
    log "Built $ARTIFACT"
fi
