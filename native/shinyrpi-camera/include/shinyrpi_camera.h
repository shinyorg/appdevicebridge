/*
 * shinyrpi_camera - a flat C ABI over libcamera's C++ API.
 *
 * libcamera publishes only a C++ interface with no stable ABI, which is why no .NET binding
 * to it exists. This shim is the missing C layer: everything below is plain C types with an
 * explicit, versioned layout, so the managed side can P/Invoke it directly.
 *
 * The contract with the managed side is deliberately narrow, and every part of it is
 * defensive, because the thing on the other side of this file - libcamera - is expected to
 * break compatibility:
 *
 *   1. SHINYRPI_CAMERA_ABI_VERSION is checked by the caller before anything else. A shim
 *      built from a different revision of this header is refused rather than trusted.
 *   2. Every struct begins with struct_size. The caller fills it in; the shim rejects a value
 *      it does not recognise. A layout drift becomes a clean error instead of memory
 *      corruption.
 *   3. No libcamera type, enum value or numeric identifier crosses this boundary. Controls
 *      are addressed by a shim-local enum and resolved to libcamera controls by *name* at
 *      runtime, so libcamera renumbering its control ids cannot reach the managed side.
 *
 * See ../README.md for what actually breaks when libcamera moves, and what to do about it.
 */

#ifndef SHINYRPI_CAMERA_H
#define SHINYRPI_CAMERA_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * The library is compiled with -fvisibility=hidden, so only the functions carrying this are
 * exported. Nothing libcamera-shaped leaks into the dynamic symbol table.
 */
#if defined(__GNUC__) || defined(__clang__)
#define SHINYRPI_API __attribute__((visibility("default")))
#else
#define SHINYRPI_API
#endif

/*
 * Bumped whenever anything below changes shape or meaning. The managed binding compares this
 * against its own copy at load time and refuses to run on a mismatch.
 */
#define SHINYRPI_CAMERA_ABI_VERSION 1

/* Opaque handles. Their contents are private to the implementation. */
typedef struct shinyrpi_manager_s shinyrpi_manager;
typedef struct shinyrpi_camera_s shinyrpi_camera;

/*
 * Return codes. Zero is success and one is a non-error timeout, so callers can test for
 * failure with < 0.
 */
#define SHINYRPI_OK                     0
#define SHINYRPI_TIMEOUT                1
#define SHINYRPI_ERR_INVALID_ARGUMENT  -1
#define SHINYRPI_ERR_NOT_FOUND         -2
#define SHINYRPI_ERR_BUSY              -3
#define SHINYRPI_ERR_INVALID_STATE     -4
#define SHINYRPI_ERR_NO_MEMORY         -5
#define SHINYRPI_ERR_CONFIGURATION     -6
#define SHINYRPI_ERR_IO                -7
#define SHINYRPI_ERR_ABI               -8
#define SHINYRPI_ERR_UNSUPPORTED       -9
#define SHINYRPI_ERR_UNKNOWN          -99

/*
 * Planar formats on the Pi use at most three planes (YUV420). A compressed format such as
 * MJPEG uses one.
 */
#define SHINYRPI_MAX_PLANES 3

#define SHINYRPI_ID_MAX    256
#define SHINYRPI_MODEL_MAX 128

/* Stream roles, mapped to libcamera::StreamRole inside the shim. */
#define SHINYRPI_ROLE_VIEWFINDER 0
#define SHINYRPI_ROLE_STILL      1
#define SHINYRPI_ROLE_VIDEO      2
#define SHINYRPI_ROLE_RAW        3

/* Physical placement, mapped from libcamera::properties::Location. */
#define SHINYRPI_LOCATION_EXTERNAL 0
#define SHINYRPI_LOCATION_FRONT    1
#define SHINYRPI_LOCATION_BACK     2

/*
 * Controls, addressed by a name that belongs to this shim rather than to libcamera. The
 * implementation maps each to a libcamera control *name string* and looks that up in the
 * sensor's own control map at runtime, so a control the attached sensor does not implement
 * reports SHINYRPI_ERR_NOT_FOUND rather than misbehaving.
 */
#define SHINYRPI_CTRL_AE_ENABLE           1  /* bool   */
#define SHINYRPI_CTRL_EXPOSURE_TIME       2  /* int32, microseconds */
#define SHINYRPI_CTRL_ANALOGUE_GAIN       3  /* float  */
#define SHINYRPI_CTRL_AWB_ENABLE          4  /* bool   */
#define SHINYRPI_CTRL_AWB_MODE            5  /* int32 enum */
#define SHINYRPI_CTRL_BRIGHTNESS          6  /* float, -1.0 to 1.0 */
#define SHINYRPI_CTRL_CONTRAST            7  /* float, 0.0 upwards, 1.0 is neutral */
#define SHINYRPI_CTRL_SATURATION          8  /* float, 0.0 upwards, 1.0 is neutral */
#define SHINYRPI_CTRL_SHARPNESS           9  /* float, 0.0 upwards, 1.0 is neutral */
#define SHINYRPI_CTRL_EXPOSURE_VALUE     10  /* float, stops */
#define SHINYRPI_CTRL_FRAME_DURATION_MIN 11  /* int64, nanoseconds */
#define SHINYRPI_CTRL_FRAME_DURATION_MAX 12  /* int64, nanoseconds */
#define SHINYRPI_CTRL_AF_MODE            13  /* int32 enum: 0 manual, 1 auto, 2 continuous */
#define SHINYRPI_CTRL_LENS_POSITION      14  /* float, dioptres; 0.0 is infinity */
#define SHINYRPI_CTRL_NOISE_REDUCTION    15  /* int32 enum */

/*
 * Field order in every struct below is chosen so that the natural alignment of the target
 * ABI introduces no padding. The managed mirror declares the same fields in the same order,
 * and both sides assert on sizeof.
 */

/* One attached camera, as reported by the manager. */
typedef struct
{
    uint32_t struct_size;
    uint32_t max_width;
    uint32_t max_height;
    int32_t  rotation_degrees;
    uint32_t location;
    uint32_t reserved0;
    char     id[SHINYRPI_ID_MAX];
    char     model[SHINYRPI_MODEL_MAX];
} shinyrpi_camera_info;

/*
 * Requested stream configuration going in, the configuration libcamera actually accepted
 * coming out. libcamera is allowed to adjust anything it cannot honour exactly, so the
 * caller must read these fields back rather than assume its request survived.
 */
typedef struct
{
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    uint32_t pixel_format;  /* fourcc, e.g. 'MJPG' packed little-endian */
    uint32_t buffer_count;  /* buffers libcamera allocates for the stream */
    uint32_t role;          /* SHINYRPI_ROLE_* */
    uint32_t queue_depth;   /* completed frames the shim will hold for the reader */
    uint32_t stride;        /* out: bytes per row of the first plane */
} shinyrpi_stream_config;

/*
 * A completed frame. plane_data points directly into the mmap'd capture buffer and stays
 * valid only until shinyrpi_camera_release_frame is called for this frame_id - the buffer is
 * handed straight back to libcamera at that point and will be overwritten.
 */
typedef struct
{
    uint32_t       struct_size;
    uint32_t       plane_count;
    uint64_t       frame_id;
    uint64_t       sequence;      /* sensor frame counter */
    int64_t        timestamp_ns;  /* CLOCK_MONOTONIC, from the buffer metadata */
    const uint8_t *plane_data[SHINYRPI_MAX_PLANES];
    uint64_t       plane_length[SHINYRPI_MAX_PLANES]; /* bytes actually filled, not capacity */
    uint32_t       width;
    uint32_t       height;
    uint32_t       pixel_format;
    uint32_t       stride;
} shinyrpi_frame;

/* Counters for diagnostics. Dropped frames are the interesting one: they mean the reader
 * is not keeping up and the shim is recycling buffers to keep the sensor running. */
typedef struct
{
    uint32_t struct_size;
    uint32_t queue_length;  /* completed frames waiting to be dequeued */
    uint32_t checked_out;   /* frames handed out and not yet released */
    uint32_t reserved0;
    uint64_t frames_completed;
    uint64_t frames_delivered;
    uint64_t frames_dropped;
    uint64_t frames_errored;
} shinyrpi_stats;

/* ------------------------------------------------------------------------------------- */
/* Library                                                                                 */
/* ------------------------------------------------------------------------------------- */

/* The ABI revision this shim was built from. Check before calling anything else. */
SHINYRPI_API int32_t shinyrpi_camera_abi_version(void);

/* The libcamera release this shim was compiled and linked against, e.g. "0.5.0". */
SHINYRPI_API const char *shinyrpi_camera_libcamera_version(void);

/*
 * A human-readable description of the most recent failure on the *calling thread*. Valid
 * until the next shim call on that thread. Never NULL.
 */
SHINYRPI_API const char *shinyrpi_camera_last_error(void);

/* ------------------------------------------------------------------------------------- */
/* Manager                                                                                 */
/* ------------------------------------------------------------------------------------- */

/*
 * Starts libcamera's camera manager. libcamera permits exactly one per process, so the shim
 * keeps a single refcounted instance behind however many handles are created.
 */
SHINYRPI_API int32_t shinyrpi_camera_manager_create(shinyrpi_manager **out_manager);

SHINYRPI_API void shinyrpi_camera_manager_destroy(shinyrpi_manager *manager);

/* Number of cameras currently attached. */
SHINYRPI_API int32_t shinyrpi_camera_manager_count(shinyrpi_manager *manager, int32_t *out_count);

/* Describes the camera at index, in the order the manager enumerates them. */
SHINYRPI_API int32_t shinyrpi_camera_manager_info(shinyrpi_manager *manager, int32_t index, shinyrpi_camera_info *out_info);

/* ------------------------------------------------------------------------------------- */
/* Camera                                                                                  */
/* ------------------------------------------------------------------------------------- */

/*
 * Acquires a camera for exclusive use. Pass NULL for id to take the first one. Returns
 * SHINYRPI_ERR_BUSY when another process - typically rpicam-apps or another instance of this
 * appliance - already holds it.
 */
SHINYRPI_API int32_t shinyrpi_camera_open(shinyrpi_manager *manager, const char *id, shinyrpi_camera **out_camera);

/* Releases the camera and every buffer mapped for it. Safe to call on a running camera. */
SHINYRPI_API void shinyrpi_camera_close(shinyrpi_camera *camera);

/*
 * Configures the single capture stream. config is read for the request and overwritten with
 * what libcamera accepted. Only valid before start, or after stop.
 */
SHINYRPI_API int32_t shinyrpi_camera_configure(shinyrpi_camera *camera, shinyrpi_stream_config *config);

/* Begins capture. Requests are queued immediately and recycled as they complete. */
SHINYRPI_API int32_t shinyrpi_camera_start(shinyrpi_camera *camera);

/* Stops capture and wakes any thread blocked in dequeue. */
SHINYRPI_API int32_t shinyrpi_camera_stop(shinyrpi_camera *camera);

/*
 * Takes the oldest completed frame, blocking for up to timeout_ms. Returns SHINYRPI_TIMEOUT
 * when none arrived, SHINYRPI_ERR_INVALID_STATE once the camera has stopped.
 *
 * The frame is checked out to the caller: its buffer is withheld from libcamera until
 * shinyrpi_camera_release_frame runs. Failing to release leaks a capture buffer and will
 * eventually stall the stream.
 */
SHINYRPI_API int32_t shinyrpi_camera_dequeue_frame(shinyrpi_camera *camera, int32_t timeout_ms, shinyrpi_frame *out_frame);

/* Returns a checked-out frame's buffer to libcamera and requeues its request. */
SHINYRPI_API int32_t shinyrpi_camera_release_frame(shinyrpi_camera *camera, uint64_t frame_id);

/*
 * Unblocks a thread waiting in dequeue without stopping the camera - used to make a managed
 * cancellation token take effect immediately rather than at the next timeout.
 */
SHINYRPI_API void shinyrpi_camera_wake(shinyrpi_camera *camera);

/* ------------------------------------------------------------------------------------- */
/* Controls                                                                                */
/* ------------------------------------------------------------------------------------- */

/*
 * Sets a SHINYRPI_CTRL_* control. The value is a double regardless of the control's real
 * type; the shim coerces it to whatever the sensor declares. Applied to subsequent requests,
 * and may be called before or during capture.
 *
 * Returns SHINYRPI_ERR_NOT_FOUND when the attached sensor does not implement the control.
 */
SHINYRPI_API int32_t shinyrpi_camera_set_control(shinyrpi_camera *camera, int32_t control, double value);

/*
 * Reports whether a control is supported and, when it is, the range the sensor accepts.
 * out_supported is set to 0 or 1; the range pointers may be NULL.
 */
SHINYRPI_API int32_t shinyrpi_camera_get_control_info(
    shinyrpi_camera *camera,
    int32_t          control,
    int32_t         *out_supported,
    double          *out_min,
    double          *out_max,
    double          *out_default
);

/* ------------------------------------------------------------------------------------- */
/* Diagnostics                                                                             */
/* ------------------------------------------------------------------------------------- */

SHINYRPI_API int32_t shinyrpi_camera_get_stats(shinyrpi_camera *camera, shinyrpi_stats *out_stats);

#ifdef __cplusplus
}
#endif

#endif /* SHINYRPI_CAMERA_H */
