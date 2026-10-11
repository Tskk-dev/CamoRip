// Original implementation, licensed under the project's MIT license.
// Uses the H.264 decoder included with Windows. No third-party codec is bundled.
#include <windows.h>
#include <d3d11.h>
#include <d3d10.h>
#include <dxgi.h>
#include <cwchar>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mftransform.h>
#include <codecapi.h>
#include <wmcodecdsp.h>
#include <wrl/client.h>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <memory>
#include <vector>

using Microsoft::WRL::ComPtr;
#define RETURN_IF_FAILED(expression) do { const HRESULT status_ = (expression); if (FAILED(status_)) return status_; } while (0)

namespace {
struct ComScope {
    HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ~ComScope() { if (SUCCEEDED(result)) CoUninitialize(); }
    HRESULT Check() const { return result; }
};

struct Decoder {
    ComPtr<IMFTransform> transform;
    ComPtr<IMFMediaType> outputType;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IMFDXGIDeviceManager> deviceManager;
    ComPtr<ID3D11Texture2D> staging;
    wchar_t adapterName[128] = L"unavailable";
    HRESULT fallbackReason = S_OK;
    bool hardwareConfirmed = false;
    bool softwareOutput = false;
    // Keep the decoder output in NV12 all the way to the virtual camera. The
    // previous NV12 -> BGRA -> NV12 round trip was a pair of scalar 1080p
    // conversions on every frame and could reduce real-time capture to ~1 fps.
    std::vector<uint8_t> nv12;
    UINT32 width = 0, height = 0;
    UINT32 displayWidth = 0, displayHeight = 0, cropLeft = 0, cropTop = 0;
    LONG stride = 0;
    bool mfStarted = false;
    bool discontinuity = true;
    LONGLONG previousSampleTime = -1;
    CO_MTA_USAGE_COOKIE mtaCookie = nullptr;

    ~Decoder() {
        if (transform) transform->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
        ReleaseTransform();
        if (mfStarted) MFShutdown();
        if (mtaCookie) CoDecrementMTAUsage(mtaCookie);
    }

    void ReleaseTransform() {
        outputType.Reset();
        transform.Reset();
        staging.Reset();
        deviceManager.Reset();
        context.Reset();
        device.Reset();
        hardwareConfirmed = softwareOutput = false;
        width = height = displayWidth = displayHeight = cropLeft = cropTop = 0;
        stride = 0;
        discontinuity = true;
        previousSampleTime = -1;
    }

    HRESULT EnableHardware() {
        ComPtr<IMFAttributes> attributes;
        RETURN_IF_FAILED(transform->GetAttributes(&attributes));
        UINT32 aware = FALSE;
        RETURN_IF_FAILED(attributes->GetUINT32(MF_SA_D3D11_AWARE, &aware));
        if (!aware) return MF_E_UNSUPPORTED_D3D_TYPE;
        // A real graphics adapter is required. WARP would only move software
        // decoding behind a graphics API and must not be reported as hardware.
        RETURN_IF_FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context));
        ComPtr<ID3D10Multithread> multithread;
        RETURN_IF_FAILED(context.As(&multithread));
        multithread->SetMultithreadProtected(TRUE);
        ComPtr<IDXGIDevice> dxgiDevice;
        ComPtr<IDXGIAdapter> adapter;
        DXGI_ADAPTER_DESC description{};
        if (SUCCEEDED(device.As(&dxgiDevice)) && SUCCEEDED(dxgiDevice->GetAdapter(&adapter)) &&
            SUCCEEDED(adapter->GetDesc(&description)))
            wcsncpy_s(adapterName, description.Description, _TRUNCATE);
        UINT token = 0;
        RETURN_IF_FAILED(MFCreateDXGIDeviceManager(&token, &deviceManager));
        RETURN_IF_FAILED(deviceManager->ResetDevice(device.Get(), token));
        return transform->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,
            reinterpret_cast<ULONG_PTR>(deviceManager.Get()));
    }

    HRESULT Initialize(bool preferHardware) {
        // Keep the COM MTA alive across Feed calls on different serialized pool threads.
        RETURN_IF_FAILED(CoIncrementMTAUsage(&mtaCookie));
        RETURN_IF_FAILED(MFStartup(MF_VERSION, MFSTARTUP_FULL));
        mfStarted = true;
        if (preferHardware) {
            const HRESULT hardware = ConfigureTransform(true);
            if (SUCCEEDED(hardware)) return hardware;
            fallbackReason = hardware;
            ReleaseTransform();
        }
        return ConfigureTransform(false);
    }

    HRESULT ConfigureTransform(bool useHardware) {
        RETURN_IF_FAILED(CoCreateInstance(CLSID_CMSH264DecoderMFT, nullptr, CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(&transform)));
        ComPtr<ICodecAPI> codec;
        if (SUCCEEDED(transform.As(&codec))) {
            VARIANT value; VariantInit(&value);
            // Microsoft's H.264 decoder specifically requires VT_UI4 here.
            // https://learn.microsoft.com/windows/win32/medfound/codecapi-avlowlatencymode
            value.vt = VT_UI4; value.ulVal = 1;
            RETURN_IF_FAILED(codec->SetValue(&CODECAPI_AVLowLatencyMode, &value));
            value.vt = VT_UI4; value.ulVal = 4096;
            codec->SetValue(&CODECAPI_AVDecVideoMaxCodedWidth, &value);
            value.ulVal = 2304;
            codec->SetValue(&CODECAPI_AVDecVideoMaxCodedHeight, &value);
        }
        if (useHardware) RETURN_IF_FAILED(EnableHardware());
        ComPtr<IMFMediaType> input;
        RETURN_IF_FAILED(MFCreateMediaType(&input));
        RETURN_IF_FAILED(input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
        RETURN_IF_FAILED(input->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264));
        RETURN_IF_FAILED(transform->SetInputType(0, input.Get(), 0));
        // With only major/subtype the decoder offers a placeholder output type.
        // SPS/PPS in the first access unit later trigger STREAM_CHANGE.
        RETURN_IF_FAILED(SelectOutputType());
        RETURN_IF_FAILED(transform->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0));
        return transform->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    }

    HRESULT SelectOutputType() {
        for (DWORD index = 0; ; ++index) {
            ComPtr<IMFMediaType> candidate;
            const HRESULT available = transform->GetOutputAvailableType(0, index, &candidate);
            if (available == MF_E_NO_MORE_TYPES) return MF_E_INVALIDMEDIATYPE;
            RETURN_IF_FAILED(available);
            GUID subtype{};
            if (FAILED(candidate->GetGUID(MF_MT_SUBTYPE, &subtype)) || subtype != MFVideoFormat_NV12) continue;
            RETURN_IF_FAILED(transform->SetOutputType(0, candidate.Get(), 0));
            outputType = candidate;
            staging.Reset();
            // Initial placeholder dimensions may be absent. Read requires real dimensions.
            width = height = 0;
            MFGetAttributeSize(candidate.Get(), MF_MT_FRAME_SIZE, &width, &height);
            UINT32 rawStride = 0;
            if (SUCCEEDED(candidate->GetUINT32(MF_MT_DEFAULT_STRIDE, &rawStride))) stride = static_cast<LONG>(rawStride);
            else stride = static_cast<LONG>(width);
            // H.264 is macroblock-aligned: 1080p commonly has a 1088-row
            // storage surface. Copy the valid aperture, not those padding rows.
            // Otherwise managed code unnecessarily scales every 1080p frame.
            displayWidth = width; displayHeight = height; cropLeft = cropTop = 0;
            MFVideoArea area{};
            UINT32 areaBytes = 0;
            HRESULT aperture = candidate->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE,
                reinterpret_cast<UINT8*>(&area), sizeof(area), &areaBytes);
            if (aperture == MF_E_ATTRIBUTENOTFOUND)
                aperture = candidate->GetBlob(MF_MT_GEOMETRIC_APERTURE,
                    reinterpret_cast<UINT8*>(&area), sizeof(area), &areaBytes);
            if (SUCCEEDED(aperture)) {
                if (areaBytes != sizeof(area) || area.OffsetX.fract || area.OffsetY.fract ||
                    area.OffsetX.value < 0 || area.OffsetY.value < 0 || area.Area.cx <= 0 || area.Area.cy <= 0)
                    return MF_E_INVALIDMEDIATYPE;
                cropLeft = static_cast<UINT32>(area.OffsetX.value);
                cropTop = static_cast<UINT32>(area.OffsetY.value);
                displayWidth = static_cast<UINT32>(area.Area.cx);
                displayHeight = static_cast<UINT32>(area.Area.cy);
                if ((cropLeft | cropTop | displayWidth | displayHeight) & 1 ||
                    static_cast<uint64_t>(cropLeft) + displayWidth > width ||
                    static_cast<uint64_t>(cropTop) + displayHeight > height)
                    return MF_E_INVALIDMEDIATYPE;
            } else if (aperture != MF_E_ATTRIBUTENOTFOUND) return aperture;
            return S_OK;
        }
    }

    HRESULT Submit(const uint8_t* bytes, int length, int64_t timestamp, bool keyframe) {
        if (!bytes || length <= 0 || length > 16 * 1024 * 1024 || timestamp > INT64_MAX / 10 || timestamp < 0)
            return E_INVALIDARG;
        ComPtr<IMFMediaBuffer> buffer;
        RETURN_IF_FAILED(MFCreateMemoryBuffer(static_cast<DWORD>(length), &buffer));
        BYTE* destination = nullptr;
        RETURN_IF_FAILED(buffer->Lock(&destination, nullptr, nullptr));
        memcpy(destination, bytes, static_cast<size_t>(length));
        buffer->Unlock();
        RETURN_IF_FAILED(buffer->SetCurrentLength(static_cast<DWORD>(length)));
        ComPtr<IMFSample> sample;
        RETURN_IF_FAILED(MFCreateSample(&sample));
        RETURN_IF_FAILED(sample->AddBuffer(buffer.Get()));
        const LONGLONG sampleTime = timestamp * 10;
        LONGLONG sampleDuration = 10'000'000 / 60;
        if (previousSampleTime >= 0 && sampleTime > previousSampleTime &&
            sampleTime - previousSampleTime <= 10'000'000)
            sampleDuration = sampleTime - previousSampleTime;
        RETURN_IF_FAILED(sample->SetSampleTime(sampleTime));
        RETURN_IF_FAILED(sample->SetSampleDuration(sampleDuration));
        if (keyframe) sample->SetUINT32(MFSampleExtension_CleanPoint, TRUE);
        if (discontinuity) sample->SetUINT32(MFSampleExtension_Discontinuity, TRUE);
        const HRESULT submitted = transform->ProcessInput(0, sample.Get(), 0);
        if (SUCCEEDED(submitted)) {
            discontinuity = false;
            previousSampleTime = sampleTime;
        }
        return submitted;
    }

    HRESULT CopyNv12(const BYTE* pixels, size_t rowPitch, UINT32 surfaceHeight, size_t available) {
        if (!pixels || rowPitch < width || surfaceHeight < height ||
            available < rowPitch * (static_cast<size_t>(surfaceHeight) + surfaceHeight / 2))
            return MF_E_BUFFERTOOSMALL;
        const size_t yBytes = static_cast<size_t>(displayWidth) * displayHeight;
        try { nv12.resize(yBytes + yBytes / 2); }
        catch (...) { return E_OUTOFMEMORY; }
        for (UINT32 row = 0; row < displayHeight; ++row)
            memcpy(nv12.data() + static_cast<size_t>(row) * displayWidth,
                pixels + static_cast<size_t>(row + cropTop) * rowPitch + cropLeft, displayWidth);
        // A GPU texture can be larger than MF's coded size. Its physical height,
        // not the display aperture or coded height, determines the UV-plane offset.
        const BYTE* inputUv = pixels + rowPitch * surfaceHeight;
        for (UINT32 row = 0; row < displayHeight / 2; ++row)
            memcpy(nv12.data() + yBytes + static_cast<size_t>(row) * displayWidth,
                inputUv + static_cast<size_t>(row + cropTop / 2) * rowPitch + cropLeft, displayWidth);
        return S_OK;
    }

    HRESULT CopyGpuBuffer(IMFDXGIBuffer* buffer) {
        if (!device || !context) return MF_E_UNSUPPORTED_D3D_TYPE;
        ComPtr<ID3D11Texture2D> texture;
        RETURN_IF_FAILED(buffer->GetResource(IID_PPV_ARGS(&texture)));
        UINT subresource = 0;
        RETURN_IF_FAILED(buffer->GetSubresourceIndex(&subresource));
        D3D11_TEXTURE2D_DESC description{};
        texture->GetDesc(&description);
        if (description.Format != DXGI_FORMAT_NV12 || description.Width < width || description.Height < height ||
            description.MipLevels != 1 || description.SampleDesc.Count != 1 ||
            subresource >= description.ArraySize || (description.Height & 1))
            return MF_E_INVALIDMEDIATYPE;
        if (staging) {
            D3D11_TEXTURE2D_DESC existing{};
            staging->GetDesc(&existing);
            if (existing.Width != description.Width || existing.Height != description.Height) staging.Reset();
        }
        if (!staging) {
            description.ArraySize = description.MipLevels = 1;
            description.Usage = D3D11_USAGE_STAGING;
            description.BindFlags = description.MiscFlags = 0;
            description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            RETURN_IF_FAILED(device->CreateTexture2D(&description, nullptr, &staging));
        }
        context->CopySubresourceRegion(staging.Get(), 0, 0, 0, 0, texture.Get(), subresource, nullptr);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        RETURN_IF_FAILED(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped));
        const size_t available = static_cast<size_t>(mapped.RowPitch) * (description.Height + description.Height / 2);
        const HRESULT copied = CopyNv12(static_cast<const BYTE*>(mapped.pData), mapped.RowPitch, description.Height, available);
        context->Unmap(staging.Get(), 0);
        RETURN_IF_FAILED(copied);
        hardwareConfirmed = true;
        softwareOutput = false;
        return S_OK;
    }

    HRESULT Convert(IMFSample* sample, int64_t* timestamp) {
        if (!width || !height || width > 4096 || height > 2304 || (width & 1) || (height & 1) || stride < static_cast<LONG>(width))
            return MF_E_INVALIDMEDIATYPE;
        ComPtr<IMFMediaBuffer> buffer;
        RETURN_IF_FAILED(sample->GetBufferByIndex(0, &buffer));
        ComPtr<IMFDXGIBuffer> gpuBuffer;
        if (SUCCEEDED(buffer.As(&gpuBuffer))) {
            RETURN_IF_FAILED(CopyGpuBuffer(gpuBuffer.Get()));
        } else {
            RETURN_IF_FAILED(sample->ConvertToContiguousBuffer(&buffer));
            DWORD currentLength = 0;
            BYTE* pixels = nullptr;
            RETURN_IF_FAILED(buffer->Lock(&pixels, nullptr, &currentLength));
            const HRESULT copied = CopyNv12(pixels, static_cast<size_t>(stride), height, currentLength);
            buffer->Unlock();
            RETURN_IF_FAILED(copied);
            softwareOutput = true;
            hardwareConfirmed = false;
        }
        LONGLONG sampleTime = 0;
        RETURN_IF_FAILED(sample->GetSampleTime(&sampleTime));
        *timestamp = sampleTime / 10;
        return S_OK;
    }

    HRESULT FallBackToSoftware(HRESULT reason) {
        if (!deviceManager) return S_FALSE;
        fallbackReason = reason;
        if (transform) transform->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
        ReleaseTransform();
        return ConfigureTransform(false);
    }

    HRESULT GetStatus(wchar_t* text, int capacity) const {
        if (!text || capacity < 512) return E_INVALIDARG;
        const wchar_t* mode = !deviceManager ? L"software" : hardwareConfirmed ? L"D3D11 hardware confirmed" :
            softwareOutput ? L"software output (D3D11 requested)" : L"D3D11 requested; awaiting decoded surface";
        swprintf_s(text, static_cast<size_t>(capacity), L"%ls; adapter=%ls; fallback HRESULT=0x%08X",
            mode, adapterName, static_cast<unsigned>(fallbackReason));
        return S_OK;
    }

    HRESULT Read(int* outputWidth, int* outputHeight, int64_t* timestamp, const uint8_t** pixels, int* length) {
        // Limit repeated stream-change negotiation in a malformed stream.
        for (int attempt = 0; attempt < 4; ++attempt) {
            MFT_OUTPUT_STREAM_INFO info{};
            RETURN_IF_FAILED(transform->GetOutputStreamInfo(0, &info));
            ComPtr<IMFSample> suppliedSample;
            if (!(info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES)) {
                RETURN_IF_FAILED(MFCreateSample(&suppliedSample));
                ComPtr<IMFMediaBuffer> buffer;
                RETURN_IF_FAILED(MFCreateAlignedMemoryBuffer(std::max<DWORD>(info.cbSize, 1), info.cbAlignment ? info.cbAlignment - 1 : 0, &buffer));
                RETURN_IF_FAILED(suppliedSample->AddBuffer(buffer.Get()));
            }
            MFT_OUTPUT_DATA_BUFFER output{};
            output.pSample = suppliedSample.Get();
            DWORD status = 0;
            const HRESULT produced = transform->ProcessOutput(0, 1, &output, &status);
            if (output.pEvents) output.pEvents->Release();
            // A transform-provided sample is an owned reference. A supplied sample is ours already.
            ComPtr<IMFSample> generatedSample;
            if (output.pSample && output.pSample != suppliedSample.Get()) generatedSample.Attach(output.pSample);
            if (produced == MF_E_TRANSFORM_NEED_MORE_INPUT) return S_FALSE;
            if (produced == MF_E_TRANSFORM_STREAM_CHANGE) { RETURN_IF_FAILED(SelectOutputType()); continue; }
            RETURN_IF_FAILED(produced);
            if (!output.pSample) return E_UNEXPECTED;
            RETURN_IF_FAILED(Convert(output.pSample, timestamp));
            *outputWidth = static_cast<int>(displayWidth);
            *outputHeight = static_cast<int>(displayHeight);
            *pixels = nv12.data();
            *length = static_cast<int>(nv12.size());
            return S_OK;
        }
        return MF_E_INVALIDMEDIATYPE;
    }

    HRESULT Reset() {
        discontinuity = true;
        previousSampleTime = -1;
        RETURN_IF_FAILED(transform->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0));
        return transform->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    }
};
}

extern "C" __declspec(dllexport) HRESULT __cdecl UCamDecoderCreateWithOptions(int preferHardware, void** handle) noexcept {
    if (!handle) return E_POINTER;
    *handle = nullptr;
    ComScope apartment;
    if (FAILED(apartment.Check())) return apartment.Check();
    try {
        auto decoder = std::make_unique<Decoder>();
        RETURN_IF_FAILED(decoder->Initialize(preferHardware != 0));
        *handle = decoder.release();
        return S_OK;
    } catch (...) { return E_OUTOFMEMORY; }
}

extern "C" __declspec(dllexport) HRESULT __cdecl UCamDecoderCreate(void** handle) noexcept {
    return UCamDecoderCreateWithOptions(1, handle);
}

extern "C" __declspec(dllexport) HRESULT __cdecl UCamDecoderFallBackToSoftware(void* handle, HRESULT reason) noexcept {
    if (!handle) return E_POINTER;
    ComScope apartment;
    if (FAILED(apartment.Check())) return apartment.Check();
    return static_cast<Decoder*>(handle)->FallBackToSoftware(reason);
}

extern "C" __declspec(dllexport) HRESULT __cdecl UCamDecoderGetStatus(void* handle, wchar_t* text, int capacity) noexcept {
    if (!handle) return E_POINTER;
    return static_cast<Decoder*>(handle)->GetStatus(text, capacity);
}

extern "C" __declspec(dllexport) HRESULT __cdecl UCamDecoderSubmit(void* handle, const uint8_t* bytes, int length, int64_t timestamp, int keyframe) noexcept {
    if (!handle) return E_POINTER;
    ComScope apartment;
    if (FAILED(apartment.Check())) return apartment.Check();
    return static_cast<Decoder*>(handle)->Submit(bytes, length, timestamp, keyframe != 0);
}

extern "C" __declspec(dllexport) HRESULT __cdecl UCamDecoderRead(void* handle, int* width, int* height, int64_t* timestamp, const uint8_t** pixels, int* length) noexcept {
    if (!handle || !width || !height || !timestamp || !pixels || !length) return E_POINTER;
    *pixels = nullptr; *length = 0;
    ComScope apartment;
    if (FAILED(apartment.Check())) return apartment.Check();
    return static_cast<Decoder*>(handle)->Read(width, height, timestamp, pixels, length);
}

extern "C" __declspec(dllexport) HRESULT __cdecl UCamDecoderReset(void* handle) noexcept {
    if (!handle) return E_POINTER;
    ComScope apartment;
    if (FAILED(apartment.Check())) return apartment.Check();
    return static_cast<Decoder*>(handle)->Reset();
}

extern "C" __declspec(dllexport) void __cdecl UCamDecoderDestroy(void* handle) noexcept {
    ComScope apartment;
    delete static_cast<Decoder*>(handle);
}

