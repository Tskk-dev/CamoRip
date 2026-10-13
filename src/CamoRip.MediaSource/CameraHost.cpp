#include "Common.h"
#include <condition_variable>
#include <future>
#include <thread>

namespace {
struct CameraHost {
    ObjectLifetime lifetime;
    std::thread thread;
    std::mutex mutex;
    std::condition_variable stopped;
    bool stop = false;
    ~CameraHost() {
        { std::lock_guard<std::mutex> lock(mutex); stop = true; }
        stopped.notify_all();
        if (thread.joinable()) thread.join();
    }
    void Run(std::wstring path, NativeFormat format, std::promise<HRESULT> ready) {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        const bool comInitialized = SUCCEEDED(hr);
        bool mfInitialized = false;
        ComPtr<IMFVirtualCamera> camera;
        if (SUCCEEDED(hr)) {
            hr = MFStartup(MF_VERSION);
            mfInitialized = SUCCEEDED(hr);
        }
        // Retire the previous display-name identity before publishing the renamed camera.
        // This runs as the same user as the companion, after its single-instance lock.
        if (SUCCEEDED(hr)) {
            ComPtr<IMFVirtualCamera> legacy;
            if (SUCCEEDED(MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource,
                MFVirtualCameraLifetime_Session, MFVirtualCameraAccess_CurrentUser,
                L"USB iPhone Camera", CamoRipClsid, nullptr, 0, &legacy))) {
                legacy->Remove(); legacy->Shutdown();
            }
        }
        if (SUCCEEDED(hr)) hr = MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource,
            MFVirtualCameraLifetime_Session, MFVirtualCameraAccess_CurrentUser,
            L"CamoRip", CamoRipClsid, nullptr, 0, &camera);
        if (SUCCEEDED(hr)) hr = camera->SetString(CAMORIP_FRAME_PATH, path.c_str());
        if (SUCCEEDED(hr)) hr = MFSetAttributeSize(camera.Get(), CAMORIP_FRAME_SIZE, format.width, format.height);
        if (SUCCEEDED(hr)) hr = camera->SetUINT32(CAMORIP_FRAME_RATE, format.fps);
        if (SUCCEEDED(hr)) hr = camera->Start(nullptr);
        ready.set_value(hr);
        if (SUCCEEDED(hr)) {
            std::unique_lock<std::mutex> lock(mutex);
            stopped.wait(lock, [this] { return stop; });
        }
        if (camera) {
            camera->Stop();
            camera->Remove();
            camera->Shutdown();
            camera.Reset();
        }
        if (mfInitialized) MFShutdown();
        if (comInitialized) CoUninitialize();
    }
};
}

extern "C" HRESULT __stdcall CamoRipStartCameraMode(LPCWSTR path, DWORD width, DWORD height, DWORD fps, void** result) {
    if (!result || !path) return E_POINTER;
    *result = nullptr;
    const NativeFormat format{width, height, fps};
    if (!format.Valid()) return E_INVALIDARG;
    try {
        auto host = std::make_unique<CameraHost>();
        std::promise<HRESULT> ready;
        auto started = ready.get_future();
        host->thread = std::thread(&CameraHost::Run, host.get(), std::wstring(path), format, std::move(ready));
        CHECK_HR(started.get());
        *result = host.release();
        return S_OK;
    } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
      catch (...) { return E_FAIL; }
}

extern "C" HRESULT __stdcall CamoRipStartCamera(LPCWSTR path, void** result) {
    return CamoRipStartCameraMode(path, FrameWidth, FrameHeight, 60, result);
}

extern "C" void __stdcall CamoRipStopCamera(void* context) { delete static_cast<CameraHost*>(context); }

// The installer can call this before unregistering the COM DLL. The app should be closed first.
extern "C" HRESULT __stdcall CamoRipRemoveCamera() {
    HRESULT result = E_FAIL;
    std::thread cleanup([&result] {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(hr)) { result = hr; return; }
        hr = MFStartup(MF_VERSION);
        if (SUCCEEDED(hr)) {
            ComPtr<IMFVirtualCamera> camera;
            hr = MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource,
                MFVirtualCameraLifetime_Session, MFVirtualCameraAccess_CurrentUser,
                L"CamoRip", CamoRipClsid, nullptr, 0, &camera);
            if (SUCCEEDED(hr)) { hr = camera->Remove(); camera->Shutdown(); camera.Reset(); }
            MFShutdown();
        }
        CoUninitialize(); result = hr;
    });
    cleanup.join();
    return result;
}
