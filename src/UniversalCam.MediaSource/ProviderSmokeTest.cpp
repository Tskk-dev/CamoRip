#include "Common.h"
#include <iostream>
#include <propvarutil.h>

using GetClassObject = HRESULT (__stdcall*)(REFCLSID, REFIID, void**);
using OpenWriter = HRESULT (__stdcall*)(LPCWSTR, void**);
using WriteFrame = HRESULT (__stdcall*)(void*, const BYTE*, DWORD, LONGLONG);
using CloseWriter = void (__stdcall*)(void*);

// Exercises the actual DLL through its COM factory without registering a camera
// or changing registry state. Runs on the Windows runner with desktop MF installed.
HRESULT Verify(HMODULE module) {
    auto getFactory = reinterpret_cast<GetClassObject>(GetProcAddress(module, "DllGetClassObject"));
    auto openWriter = reinterpret_cast<OpenWriter>(GetProcAddress(module, "UCamOpenWriter"));
    auto writeFrame = reinterpret_cast<WriteFrame>(GetProcAddress(module, "UCamWriteFrame"));
    auto closeWriter = reinterpret_cast<CloseWriter>(GetProcAddress(module, "UCamCloseWriter"));
    if (!getFactory || !openWriter || !writeFrame || !closeWriter) return E_NOINTERFACE;
    WCHAR temporary[MAX_PATH];
    if (!GetTempPathW(ARRAYSIZE(temporary), temporary)) return HRESULT_FROM_WIN32(GetLastError());
    std::wstring path = std::wstring(temporary) + L"UCamProviderSmoke-" + std::to_wstring(GetCurrentProcessId()) + L"\\stream.bin";
    struct WriterGuard { void* value = nullptr; CloseWriter close; ~WriterGuard() { if (value) close(value); } } writer{nullptr, closeWriter};
    CHECK_HR(openWriter(path.c_str(), &writer.value));
    std::vector<BYTE> pixels(FrameBytes, 128);
    CHECK_HR(writeFrame(writer.value, pixels.data(), FrameBytes, 12345));
    ComPtr<IClassFactory> factory;
    CHECK_HR(getFactory(CLSID_UniversalCam, IID_PPV_ARGS(&factory)));
    ComPtr<IMFActivate> activation;
    CHECK_HR(factory->CreateInstance(nullptr, IID_PPV_ARGS(&activation)));
    CHECK_HR(activation->SetString(UCAM_FRAME_PATH, path.c_str()));
    ComPtr<IMFMediaSource> closedSource;
    CHECK_HR(activation->ActivateObject(IID_PPV_ARGS(&closedSource)));
    CHECK_HR(activation->ShutdownObject());
    if (closedSource->Stop() != MF_E_SHUTDOWN) return E_UNEXPECTED;
    closedSource.Reset();
    ComPtr<IMFMediaSource> source;
    CHECK_HR(activation->ActivateObject(IID_PPV_ARGS(&source)));
    // Releasing the helper must not invalidate the returned source's lifetime.
    activation.Reset();
    struct SourceGuard { IMFMediaSource* source; ~SourceGuard() { source->Shutdown(); } } sourceGuard{source.Get()};
    ComPtr<IMFPresentationDescriptor> presentation;
    CHECK_HR(source->CreatePresentationDescriptor(&presentation));
    DWORD count = 0; CHECK_HR(presentation->GetStreamDescriptorCount(&count));
    if (count != 1) return E_UNEXPECTED;
    BOOL selected = FALSE; ComPtr<IMFStreamDescriptor> descriptor;
    CHECK_HR(presentation->GetStreamDescriptorByIndex(0, &selected, &descriptor));
    ComPtr<IMFMediaTypeHandler> handler; ComPtr<IMFMediaType> type;
    CHECK_HR(descriptor->GetMediaTypeHandler(&handler));
    CHECK_HR(handler->GetCurrentMediaType(&type));
    UINT32 width, height, rate, denominator; GUID subtype;
    CHECK_HR(type->GetGUID(MF_MT_SUBTYPE, &subtype));
    CHECK_HR(MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &width, &height));
    CHECK_HR(MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, &rate, &denominator));
    if (!selected || width != FrameWidth || height != FrameHeight || rate != 30 || denominator != 1 || subtype != MFVideoFormat_NV12) return MF_E_INVALIDMEDIATYPE;
    PROPVARIANT position{};
    CHECK_HR(source->Start(presentation.Get(), nullptr, &position));
    ComPtr<IMFMediaStream> stream;
    for (;;) {
        ComPtr<IMFMediaEvent> event; CHECK_HR(source->GetEvent(0, &event));
        MediaEventType eventType; CHECK_HR(event->GetType(&eventType));
        if (eventType == MENewStream) {
            PROPVARIANT value{}; CHECK_HR(event->GetValue(&value));
            const auto hr = value.vt == VT_UNKNOWN ? value.punkVal->QueryInterface(IID_PPV_ARGS(&stream)) : E_UNEXPECTED;
            PropVariantClear(&value); CHECK_HR(hr);
        }
        if (eventType == MESourceStarted) break;
    }
    if (!stream) return E_UNEXPECTED;
    LONGLONG previousTime = -1;
    for (int index = 0; index != 4; ++index) {
        ComPtr<IMFAttributes> token; CHECK_HR(MFCreateAttributes(&token, 1));
        CHECK_HR(token->SetUINT32(UCAM_FRAME_PATH, index));
        CHECK_HR(stream->RequestSample(token.Get()));
        for (;;) {
            ComPtr<IMFMediaEvent> event; CHECK_HR(stream->GetEvent(0, &event));
            MediaEventType eventType; CHECK_HR(event->GetType(&eventType));
            HRESULT status = S_OK; CHECK_HR(event->GetStatus(&status)); CHECK_HR(status);
            if (eventType != MEMediaSample) continue;
            PROPVARIANT value{}; CHECK_HR(event->GetValue(&value));
            ComPtr<IMFSample> sample;
            auto hr = value.vt == VT_UNKNOWN ? value.punkVal->QueryInterface(IID_PPV_ARGS(&sample)) : E_UNEXPECTED;
            PropVariantClear(&value); CHECK_HR(hr);
            ComPtr<IMFAttributes> returnedToken;
            CHECK_HR(sample->GetUnknown(MFSampleExtension_Token, IID_PPV_ARGS(&returnedToken)));
            UINT32 tokenIndex = 100; CHECK_HR(returnedToken->GetUINT32(UCAM_FRAME_PATH, &tokenIndex));
            LONGLONG time, duration; CHECK_HR(sample->GetSampleTime(&time)); CHECK_HR(sample->GetSampleDuration(&duration));
            DWORD length = 0; CHECK_HR(sample->GetTotalLength(&length));
            if (tokenIndex != static_cast<UINT32>(index) || time <= previousTime || duration != FrameDuration || length != FrameBytes) return E_UNEXPECTED;
            previousTime = time;
            ComPtr<IMFMediaBuffer> buffer; CHECK_HR(sample->ConvertToContiguousBuffer(&buffer));
            BYTE* data = nullptr; CHECK_HR(buffer->Lock(&data, nullptr, nullptr));
            const bool matches = data[0] == 128;
            CHECK_HR(buffer->Unlock());
            if (!matches) return E_FAIL;
            break;
        }
    }
    CHECK_HR(source->Stop());
    CHECK_HR(source->Start(presentation.Get(), nullptr, &position));
    CHECK_HR(source->Stop());
    CHECK_HR(source->Shutdown());
    if (source->Stop() != MF_E_SHUTDOWN) return E_UNEXPECTED;
    closeWriter(writer.value); writer.value = nullptr;
    return S_OK;
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 2;
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) return 3;
    hr = MFStartup(MF_VERSION);
    if (SUCCEEDED(hr)) {
        HMODULE module = LoadLibraryW(argv[1]);
        hr = module ? Verify(module) : HRESULT_FROM_WIN32(GetLastError());
        if (module) FreeLibrary(module);
        MFShutdown();
    }
    CoUninitialize();
    if (FAILED(hr)) { std::cerr << "Provider smoke test failed: 0x" << std::hex << static_cast<unsigned long>(hr) << '\n'; return 1; }
    std::cout << "Provider source: NV12 1080p30, shared frame, tokens, timestamps, restart, shutdown passed.\n";
    return 0;
}

