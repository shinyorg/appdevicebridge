// A C surface over valhalla-mobile's iOS static library, for Shiny.AppDeviceBridge.Maps.Valhalla to P/Invoke.
//
// valhalla-mobile ships libvalhalla_all.a with a C++ entry point (std::string in and out) and wraps it in Objective-C++
// inside its Swift package. .NET can call neither, so this file declares the three functions it needs exactly as
// valhalla-mobile's main.h does — the mangled names must match — and exports them as plain C. It is compiled for the
// device and the simulator by the package's build and linked into the app with libvalhalla_all.a.
//
// Nothing may throw across this boundary: every C++ exception becomes a null actor or the error envelope valhalla-mobile
// itself answers with, { "code": -1, "message": "…" }.

#include <cstdlib>
#include <cstring>
#include <exception>
#include <string>

class ValhallaMobileHttpClient;

std::string route(const char* request, void* actor);
void* create_valhalla_actor(const char* config_path, ValhallaMobileHttpClient* http_client);
void delete_valhalla_actor(void* actor);

// Why the last shiny_valhalla_create on this thread returned null.
static thread_local std::string last_error;

static char* copy_out(const std::string& value)
{
    char* out = static_cast<char*>(std::malloc(value.size() + 1));
    if (out != nullptr)
    {
        std::memcpy(out, value.data(), value.size());
        out[value.size()] = '\0';
    }
    return out;
}

// Exported: the build hides everything else, and .NET finds these with dlsym on the app's own executable.
#define SHINY_EXPORT __attribute__((visibility("default")))

static std::string escape(const char* text)
{
    std::string out;
    for (const char* c = text; *c; ++c)
    {
        if (*c == '"' || *c == '\\')
            out += '\\';
        if (static_cast<unsigned char>(*c) >= 0x20)
            out += *c;
    }
    return out;
}

extern "C"
{

// The shim's version. Shiny.AppDeviceBridge.Maps.Valhalla refuses a shim with a different one.
SHINY_EXPORT int shiny_valhalla_abi_version(void)
{
    return 2;
}

// An actor over the tiles the config names, or null — with the reason in shiny_valhalla_last_error — when it cannot start.
SHINY_EXPORT void* shiny_valhalla_create(const char* config_path)
{
    last_error.clear();
    try
    {
        return create_valhalla_actor(config_path, nullptr);
    }
    catch (const std::exception& e)
    {
        last_error = e.what();
        return nullptr;
    }
    catch (...)
    {
        last_error = "unknown exception";
        return nullptr;
    }
}

// Why the last shiny_valhalla_create on this thread returned null. Free the result with shiny_valhalla_free.
SHINY_EXPORT char* shiny_valhalla_last_error(void)
{
    return copy_out(last_error);
}

SHINY_EXPORT void shiny_valhalla_delete(void* actor)
{
    try
    {
        if (actor != nullptr)
            delete_valhalla_actor(actor);
    }
    catch (...)
    {
    }
}

// Valhalla's route response JSON, or its error envelope. Free the result with shiny_valhalla_free. Not safe to call on
// one actor from two threads at once; the caller serializes.
SHINY_EXPORT char* shiny_valhalla_route(void* actor, const char* request)
{
    try
    {
        return copy_out(route(request, actor));
    }
    catch (const std::exception& e)
    {
        return copy_out(std::string("{\"code\":-1,\"message\":\"") + escape(e.what()) + "\"}");
    }
    catch (...)
    {
        return copy_out("{\"code\":-1,\"message\":\"unknown exception\"}");
    }
}

SHINY_EXPORT void shiny_valhalla_free(char* value)
{
    std::free(value);
}

}
