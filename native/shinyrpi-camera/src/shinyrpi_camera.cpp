/*
 * shinyrpi_camera - implementation.
 *
 * Everything libcamera-shaped is confined to this file. The rules it follows, all of them
 * chosen to limit how much a libcamera release can hurt us:
 *
 *   - Only long-stable libcamera API is used. In particular ControlList::get(unsigned int),
 *     which returns a reference, is used everywhere in preference to the templated
 *     get(Control<T>) - the latter changed its return type to std::optional<T> in libcamera
 *     0.1 and would not compile against both sides of that change.
 *   - Controls are resolved by name from the sensor's own ControlInfoMap at runtime. No
 *     libcamera control identifier is baked in, so a renumbering is invisible to us and a
 *     control the sensor lacks degrades to a clean SHINYRPI_ERR_NOT_FOUND.
 *   - The frame queue is owned here, and the managed side blocks on it. There is no reverse
 *     callback into managed code from a libcamera thread.
 */

#include "shinyrpi_camera.h"

#include <libcamera/libcamera.h>
#include <libcamera/control_ids.h>
#include <libcamera/formats.h>
#include <libcamera/framebuffer_allocator.h>
#include <libcamera/property_ids.h>

#include <sys/mman.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <map>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <unordered_map>
#include <vector>

namespace {

/* ------------------------------------------------------------------------------------- */
/* Error reporting                                                                         */
/* ------------------------------------------------------------------------------------- */

/*
 * Per-thread so that two managed threads - typically one driving the capture loop and one
 * changing controls - cannot overwrite each other's diagnostics.
 */
thread_local std::string t_lastError;

int32_t fail(int32_t code, std::string message)
{
    t_lastError = std::move(message);
    return code;
}

int32_t ok()
{
    t_lastError.clear();
    return SHINYRPI_OK;
}

std::string errnoText(int ret)
{
    return std::string(strerror(ret < 0 ? -ret : ret));
}

/*
 * Every struct crossing the boundary carries its own size. A managed build that disagrees
 * with the shim's layout is caught here, on the first call that uses the struct, instead of
 * scribbling over the caller's stack.
 */
template <typename T>
bool layoutOk(const T *value)
{
    return value != nullptr && value->struct_size == static_cast<uint32_t>(sizeof(T));
}

template <typename T>
int32_t layoutError(const char *name, const T *value)
{
    if (value == nullptr)
        return fail(SHINYRPI_ERR_INVALID_ARGUMENT, std::string(name) + " is null");

    return fail(
        SHINYRPI_ERR_ABI,
        std::string(name) + " layout mismatch: caller says " + std::to_string(value->struct_size)
            + " bytes, shim was built with " + std::to_string(sizeof(T))
            + ". The managed binding and libshinyrpi_camera.so are from different revisions."
    );
}

/* ------------------------------------------------------------------------------------- */
/* Control mapping                                                                         */
/* ------------------------------------------------------------------------------------- */

/*
 * Our control ids to libcamera control *names*. Names have proven far more stable than the
 * numeric ids, and resolving them against the live sensor is the whole point - a Camera
 * Module 3 exposes AfMode and LensPosition, a v2 module does not, and neither case should
 * need a different build.
 */
const char *controlName(int32_t control)
{
    switch (control)
    {
        case SHINYRPI_CTRL_AE_ENABLE:           return "AeEnable";
        case SHINYRPI_CTRL_EXPOSURE_TIME:       return "ExposureTime";
        case SHINYRPI_CTRL_ANALOGUE_GAIN:       return "AnalogueGain";
        case SHINYRPI_CTRL_AWB_ENABLE:          return "AwbEnable";
        case SHINYRPI_CTRL_AWB_MODE:            return "AwbMode";
        case SHINYRPI_CTRL_BRIGHTNESS:          return "Brightness";
        case SHINYRPI_CTRL_CONTRAST:            return "Contrast";
        case SHINYRPI_CTRL_SATURATION:          return "Saturation";
        case SHINYRPI_CTRL_SHARPNESS:           return "Sharpness";
        case SHINYRPI_CTRL_EXPOSURE_VALUE:      return "ExposureValue";
        case SHINYRPI_CTRL_FRAME_DURATION_MIN:  return "FrameDurationLimits";
        case SHINYRPI_CTRL_FRAME_DURATION_MAX:  return "FrameDurationLimits";
        case SHINYRPI_CTRL_AF_MODE:             return "AfMode";
        case SHINYRPI_CTRL_LENS_POSITION:       return "LensPosition";
        case SHINYRPI_CTRL_NOISE_REDUCTION:     return "NoiseReductionMode";
        default:                                return nullptr;
    }
}

const libcamera::ControlId *findControl(
    const libcamera::ControlInfoMap  &controls,
    const char                       *name,
    const libcamera::ControlInfo    **outInfo
)
{
    for (const auto &entry : controls)
    {
        if (entry.first->name() == name)
        {
            if (outInfo != nullptr)
                *outInfo = &entry.second;

            return entry.first;
        }
    }

    return nullptr;
}

/* Widens whatever the sensor declared into the double the ABI speaks in. */
bool controlValueToDouble(const libcamera::ControlValue &value, double *out)
{
    if (value.isNone())
        return false;

    switch (value.type())
    {
        case libcamera::ControlTypeBool:      *out = value.get<bool>() ? 1.0 : 0.0;   return true;
        case libcamera::ControlTypeByte:      *out = value.get<uint8_t>();            return true;
        case libcamera::ControlTypeInteger32: *out = value.get<int32_t>();            return true;
        case libcamera::ControlTypeInteger64: *out = static_cast<double>(value.get<int64_t>()); return true;
        case libcamera::ControlTypeFloat:     *out = value.get<float>();              return true;
        default:                                                                      return false;
    }
}

/* And the reverse: narrows to whatever the sensor actually wants. */
bool doubleToControlValue(libcamera::ControlType type, double value, libcamera::ControlValue *out)
{
    switch (type)
    {
        case libcamera::ControlTypeBool:      *out = libcamera::ControlValue(value != 0.0); return true;
        case libcamera::ControlTypeByte:      *out = libcamera::ControlValue(static_cast<uint8_t>(value)); return true;
        case libcamera::ControlTypeInteger32: *out = libcamera::ControlValue(static_cast<int32_t>(value)); return true;
        case libcamera::ControlTypeInteger64: *out = libcamera::ControlValue(static_cast<int64_t>(value)); return true;
        case libcamera::ControlTypeFloat:     *out = libcamera::ControlValue(static_cast<float>(value)); return true;
        default:                                                                       return false;
    }
}

libcamera::StreamRole toStreamRole(uint32_t role)
{
    switch (role)
    {
        case SHINYRPI_ROLE_STILL: return libcamera::StreamRole::StillCapture;
        case SHINYRPI_ROLE_VIDEO: return libcamera::StreamRole::VideoRecording;
        case SHINYRPI_ROLE_RAW:   return libcamera::StreamRole::Raw;
        default:                  return libcamera::StreamRole::Viewfinder;
    }
}

/*
 * Reads a camera property without the templated ControlList::get. The reference-returning
 * overload yields an empty ControlValue for an absent property, which isNone() detects.
 */
const libcamera::ControlValue &property(const libcamera::ControlList &properties, unsigned int id)
{
    return properties.get(id);
}

/* ------------------------------------------------------------------------------------- */
/* Camera manager - libcamera allows exactly one per process                               */
/* ------------------------------------------------------------------------------------- */

std::mutex                                g_managerMutex;
std::weak_ptr<libcamera::CameraManager>    g_manager;

std::shared_ptr<libcamera::CameraManager> acquireManager(std::string *error)
{
    std::lock_guard<std::mutex> lock(g_managerMutex);

    if (auto existing = g_manager.lock())
        return existing;

    std::shared_ptr<libcamera::CameraManager> manager(
        new (std::nothrow) libcamera::CameraManager(),
        [](libcamera::CameraManager *value) {
            value->stop();
            delete value;
        }
    );

    if (!manager)
    {
        *error = "out of memory allocating CameraManager";
        return nullptr;
    }

    const int ret = manager->start();
    if (ret != 0)
    {
        *error = "CameraManager::start failed: " + errnoText(ret)
            + ". Check that the camera is enabled and that this process can reach /dev/media*.";
        return nullptr;
    }

    g_manager = manager;
    return manager;
}

} // namespace

/* ------------------------------------------------------------------------------------- */
/* Buffer bookkeeping                                                                      */
/* ------------------------------------------------------------------------------------- */

/* Outside the anonymous namespace: these appear in the members of the handle structs below,
 * which have external linkage. */
struct MappedBuffer
{
    uint8_t *memory = nullptr;
    size_t   length = 0;
};

struct PendingFrame
{
    uint64_t                frameId = 0;
    libcamera::Request     *request = nullptr;
    libcamera::FrameBuffer *buffer  = nullptr;
};

/* ------------------------------------------------------------------------------------- */
/* Handles                                                                                 */
/* ------------------------------------------------------------------------------------- */

struct shinyrpi_manager_s
{
    std::shared_ptr<libcamera::CameraManager> manager;
};

struct shinyrpi_camera_s
{
    std::shared_ptr<libcamera::CameraManager>       manager;
    std::shared_ptr<libcamera::Camera>              camera;
    std::unique_ptr<libcamera::CameraConfiguration> configuration;
    std::unique_ptr<libcamera::FrameBufferAllocator> allocator;
    libcamera::Stream                              *stream = nullptr;

    std::vector<std::unique_ptr<libcamera::Request>> requests;
    std::map<libcamera::FrameBuffer *, MappedBuffer> mapped;

    uint32_t width       = 0;
    uint32_t height      = 0;
    uint32_t stride      = 0;
    uint32_t pixelFormat = 0;
    uint32_t queueDepth  = 1;

    /* Guards everything below it. Never held across a call into libcamera. */
    std::mutex                                mutex;
    std::condition_variable                   ready;
    std::deque<PendingFrame>                  completed;
    std::unordered_map<uint64_t, PendingFrame> checkedOut;
    uint64_t                                  nextFrameId  = 1;
    bool                                      running      = false;
    bool                                      wakeRequested = false;

    /* Separate lock: controls are set from the caller's thread while the capture thread is
     * inside requestComplete applying them to a recycled request. */
    std::mutex           controlMutex;
    libcamera::ControlList pendingControls;
    int64_t              frameDurationMin = 0;
    int64_t              frameDurationMax = 0;

    bool     signalConnected = false;
    uint64_t statCompleted   = 0;
    uint64_t statDelivered   = 0;
    uint64_t statDropped     = 0;
    uint64_t statErrored     = 0;

    void requestComplete(libcamera::Request *request);
    int  recycle(libcamera::Request *request);
    void applyControls(libcamera::Request *request);
    void unmapAll();
};

void shinyrpi_camera_s::applyControls(libcamera::Request *request)
{
    std::lock_guard<std::mutex> lock(this->controlMutex);

    /* Copied entry by entry rather than merged: ControlList::merge grew a policy argument
     * partway through libcamera 0.x and this avoids caring. */
    for (const auto &entry : this->pendingControls)
        request->controls().set(entry.first, entry.second);
}

/*
 * Resets a request and puts it back in the queue. reuse() is not optional and not only for
 * requests that have already completed: after a stop/start cycle every request is left in a
 * completed or cancelled state, and queueRequest rejects those outright. Calling it on a fresh
 * request is harmless, so this is the single path used for both the initial queueing and every
 * requeue afterwards.
 */
int shinyrpi_camera_s::recycle(libcamera::Request *request)
{
    request->reuse(libcamera::Request::ReuseBuffers);
    this->applyControls(request);
    return this->camera->queueRequest(request);
}

/*
 * Runs on libcamera's CameraManager thread. It takes the queue lock, decides what to do, and
 * gets out - any requeue happens after the lock is dropped, because queueRequest reaches back
 * into libcamera and holding our lock across that is how lock-order bugs start.
 */
void shinyrpi_camera_s::requestComplete(libcamera::Request *request)
{
    if (request->status() == libcamera::Request::RequestCancelled)
        return;

    std::vector<libcamera::Request *> toRecycle;
    bool notify = false;

    {
        std::lock_guard<std::mutex> lock(this->mutex);
        this->statCompleted++;

        /* Stopping. camera->stop() will cancel and reclaim this request; leave it alone. */
        if (!this->running)
            return;

        libcamera::FrameBuffer *buffer = nullptr;
        const auto              it     = request->buffers().find(this->stream);
        if (it != request->buffers().end())
            buffer = it->second;

        if (buffer == nullptr || buffer->metadata().status != libcamera::FrameMetadata::FrameSuccess)
        {
            this->statErrored++;
            toRecycle.push_back(request);
        }
        else
        {
            /* A reader that has fallen behind must not stall the sensor, so the oldest
             * frames go back to libcamera and are counted as dropped. Live preview wants the
             * newest frame, not a backlog of stale ones. */
            while (this->completed.size() >= this->queueDepth)
            {
                toRecycle.push_back(this->completed.front().request);
                this->completed.pop_front();
                this->statDropped++;
            }

            PendingFrame frame;
            frame.frameId = this->nextFrameId++;
            frame.request = request;
            frame.buffer  = buffer;
            this->completed.push_back(frame);
            notify = true;
        }
    }

    for (libcamera::Request *item : toRecycle)
        (void) this->recycle(item);

    if (notify)
        this->ready.notify_one();
}

void shinyrpi_camera_s::unmapAll()
{
    for (auto &entry : this->mapped)
    {
        if (entry.second.memory != nullptr)
            munmap(entry.second.memory, entry.second.length);
    }

    this->mapped.clear();
}

/* ------------------------------------------------------------------------------------- */
/* Library                                                                                 */
/* ------------------------------------------------------------------------------------- */

extern "C" int32_t shinyrpi_camera_abi_version(void)
{
    return SHINYRPI_CAMERA_ABI_VERSION;
}

extern "C" const char *shinyrpi_camera_libcamera_version(void)
{
    /* Baked in by CMake from pkg-config, so the managed side can log which libcamera this
     * shim was actually built against - the single most useful fact when it stops loading. */
    return SHINYRPI_LIBCAMERA_VERSION;
}

extern "C" const char *shinyrpi_camera_last_error(void)
{
    return t_lastError.c_str();
}

/* ------------------------------------------------------------------------------------- */
/* Manager                                                                                 */
/* ------------------------------------------------------------------------------------- */

extern "C" int32_t shinyrpi_camera_manager_create(shinyrpi_manager **out_manager)
{
    if (out_manager == nullptr)
        return fail(SHINYRPI_ERR_INVALID_ARGUMENT, "out_manager is null");

    *out_manager = nullptr;

    std::string error;
    auto        manager = acquireManager(&error);
    if (!manager)
        return fail(SHINYRPI_ERR_IO, error);

    auto *handle = new (std::nothrow) shinyrpi_manager_s();
    if (handle == nullptr)
        return fail(SHINYRPI_ERR_NO_MEMORY, "out of memory");

    handle->manager = std::move(manager);
    *out_manager    = handle;
    return ok();
}

extern "C" void shinyrpi_camera_manager_destroy(shinyrpi_manager *manager)
{
    delete manager;
}

extern "C" int32_t shinyrpi_camera_manager_count(shinyrpi_manager *manager, int32_t *out_count)
{
    if (manager == nullptr || out_count == nullptr)
        return fail(SHINYRPI_ERR_INVALID_ARGUMENT, "manager or out_count is null");

    *out_count = static_cast<int32_t>(manager->manager->cameras().size());
    return ok();
}

extern "C" int32_t shinyrpi_camera_manager_info(shinyrpi_manager *manager, int32_t index, shinyrpi_camera_info *out_info)
{
    if (manager == nullptr)
        return fail(SHINYRPI_ERR_INVALID_ARGUMENT, "manager is null");

    if (!layoutOk(out_info))
        return layoutError("shinyrpi_camera_info", out_info);

    const auto cameras = manager->manager->cameras();
    if (index < 0 || static_cast<size_t>(index) >= cameras.size())
        return fail(SHINYRPI_ERR_NOT_FOUND, "no camera at index " + std::to_string(index));

    const std::shared_ptr<libcamera::Camera> &camera = cameras[static_cast<size_t>(index)];

    /* Preserve struct_size - the caller set it and the rest of this function zeroes around it. */
    const uint32_t structSize = out_info->struct_size;
    std::memset(out_info, 0, sizeof(*out_info));
    out_info->struct_size = structSize;

    const std::string id = camera->id();
    std::strncpy(out_info->id, id.c_str(), SHINYRPI_ID_MAX - 1);

    const libcamera::ControlList &properties = camera->properties();

    const libcamera::ControlValue &model = property(properties, libcamera::properties::Model.id());
    if (!model.isNone())
        std::strncpy(out_info->model, model.get<std::string>().c_str(), SHINYRPI_MODEL_MAX - 1);

    const libcamera::ControlValue &pixelArray = property(properties, libcamera::properties::PixelArraySize.id());
    if (!pixelArray.isNone())
    {
        const libcamera::Size size = pixelArray.get<libcamera::Size>();
        out_info->max_width  = size.width;
        out_info->max_height = size.height;
    }

    const libcamera::ControlValue &rotation = property(properties, libcamera::properties::Rotation.id());
    if (!rotation.isNone())
        out_info->rotation_degrees = rotation.get<int32_t>();

    out_info->location = SHINYRPI_LOCATION_EXTERNAL;

    const libcamera::ControlValue &location = property(properties, libcamera::properties::Location.id());
    if (!location.isNone())
    {
        switch (location.get<int32_t>())
        {
            case libcamera::properties::CameraLocationFront: out_info->location = SHINYRPI_LOCATION_FRONT; break;
            case libcamera::properties::CameraLocationBack:  out_info->location = SHINYRPI_LOCATION_BACK;  break;
            default:                                         out_info->location = SHINYRPI_LOCATION_EXTERNAL; break;
        }
    }

    return ok();
}

/* ------------------------------------------------------------------------------------- */
/* Camera                                                                                  */
/* ------------------------------------------------------------------------------------- */

extern "C" int32_t shinyrpi_camera_open(shinyrpi_manager *manager, const char *id, shinyrpi_camera **out_camera)
{
    if (manager == nullptr || out_camera == nullptr)
        return fail(SHINYRPI_ERR_INVALID_ARGUMENT, "manager or out_camera is null");

    *out_camera = nullptr;

    std::shared_ptr<libcamera::Camera> camera;
    if (id != nullptr && id[0] != '\0')
    {
        camera = manager->manager->get(std::string(id));
        if (!camera)
            return fail(SHINYRPI_ERR_NOT_FOUND, std::string("no camera with id '") + id + "'");
    }
    else
    {
        const auto cameras = manager->manager->cameras();
        if (cameras.empty())
            return fail(SHINYRPI_ERR_NOT_FOUND, "no cameras attached");

        camera = cameras[0];
    }

    const int ret = camera->acquire();
    if (ret != 0)
    {
        /* EBUSY here almost always means rpicam-apps, another appliance instance, or a
         * previous run that did not shut down cleanly still holds the sensor. */
        return fail(
            ret == -EBUSY ? SHINYRPI_ERR_BUSY : SHINYRPI_ERR_IO,
            "Camera::acquire failed: " + errnoText(ret)
        );
    }

    auto *handle = new (std::nothrow) shinyrpi_camera_s();
    if (handle == nullptr)
    {
        camera->release();
        return fail(SHINYRPI_ERR_NO_MEMORY, "out of memory");
    }

    handle->manager = manager->manager;
    handle->camera  = std::move(camera);
    *out_camera     = handle;
    return ok();
}

extern "C" void shinyrpi_camera_close(shinyrpi_camera *camera)
{
    if (camera == nullptr)
        return;

    (void) shinyrpi_camera_stop(camera);

    if (camera->signalConnected)
    {
        camera->camera->requestCompleted.disconnect(camera, &shinyrpi_camera_s::requestComplete);
        camera->signalConnected = false;
    }

    camera->requests.clear();
    camera->unmapAll();

    if (camera->allocator && camera->stream != nullptr)
        camera->allocator->free(camera->stream);

    camera->allocator.reset();
    camera->configuration.reset();
    camera->camera->release();

    delete camera;
}

extern "C" int32_t shinyrpi_camera_configure(shinyrpi_camera *camera, shinyrpi_stream_config *config)
{
    if (camera == nullptr)
        return fail(SHINYRPI_ERR_INVALID_ARGUMENT, "camera is null");

    if (!layoutOk(config))
        return layoutError("shinyrpi_stream_config", config);

    if (camera->running)
        return fail(SHINYRPI_ERR_INVALID_STATE, "cannot reconfigure while the camera is running");

    /* Tear down any previous configuration before generating a new one. */
    camera->requests.clear();
    camera->unmapAll();
    if (camera->allocator && camera->stream != nullptr)
        camera->allocator->free(camera->stream);
    camera->allocator.reset();
    camera->stream = nullptr;

    /* Passed as a named vector lvalue rather than a braced list: libcamera changed this
     * parameter from const StreamRoles & to Span<const StreamRole>, and a vector binds to
     * both while a braced-init-list binds only to the first. */
    std::vector<libcamera::StreamRole> roles = { toStreamRole(config->role) };

    auto configuration = camera->camera->generateConfiguration(roles);
    if (!configuration || configuration->size() != 1)
        return fail(SHINYRPI_ERR_CONFIGURATION, "the camera did not offer a single stream for this role");

    libcamera::StreamConfiguration &streamConfig = configuration->at(0);

    if (config->width != 0)
        streamConfig.size.width = config->width;

    if (config->height != 0)
        streamConfig.size.height = config->height;

    if (config->pixel_format != 0)
        streamConfig.pixelFormat = libcamera::PixelFormat(config->pixel_format);

    if (config->buffer_count != 0)
        streamConfig.bufferCount = config->buffer_count;

    /*
     * validate() rewrites anything the pipeline cannot honour rather than refusing it, so
     * Adjusted is a success - but the caller has to be told, which is why every field is
     * written back below.
     */
    const libcamera::CameraConfiguration::Status status = configuration->validate();
    if (status == libcamera::CameraConfiguration::Invalid)
        return fail(SHINYRPI_ERR_CONFIGURATION, "the requested stream configuration cannot be satisfied");

    const int ret = camera->camera->configure(configuration.get());
    if (ret != 0)
        return fail(SHINYRPI_ERR_CONFIGURATION, "Camera::configure failed: " + errnoText(ret));

    camera->configuration = std::move(configuration);
    camera->stream        = camera->configuration->at(0).stream();

    camera->allocator = std::unique_ptr<libcamera::FrameBufferAllocator>(
        new (std::nothrow) libcamera::FrameBufferAllocator(camera->camera)
    );
    if (!camera->allocator)
        return fail(SHINYRPI_ERR_NO_MEMORY, "out of memory allocating the frame buffer allocator");

    const int allocated = camera->allocator->allocate(camera->stream);
    if (allocated < 0)
        return fail(SHINYRPI_ERR_NO_MEMORY, "FrameBufferAllocator::allocate failed: " + errnoText(allocated));

    /*
     * Map every capture buffer once, up front. libcamera hands out dmabuf file descriptors;
     * on the Pi all planes of a buffer live in one allocation at different offsets, which is
     * what the single-fd check below asserts.
     */
    for (const std::unique_ptr<libcamera::FrameBuffer> &buffer : camera->allocator->buffers(camera->stream))
    {
        size_t length = 0;
        int    fd     = -1;

        for (const libcamera::FrameBuffer::Plane &plane : buffer->planes())
        {
            if (fd == -1)
                fd = plane.fd.get();
            else if (fd != plane.fd.get())
                return fail(SHINYRPI_ERR_UNSUPPORTED, "buffer planes span multiple dmabufs, which this shim does not map");

            length = std::max(length, static_cast<size_t>(plane.offset) + plane.length);
        }

        if (fd == -1 || length == 0)
            return fail(SHINYRPI_ERR_CONFIGURATION, "the allocator returned a buffer with no planes");

        void *memory = mmap(nullptr, length, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
        if (memory == MAP_FAILED)
            return fail(SHINYRPI_ERR_IO, "mmap of a capture buffer failed: " + errnoText(errno));

        MappedBuffer mappedBuffer;
        mappedBuffer.memory                 = static_cast<uint8_t *>(memory);
        mappedBuffer.length                 = length;
        camera->mapped[buffer.get()]        = mappedBuffer;

        std::unique_ptr<libcamera::Request> request = camera->camera->createRequest();
        if (!request)
            return fail(SHINYRPI_ERR_NO_MEMORY, "Camera::createRequest returned nothing");

        const int addRet = request->addBuffer(camera->stream, buffer.get());
        if (addRet != 0)
            return fail(SHINYRPI_ERR_IO, "Request::addBuffer failed: " + errnoText(addRet));

        camera->requests.push_back(std::move(request));
    }

    const libcamera::StreamConfiguration &applied = camera->configuration->at(0);
    camera->width       = applied.size.width;
    camera->height      = applied.size.height;
    camera->stride      = applied.stride;
    camera->pixelFormat = applied.pixelFormat.fourcc();

    /*
     * Two buffers must stay in libcamera's hands at all times or the sensor starves while the
     * reader holds everything. That ceiling is enforced here rather than trusted to the caller.
     */
    const uint32_t bufferCount = static_cast<uint32_t>(camera->requests.size());
    const uint32_t maxDepth    = bufferCount > 2 ? bufferCount - 2 : 1;
    const uint32_t wanted      = config->queue_depth != 0 ? config->queue_depth : 1;
    camera->queueDepth         = std::min(wanted, maxDepth);

    config->width        = camera->width;
    config->height       = camera->height;
    config->pixel_format = camera->pixelFormat;
    config->buffer_count = bufferCount;
    config->stride       = camera->stride;
    config->queue_depth  = camera->queueDepth;

    return ok();
}

extern "C" int32_t shinyrpi_camera_start(shinyrpi_camera *camera)
{
    if (camera == nullptr)
        return fail(SHINYRPI_ERR_INVALID_ARGUMENT, "camera is null");

    if (camera->stream == nullptr)
        return fail(SHINYRPI_ERR_INVALID_STATE, "configure must be called before start");

    if (camera->running)
        return ok();

    if (!camera->signalConnected)
    {
        camera->camera->requestCompleted.connect(camera, &shinyrpi_camera_s::requestComplete);
        camera->signalConnected = true;
    }

    {
        std::lock_guard<std::mutex> lock(camera->mutex);
        camera->completed.clear();
        camera->checkedOut.clear();
        camera->wakeRequested = false;
        camera->running       = true;
    }

    int ret;
    {
        /* Hand libcamera the controls set before start so the very first frame is correct
         * rather than converging over the next few. */
        std::lock_guard<std::mutex> lock(camera->controlMutex);
        ret = camera->camera->start(camera->pendingControls.empty() ? nullptr : &camera->pendingControls);
    }

    if (ret != 0)
    {
        std::lock_guard<std::mutex> lock(camera->mutex);
        camera->running = false;
        return fail(SHINYRPI_ERR_IO, "Camera::start failed: " + errnoText(ret));
    }

    for (std::unique_ptr<libcamera::Request> &request : camera->requests)
    {
        const int queueRet = camera->recycle(request.get());
        if (queueRet != 0)
        {
            (void) shinyrpi_camera_stop(camera);
            return fail(SHINYRPI_ERR_IO, "Camera::queueRequest failed: " + errnoText(queueRet));
        }
    }

    return ok();
}

extern "C" int32_t shinyrpi_camera_stop(shinyrpi_camera *camera)
{
    if (camera == nullptr)
        return fail(SHINYRPI_ERR_INVALID_ARGUMENT, "camera is null");

    bool wasRunning;
    {
        std::lock_guard<std::mutex> lock(camera->mutex);
        wasRunning      = camera->running;
        camera->running = false;
    }

    /* Wake the reader before stopping: Camera::stop waits for in-flight requests, and those
     * completions take the same lock a blocked dequeue is holding. */
    camera->ready.notify_all();

    if (wasRunning)
        camera->camera->stop();

    {
        std::lock_guard<std::mutex> lock(camera->mutex);
        camera->completed.clear();
        camera->checkedOut.clear();
    }

    return ok();
}

extern "C" int32_t shinyrpi_camera_dequeue_frame(shinyrpi_camera *camera, int32_t timeout_ms, shinyrpi_frame *out_frame)
{
    if (camera == nullptr)
        return fail(SHINYRPI_ERR_INVALID_ARGUMENT, "camera is null");

    if (!layoutOk(out_frame))
        return layoutError("shinyrpi_frame", out_frame);

    PendingFrame frame;
    {
        std::unique_lock<std::mutex> lock(camera->mutex);

        const auto haveWork = [camera]() {
            return !camera->completed.empty() || !camera->running || camera->wakeRequested;
        };

        if (!haveWork())
        {
            if (timeout_ms < 0)
                camera->ready.wait(lock, haveWork);
            else
                camera->ready.wait_for(lock, std::chrono::milliseconds(timeout_ms), haveWork);
        }

        if (camera->wakeRequested)
        {
            camera->wakeRequested = false;
            return SHINYRPI_TIMEOUT;
        }

        if (camera->completed.empty())
        {
            if (!camera->running)
                return fail(SHINYRPI_ERR_INVALID_STATE, "the camera is not running");

            return SHINYRPI_TIMEOUT;
        }

        frame = camera->completed.front();
        camera->completed.pop_front();
        camera->checkedOut[frame.frameId] = frame;
        camera->statDelivered++;
    }

    /* Read without the lock: mapped is only ever written by configure, which cannot run while
     * the camera is started. */
    const auto mappedIt = camera->mapped.find(frame.buffer);
    if (mappedIt == camera->mapped.end())
        return fail(SHINYRPI_ERR_INVALID_STATE, "completed frame refers to an unmapped buffer");

    const uint32_t structSize = out_frame->struct_size;
    std::memset(out_frame, 0, sizeof(*out_frame));
    out_frame->struct_size = structSize;

    const auto &planes         = frame.buffer->planes();
    const auto &planeMetadata  = frame.buffer->metadata().planes();
    const uint32_t planeCount  = static_cast<uint32_t>(std::min<size_t>(planes.size(), SHINYRPI_MAX_PLANES));

    for (uint32_t i = 0; i < planeCount; i++)
    {
        out_frame->plane_data[i] = mappedIt->second.memory + planes[i].offset;

        /*
         * bytesused, not the plane's capacity. For a compressed format that difference is the
         * whole point - MJPEG writes a JPEG of whatever size it came out as into a buffer
         * sized for the worst case.
         */
        out_frame->plane_length[i] = i < planeMetadata.size()
            ? planeMetadata[i].bytesused
            : planes[i].length;
    }

    out_frame->plane_count  = planeCount;
    out_frame->frame_id     = frame.frameId;
    out_frame->sequence     = frame.buffer->metadata().sequence;
    out_frame->timestamp_ns = static_cast<int64_t>(frame.buffer->metadata().timestamp);
    out_frame->width        = camera->width;
    out_frame->height       = camera->height;
    out_frame->pixel_format = camera->pixelFormat;
    out_frame->stride       = camera->stride;

    return ok();
}

extern "C" int32_t shinyrpi_camera_release_frame(shinyrpi_camera *camera, uint64_t frame_id)
{
    if (camera == nullptr)
        return fail(SHINYRPI_ERR_INVALID_ARGUMENT, "camera is null");

    PendingFrame frame;
    bool         requeue = false;

    {
        std::lock_guard<std::mutex> lock(camera->mutex);

        const auto it = camera->checkedOut.find(frame_id);
        if (it == camera->checkedOut.end())
            return fail(SHINYRPI_ERR_NOT_FOUND, "frame " + std::to_string(frame_id) + " is not checked out");

        frame = it->second;
        camera->checkedOut.erase(it);
        requeue = camera->running;
    }

    /* A frame released after stop has already had its request reclaimed by Camera::stop. */
    if (requeue)
        (void) camera->recycle(frame.request);

    return ok();
}

extern "C" void shinyrpi_camera_wake(shinyrpi_camera *camera)
{
    if (camera == nullptr)
        return;

    {
        std::lock_guard<std::mutex> lock(camera->mutex);
        camera->wakeRequested = true;
    }

    camera->ready.notify_all();
}

/* ------------------------------------------------------------------------------------- */
/* Controls                                                                                */
/* ------------------------------------------------------------------------------------- */

extern "C" int32_t shinyrpi_camera_set_control(shinyrpi_camera *camera, int32_t control, double value)
{
    if (camera == nullptr)
        return fail(SHINYRPI_ERR_INVALID_ARGUMENT, "camera is null");

    const char *name = controlName(control);
    if (name == nullptr)
        return fail(SHINYRPI_ERR_INVALID_ARGUMENT, "unknown control " + std::to_string(control));

    const libcamera::ControlId *id = findControl(camera->camera->controls(), name, nullptr);
    if (id == nullptr)
        return fail(SHINYRPI_ERR_NOT_FOUND, std::string("this sensor does not support ") + name);

    std::lock_guard<std::mutex> lock(camera->controlMutex);

    /*
     * FrameDurationLimits is one control carrying two values, so the two halves are held here
     * and written together. Setting only one end pins both, which is what a caller asking for
     * "exactly 30fps" means.
     */
    if (control == SHINYRPI_CTRL_FRAME_DURATION_MIN || control == SHINYRPI_CTRL_FRAME_DURATION_MAX)
    {
        if (control == SHINYRPI_CTRL_FRAME_DURATION_MIN)
            camera->frameDurationMin = static_cast<int64_t>(value);
        else
            camera->frameDurationMax = static_cast<int64_t>(value);

        const int64_t low  = camera->frameDurationMin != 0 ? camera->frameDurationMin : camera->frameDurationMax;
        const int64_t high = camera->frameDurationMax != 0 ? camera->frameDurationMax : camera->frameDurationMin;
        if (low == 0 || high == 0)
            return ok();

        const int64_t limits[2] = { low, high };
        camera->pendingControls.set(id->id(), libcamera::ControlValue(libcamera::Span<const int64_t>(limits, 2)));
        return ok();
    }

    libcamera::ControlValue controlValue;
    if (!doubleToControlValue(id->type(), value, &controlValue))
        return fail(SHINYRPI_ERR_UNSUPPORTED, std::string(name) + " has a type this shim cannot set from a double");

    camera->pendingControls.set(id->id(), controlValue);
    return ok();
}

extern "C" int32_t shinyrpi_camera_get_control_info(
    shinyrpi_camera *camera,
    int32_t          control,
    int32_t         *out_supported,
    double          *out_min,
    double          *out_max,
    double          *out_default
)
{
    if (camera == nullptr || out_supported == nullptr)
        return fail(SHINYRPI_ERR_INVALID_ARGUMENT, "camera or out_supported is null");

    *out_supported = 0;

    const char *name = controlName(control);
    if (name == nullptr)
        return fail(SHINYRPI_ERR_INVALID_ARGUMENT, "unknown control " + std::to_string(control));

    const libcamera::ControlInfo *info = nullptr;
    const libcamera::ControlId   *id   = findControl(camera->camera->controls(), name, &info);
    if (id == nullptr || info == nullptr)
        return ok();

    *out_supported = 1;

    if (out_min != nullptr)
        controlValueToDouble(info->min(), out_min);

    if (out_max != nullptr)
        controlValueToDouble(info->max(), out_max);

    if (out_default != nullptr)
        controlValueToDouble(info->def(), out_default);

    return ok();
}

/* ------------------------------------------------------------------------------------- */
/* Diagnostics                                                                             */
/* ------------------------------------------------------------------------------------- */

extern "C" int32_t shinyrpi_camera_get_stats(shinyrpi_camera *camera, shinyrpi_stats *out_stats)
{
    if (camera == nullptr)
        return fail(SHINYRPI_ERR_INVALID_ARGUMENT, "camera is null");

    if (!layoutOk(out_stats))
        return layoutError("shinyrpi_stats", out_stats);

    std::lock_guard<std::mutex> lock(camera->mutex);

    out_stats->queue_length     = static_cast<uint32_t>(camera->completed.size());
    out_stats->checked_out      = static_cast<uint32_t>(camera->checkedOut.size());
    out_stats->reserved0        = 0;
    out_stats->frames_completed = camera->statCompleted;
    out_stats->frames_delivered = camera->statDelivered;
    out_stats->frames_dropped   = camera->statDropped;
    out_stats->frames_errored   = camera->statErrored;

    return ok();
}
