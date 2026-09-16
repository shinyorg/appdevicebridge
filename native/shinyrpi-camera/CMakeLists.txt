cmake_minimum_required(VERSION 3.16)

project(shinyrpi_camera VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

find_package(PkgConfig REQUIRED)

#
# The version floor is the load-bearing part of this file.
#
# libcamera has no stable ABI and does not promise a stable API either. Rather than let a
# mismatch surface as a subtly wrong frame at runtime on a device with no screen, the build
# refuses anything older than the release whose API this shim was written against.
#
# 0.1.0 is the floor because that is where ControlList::get(Control<T>) changed to return
# std::optional<T>. The shim sidesteps that call entirely, but 0.1 is also where the rest of
# the surface we use settled, and nothing older has shipped on a supported Raspberry Pi OS
# for years.
#
# Raising this floor is a deliberate act. See README.md.
#
set(SHINYRPI_LIBCAMERA_MINIMUM 0.1.0)

pkg_check_modules(LIBCAMERA REQUIRED IMPORTED_TARGET libcamera>=${SHINYRPI_LIBCAMERA_MINIMUM})

message(STATUS "Building against libcamera ${LIBCAMERA_VERSION} (minimum ${SHINYRPI_LIBCAMERA_MINIMUM})")

#
# Compile-time probes. Each one turns "the shim built fine and then behaved oddly" into a
# build failure with a name attached, which is the only affordable failure mode for a headless
# appliance.
#
include(CheckCXXSourceCompiles)

set(CMAKE_REQUIRED_INCLUDES ${LIBCAMERA_INCLUDE_DIRS})
set(CMAKE_REQUIRED_LIBRARIES PkgConfig::LIBCAMERA)
set(CMAKE_REQUIRED_FLAGS "-std=c++17")

# FrameBuffer::Plane::offset. Without it, planes of a multi-planar buffer cannot be located
# inside a single mapping and every YUV frame would silently come out as garbage.
check_cxx_source_compiles("
    #include <libcamera/framebuffer.h>
    int main() { libcamera::FrameBuffer::Plane plane; return static_cast<int>(plane.offset); }
" SHINYRPI_HAVE_PLANE_OFFSET)

if(NOT SHINYRPI_HAVE_PLANE_OFFSET)
    message(FATAL_ERROR
        "This libcamera's FrameBuffer::Plane has no offset field. The shim cannot locate "
        "planes within a mapped buffer without it. libcamera ${LIBCAMERA_VERSION} is too old.")
endif()

# The reference-returning ControlList::get(unsigned int). This is the accessor the shim uses
# everywhere precisely because the templated one changed shape; if it ever goes away, this
# build stops rather than silently picking a different overload.
check_cxx_source_compiles("
    #include <libcamera/controls.h>
    int main() {
        libcamera::ControlList list;
        const libcamera::ControlValue &value = list.get(1u);
        return value.isNone() ? 0 : 1;
    }
" SHINYRPI_HAVE_CONTROLLIST_GET_BY_ID)

if(NOT SHINYRPI_HAVE_CONTROLLIST_GET_BY_ID)
    message(FATAL_ERROR
        "ControlList::get(unsigned int) is gone from libcamera ${LIBCAMERA_VERSION}. "
        "src/shinyrpi_camera.cpp needs updating - see README.md, 'When libcamera moves'.")
endif()

unset(CMAKE_REQUIRED_INCLUDES)
unset(CMAKE_REQUIRED_LIBRARIES)
unset(CMAKE_REQUIRED_FLAGS)

add_library(shinyrpi_camera SHARED src/shinyrpi_camera.cpp)

target_include_directories(shinyrpi_camera PUBLIC include)
target_link_libraries(shinyrpi_camera PRIVATE PkgConfig::LIBCAMERA)

# Reported back through shinyrpi_camera_libcamera_version(). The managed side logs it on
# startup, so a support question can be answered without shelling into the device.
target_compile_definitions(shinyrpi_camera PRIVATE
    SHINYRPI_LIBCAMERA_VERSION="${LIBCAMERA_VERSION}")

target_compile_options(shinyrpi_camera PRIVATE
    -Wall -Wextra -Wpedantic -fvisibility=hidden)

#
# Only the eighteen functions marked SHINYRPI_API in the header are exported; everything else,
# including every libcamera symbol pulled in here, stays local to the .so and cannot collide
# with anything else the process loads.
#
set_target_properties(shinyrpi_camera PROPERTIES
    OUTPUT_NAME shinyrpi_camera
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON)

install(TARGETS shinyrpi_camera LIBRARY DESTINATION lib)
install(FILES include/shinyrpi_camera.h DESTINATION include)
