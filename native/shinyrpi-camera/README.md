# shinyrpi-camera — a real libcamera binding, and what it costs

This is the missing C layer between libcamera and .NET.

libcamera publishes a C++ API only, with no stable ABI and no C wrapper. That single fact is
why every "libcamera library for .NET" you can find on NuGet — `LibCamera.Net`,
`Iot.Device.Camera`, and the rest — actually shells out to `rpicam-still` and parses stdout.
Process-per-photograph is fine for a photograph. It is not fine for a continuous feed on a
four-core device that is also running a web server, a BLE peripheral and a job scheduler.

So this shim does the thing those libraries avoid: it links against libcamera's C++ API and
exposes a flat C ABI that `LibraryImport` can call. Frames come straight from the capture
pipeline as pointers into memory-mapped DMA buffers.

**The cost is a permanent maintenance tax, and this document is that tax written down.** Read
it before you upgrade libcamera on a fleet.

---

## What this buys

| | rpicam-apps wrappers | this shim |
|---|---|---|
| Per frame | fork, exec, pipe, parse | one memcpy out of a mapped buffer |
| Continuous streaming | one long-lived child process, framed stdout | native request queue |
| Controls | rebuild the command line, restart the process | set on the next request |
| Camera enumeration | parse `--list-cameras` from stderr | structured, typed |
| Errors | exit codes and English prose | typed result codes with libcamera's own message |
| Failure to build | n/a | the whole feature is gone |

That last row is the trade. Be sure it is worth it before adopting this — for an appliance
that takes one photograph an hour, it is not.

---

## Layout

```
include/shinyrpi_camera.h   the ABI. 18 functions, 4 structs, no libcamera types
src/shinyrpi_camera.cpp     the implementation. All libcamera contact is here
CMakeLists.txt              version gate and API probes
build.sh                    build on the device, or in a matching container
Dockerfile                  arm64 cross-build pinned to the Raspberry Pi apt repository
```

The managed side is `src/Shiny.AppDeviceBridge.RpiCamera`.

---

## Building

**On the device** — always correct, because it compiles against exactly the libcamera that
will load it:

```bash
sudo apt install build-essential cmake pkg-config libcamera-dev
native/shinyrpi-camera/build.sh --output /opt/myappliance
```

**In a container** — reproducible, from any machine that runs arm64 containers:

```bash
docker buildx build --platform linux/arm64 \
    -o type=local,dest=native/shinyrpi-camera/build \
    native/shinyrpi-camera
```

Ship the `.so` beside your published assemblies, or point `RpiCameraBridgeOptions.Camera.NativeLibraryPath`
at it. The project file copies `build/libshinyrpi_camera.so` into the output when a build has produced one.

### The container base is not a free choice

Raspberry Pi OS is Debian-based but does **not** use Debian's libcamera:

| Source | libcamera |
|---|---|
| `debian:bookworm` | 0.0.3 — too old, the CMake gate rejects it |
| `archive.raspberrypi.com` bookworm | 0.5.x — what the device actually runs |

The Dockerfile adds the Raspberry Pi repository for this reason. Building against Debian's
libcamera would produce a shim linked to `libcamera.so.0.0`, which no Raspberry Pi OS device
can load. If your appliance runs something else, change the base image to match it — or build
on the device and stop thinking about it.

---

## The tax, itemised

### 1. The shim is pinned to a libcamera soname

```
$ objdump -p libshinyrpi_camera.so | grep NEEDED
  NEEDED   libcamera.so.0.5
  NEEDED   libcamera-base.so.0.5
```

libcamera bumps its soname on most releases. When a device upgrades from 0.5 to 0.6, this
`.so` stops loading — `dlopen` fails, and that is the end of it.

**What happens:** the device still starts. `ICameraService.IsSupported` is false and
`BackendDescription` carries the loader's message verbatim. Nothing crashes; the camera is
simply gone until someone rebuilds.

**What to do:** rebuild the shim. Nothing else. No source changes are needed for a soname
bump alone.

**How to avoid being surprised:** compare the shim's `NEEDED` soname against `ldconfig -p` when you install, and
warn before you have a fleet of blind devices. `GET /_bridge/rpicamera` reports the loader's message as `backend`.

### 2. libcamera can change its API, not only its ABI

Rarer, and worse, because it needs a code change. Two known instances are already handled:

- **`ControlList::get(Control<T>)` began returning `std::optional<T>` in 0.1.** The shim uses
  the reference-returning `get(unsigned int)` overload everywhere instead, which has not
  changed. CMake probes for it and fails the build with a pointer to this section if it ever
  goes away.
- **`generateConfiguration` changed from `const StreamRoles &` to `Span<const StreamRole>`.**
  The shim passes a named `std::vector` lvalue, which binds to both. A braced-init-list would
  bind only to the first.

`CMakeLists.txt` also probes for `FrameBuffer::Plane::offset`, without which multi-planar
frames would be silently wrong rather than loudly broken — the failure mode most worth
converting into a build error.

**If a probe fails,** the build stops and names what changed. Fix it in
`src/shinyrpi_camera.cpp`; nothing above that file needs to know.

### 3. Two ABIs, and only one of them is checked by a compiler

The C++ boundary is checked when the shim builds. The C boundary between the shim and .NET is
not checked by anything, so it checks itself:

- **Version handshake.** `SHINYRPI_CAMERA_ABI_VERSION` in the header and
  `NativeMethods.ExpectedAbiVersion` in C# must agree. A mismatch is refused at load time with
  a message naming the rebuild.
- **Size handshake.** Every struct starts with `struct_size`. C# stamps it, the shim rejects
  anything it does not recognise. Layout drift becomes `SHINYRPI_ERR_ABI`, not memory
  corruption.
- **Pinned layouts.** `tests/Shiny.AppDeviceBridge.Tests/RpiCamera/CameraInteropTests.cs` asserts the exact byte size
  of each struct, so a field added on the managed side without touching the header fails
  `dotnet test` rather than a device.

**When you change anything in the header:** bump `SHINYRPI_CAMERA_ABI_VERSION`, bump
`ExpectedAbiVersion` to match, update the size assertions in the tests, and rebuild both sides.

### 4. Controls are deliberately not part of the ABI

libcamera identifies controls with numeric ids generated from `control_ids.yaml`. Those ids
are not contractual. So none of them cross this boundary: the header defines its own
`SHINYRPI_CTRL_*` constants, and the shim maps each to a libcamera control **name string**,
which it looks up in the attached sensor's own `ControlInfoMap` at runtime.

Two things fall out of this, both good:

- libcamera renumbering a control cannot reach .NET.
- A control the attached sensor does not implement returns `SHINYRPI_ERR_NOT_FOUND`, surfaced
  as `TrySetControl` returning false. A Camera Module 3 has autofocus; a v2 does not; the same
  binary handles both.

---

## When libcamera moves: the checklist

1. Rebuild the shim (`build.sh` on a device running the new libcamera).
2. If CMake fails a probe, it names the API that changed. Fix `src/shinyrpi_camera.cpp`.
3. `dotnet test` — the layout assertions still have to pass.
4. Deploy, checking the soname on the way in.

If step 2 ever gets expensive enough to argue about, the honest fallback is
`Iot.Device.Camera` and a `rpicam-vid` process. `ICameraService` exists
partly so that swap is a new implementation rather than a rewrite.

---

## Design notes

**No callback into managed code.** libcamera raises `requestCompleted` on its own event
thread. Calling .NET from there would run consumer code on a thread the pipeline needs back
within a frame interval. Instead the shim owns a bounded queue; .NET blocks on
`shinyrpi_camera_dequeue_frame` from a dedicated thread and posts to a channel.

**A slow reader drops frames rather than stalling the sensor.** When the queue is full the
oldest completed frame is recycled and counted in `frames_dropped`. A live preview wants the
newest frame, not a backlog. `ICameraSession.GetStatistics()` exposes the counter.

**Frames are copied out.** `shinyrpi_frame.plane_data` points into a mapped DMA buffer that
libcamera needs back promptly, which is an unreasonable lifetime to hand to arbitrary consumer
code. `CameraFrame` copies into a pooled array; the buffer is released before the frame is
handed on.

**Two buffers always stay with the sensor.** `queue_depth` is clamped to `buffer_count - 2` at
configure time, so a reader holding frames can never starve the pipeline entirely.

**Only 18 symbols are exported.** `-fvisibility=hidden` plus `SHINYRPI_API` keeps every
libcamera symbol local to the `.so`, so it cannot collide with anything else in the process.
`build.sh` counts them and fails if the number is wrong.
