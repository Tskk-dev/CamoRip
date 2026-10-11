// Original implementation, licensed under the project's MIT license.
// Uses the H.264 decoder included with Windows. No third-party codec is bundled.
#include <windows.h>
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
#include "H264Geometry.h"

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
    // Keep the decoder output in NV12 all the way to the virtual camera. The
    // previous NV12 -> BGRA -> NV12 round trip was a pair of scalar 1080p
    // conversions on every frame and could reduce real-time capture to ~1 fps.
    std::vector<uint8_t> nv12;
    UINT32 width = 0, height = 0;
    UINT32 displayWidth = 0, displayHeight = 0, cropLeft = 0, cropTop = 0;
    H264::Geometry spsGeometry;
    LONG stride = 0;
    bool mfStarted = false;
    bool discontinuity = true;
    LONGLONG previousSampleTime = -1;
    CO_MTA_USAGE_COOKIE mtaCookie = nullptr;

    ~Decoder() {
        if (transform) transform->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
        transform.Reset();
        outputType.Reset();
        if (mfStarted) MFShutdown();
        if (mtaCookie) CoDecrementMTAUsage(mtaCookie);
    }

    HRESULT Initialize() {
        // Keep the COM MTA alive across Feed calls on different serialized pool threads.
        RETURN_IF_FAILED(CoIncrementMTAUsage(&mtaCookie));
        RETURN_IF_FAILED(MFStartup(MF_VERSION, MFSTARTUP_FULL));
        mfStarted = true;
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
            // The software MFT can omit aperture metadata even when the SPS
            // specifies cropping. Match the SPS to this storage surface before
            // using its crop; never apply geometry from a different resolution.
            if (spsGeometry.codedWidth == width && spsGeometry.codedHeight == height) {
                displayWidth = spsGeometry.width; displayHeight = spsGeometry.height;
                cropLeft = spsGeometry.left; cropTop = spsGeometry.top;
            }
            return S_OK;
        }
    }

    HRESULT Submit(const uint8_t* bytes, int length, int64_t timestamp, bool keyframe) {
        if (!bytes || length <= 0 || length > 16 * 1024 * 1024 || timestamp > INT64_MAX / 10 || timestamp < 0)
            return E_INVALIDARG;
        H264::Geometry geometry;
        const bool hasGeometry = keyframe && H264::FindGeometry(bytes, static_cast<size_t>(length), geometry);
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
            if (hasGeometry) {
                spsGeometry = geometry;
                if (spsGeometry.codedWidth == width && spsGeometry.codedHeight == height) {
                    displayWidth = spsGeometry.width; displayHeight = spsGeometry.height;
                    cropLeft = spsGeometry.left; cropTop = spsGeometry.top;
                }
            }
            discontinuity = false;
            previousSampleTime = sampleTime;
        }
        return submitted;
    }

    HRESULT Convert(IMFSample* sample, int64_t* timestamp) {
        if (!width || !height || width > 4096 || height > 2304 || (width & 1) || (height & 1) || stride < static_cast<LONG>(width))
            return MF_E_INVALIDMEDIATYPE;
        ComPtr<IMFMediaBuffer> buffer;
        RETURN_IF_FAILED(sample->ConvertToContiguousBuffer(&buffer));
        DWORD currentLength = 0;
        BYTE* pixels = nullptr;
        RETURN_IF_FAILED(buffer->Lock(&pixels, nullptr, &currentLength));
        // NV12 has a full-height Y plane followed by a half-height interleaved UV plane.
        const size_t ySize = static_cast<size_t>(stride) * height;
        const size_t required = ySize + static_cast<size_t>(stride) * (height / 2);
        if (currentLength < required) { buffer->Unlock(); return MF_E_BUFFERTOOSMALL; }
        const size_t yBytes = static_cast<size_t>(displayWidth) * displayHeight;
        const size_t frameBytes = yBytes + yBytes / 2;
        try { nv12.resize(frameBytes); }
        catch (...) { buffer->Unlock(); return E_OUTOFMEMORY; }
        for (UINT32 row = 0; row < displayHeight; ++row) {
            memcpy(nv12.data() + static_cast<size_t>(row) * displayWidth,
                pixels + static_cast<size_t>(row + cropTop) * stride + cropLeft, displayWidth);
        }
        const BYTE* inputUv = pixels + ySize;
        BYTE* outputUv = nv12.data() + yBytes;
        for (UINT32 row = 0; row < displayHeight / 2; ++row)
            memcpy(outputUv + static_cast<size_t>(row) * displayWidth,
                inputUv + static_cast<size_t>(row + cropTop / 2) * stride + cropLeft, displayWidth);
        buffer->Unlock();
        LONGLONG sampleTime = 0;
        RETURN_IF_FAILED(sample->GetSampleTime(&sampleTime));
        *timestamp = sampleTime / 10;
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

extern "C" __declspec(dllexport) HRESULT __cdecl UCamDecoderCreate(void** handle) noexcept {
    if (!handle) return E_POINTER;
    *handle = nullptr;
    ComScope apartment;
    if (FAILED(apartment.Check())) return apartment.Check();
    try {
        auto decoder = std::make_unique<Decoder>();
        RETURN_IF_FAILED(decoder->Initialize());
        *handle = decoder.release();
        return S_OK;
    } catch (...) { return E_OUTOFMEMORY; }
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

