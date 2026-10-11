#pragma once
#include <windows.h>
#include <wrl.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mfvirtualcamera.h>
#include <ks.h>
#include <ksmedia.h>
#include <ksproxy.h>
#include <atomic>
#include <mutex>
#include <string>
#include <vector>
#include <memory>

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::RuntimeClass;
using Microsoft::WRL::RuntimeClassFlags;
using Microsoft::WRL::ClassicCom;
using Microsoft::WRL::FtmBase;
using Microsoft::WRL::ChainInterfaces;
using Microsoft::WRL::Make;

#define CHECK_HR(expression) do { const HRESULT checkResult = (expression); if (FAILED(checkResult)) return checkResult; } while (false)
inline constexpr GUID CLSID_UniversalCam = {0x760f1341,0x0182,0x4e3b,{0x88,0x10,0xa4,0x46,0xeb,0x25,0x07,0x0c}};
inline constexpr wchar_t UniversalCamClsid[] = L"{760F1341-0182-4E3B-8810-A446EB25070C}";
inline constexpr GUID UCAM_FRAME_PATH = {0xab79b379,0xcfad,0x46e1,{0x97,0x61,0x7a,0x53,0x23,0x92,0xc0,0x55}};
inline constexpr DWORD FrameWidth = 1920;
inline constexpr DWORD FrameHeight = 1080;
inline constexpr DWORD FrameBytes = FrameWidth * FrameHeight * 3 / 2;
inline constexpr LONGLONG FrameDuration = 10'000'000 / 30;
extern std::atomic<long> g_objects;
extern std::atomic<long> g_locks;
struct ObjectLifetime {
    ObjectLifetime() { ++g_objects; }
    ~ObjectLifetime() { --g_objects; }
};

