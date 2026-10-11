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
    void Run(std::wstring path, std::promise<HRESULT> ready) {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        const bool comInitialized = SUCCEEDED(hr);
        bool mfInitialized = false;
        ComPtr<IMFVirtualCamera> camera;
        if (SUCCEEDED(hr)) {
            hr = MFStartup(MF_VERSION);
            mfInitialized = SUCCEEDED(hr);
        }
        if (SUCCEEDED(hr)) hr = MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource,
            MFVirtualCameraLifetime_Session, MFVirtualCameraAccess_CurrentUser,
            L"CamoRip", UniversalCamClsid, nullptr, 0, &camera);
        if (SUCCEEDED(hr)) hr = camera->SetString(UCAM_FRAME_PATH, path.c_str());
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

extern "C" HRESULT __stdcall UCamStartCamera(LPCWSTR path, void** result) {
    if (!result || !path) return E_POINTER;
    *result = nullptr;
    try {
        auto host = std::make_unique<CameraHost>();
        std::promise<HRESULT> ready;
        auto started = ready.get_future();
        host->thread = std::thread(&CameraHost::Run, host.get(), std::wstring(path), std::move(ready));
        CHECK_HR(started.get());
        *result = host.release();
        return S_OK;
    } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
      catch (...) { return E_FAIL; }
}

extern "C" void __stdcall UCamStopCamera(void* context) { delete static_cast<CameraHost*>(context); }

// The installer can call this before unregistering the COM DLL. The app should be closed first.
extern "C" HRESULT __stdcall UCamRemoveCamera() {
    HRESULT result = E_FAIL;
    std::thread cleanup([&result] {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(hr)) { result = hr; return; }
        hr = MFStartup(MF_VERSION);
        if (SUCCEEDED(hr)) {
            ComPtr<IMFVirtualCamera> camera;
            hr = MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource,
                MFVirtualCameraLifetime_Session, MFVirtualCameraAccess_CurrentUser,
                L"CamoRip", UniversalCamClsid, nullptr, 0, &camera);
            if (SUCCEEDED(hr)) { hr = camera->Remove(); camera->Shutdown(); camera.Reset(); }
            MFShutdown();
        }
        CoUninitialize(); result = hr;
    });
    cleanup.join();
    return result;
}
