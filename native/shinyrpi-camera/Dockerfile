# Cross-builds libshinyrpi_camera.so for a Raspberry Pi from any machine that can run arm64
# containers (Docker Desktop on Apple silicon does this natively; on x86 it needs binfmt/qemu).
#
#   docker buildx build --platform linux/arm64 -o type=local,dest=./out native/shinyrpi-camera
#
# The result lands in ./out/libshinyrpi_camera.so.
#
# ---------------------------------------------------------------------------------------------
# The libcamera this image installs must be the one the device runs. That is a stronger
# requirement than "same Debian release", and getting it wrong is the single most likely way to
# end up with a shim that builds here and refuses to load there.
#
# Raspberry Pi OS is Debian-based but does NOT use Debian's libcamera. Debian bookworm ships
# libcamera 0.0.3, which predates most of the API this shim uses and which the CMake version
# gate rejects outright. Raspberry Pi OS pulls libcamera from archive.raspberrypi.com, where it
# is currently 0.5.x and tracks the Pi camera stack. So this image adds that repository and
# pins to it - exactly as the device does.
#
# If the appliance runs something other than Raspberry Pi OS bookworm, change the base image and
# the repository below to match, or skip the container entirely and build on the device itself
# with build.sh, which is always correct by construction.
# ---------------------------------------------------------------------------------------------

FROM --platform=linux/arm64 debian:bookworm-slim AS build

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
    && curl -fsSL https://archive.raspberrypi.com/debian/raspberrypi.gpg.key \
        | gpg --dearmor -o /usr/share/keyrings/raspberrypi-archive-keyring.gpg \
    && echo "deb [signed-by=/usr/share/keyrings/raspberrypi-archive-keyring.gpg] http://archive.raspberrypi.com/debian/ bookworm main" \
        > /etc/apt/sources.list.d/raspi.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        build-essential \
        cmake \
        pkg-config \
        libcamera-dev \
    && rm -rf /var/lib/apt/lists/*

# Fail early and legibly if the repository above stopped being the one that supplies libcamera -
# an apt pinning change would otherwise surface as a confusing CMake version error.
RUN set -eu; \
    version="$(pkg-config --modversion libcamera)"; \
    echo "libcamera $version"; \
    case "$version" in \
        0.0.*) echo "ERROR: got Debian's libcamera $version, not the Raspberry Pi build. Check the apt repository above." >&2; exit 1 ;; \
    esac

WORKDIR /src
COPY CMakeLists.txt ./
COPY include ./include
COPY src ./src

RUN cmake -S . -B build -DCMAKE_BUILD_TYPE=Release \
    && cmake --build build --parallel \
    && nm -D --defined-only build/libshinyrpi_camera.so | grep -c ' T shinyrpi_camera_' \
    && pkg-config --modversion libcamera > build/libcamera.version

# A scratch stage so `-o type=local` writes just the artifact rather than a whole filesystem.
FROM scratch AS export
COPY --from=build /src/build/libshinyrpi_camera.so /
COPY --from=build /src/build/libcamera.version /
