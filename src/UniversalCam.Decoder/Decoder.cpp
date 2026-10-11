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
    std::vector<uint8_t> bgra;
    UINT32 width = 0, height = 0;
    LONG stride = 0;
    UINT32 matrix = MFVideoTransferMatrix_BT709;
    UINT32 range = MFNominalRange_16_235;
    bool mfStarted = false;
    bool discontinuity = true;
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
            value.vt = VT_BOOL; value.boolVal = VARIANT_TRUE;
            codec->SetValue(&CODECAPI_AVLowLatencyMode, &value);
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
            matrix = MFGetAttributeUINT32(candidate.Get(), MF_MT_YUV_MATRIX,
                width >= 1280 ? MFVideoTransferMatrix_BT709 : MFVideoTransferMatrix_BT601);
            range = MFGetAttributeUINT32(candidate.Get(), MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
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
        RETURN_IF_FAILED(sample->SetSampleTime(timestamp * 10));
        RETURN_IF_FAILED(sample->SetSampleDuration(10'000'000 / 30));
        if (keyframe) sample->SetUINT32(MFSampleExtension_CleanPoint, TRUE);
        if (discontinuity) sample->SetUINT32(MFSampleExtension_Discontinuity, TRUE);
        const HRESULT submitted = transform->ProcessInput(0, sample.Get(), 0);
        if (SUCCEEDED(submitted)) discontinuity = false;
        return submitted;
    }

    static uint8_t Clamp(int value) { return static_cast<uint8_t>(std::clamp(value, 0, 255)); }

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
        try { bgra.resize(static_cast<size_t>(width) * height * 4); }
        catch (...) { buffer->Unlock(); return E_OUTOFMEMORY; }
        const bool bt709 = matrix == MFVideoTransferMatrix_BT709;
        const bool fullRange = range == MFNominalRange_0_255;
        for (UINT32 row = 0; row < height; ++row) {
            const auto yRow = pixels + static_cast<size_t>(row) * stride;
            const auto uvRow = pixels + ySize + static_cast<size_t>(row / 2) * stride;
            auto output = bgra.data() + static_cast<size_t>(row) * width * 4;
            for (UINT32 column = 0; column < width; ++column) {
                const int u = uvRow[column & ~1u] - 128;
                const int v = uvRow[(column & ~1u) + 1] - 128;
                const int y = fullRange ? 256 * yRow[column] + 128 : 298 * (yRow[column] - 16) + 128;
                const int red = y + (fullRange ? (bt709 ? 403 : 359) : (bt709 ? 459 : 409)) * v;
                const int green = y - (fullRange ? (bt709 ? 48 : 88) : (bt709 ? 55 : 100)) * u
                    - (fullRange ? (bt709 ? 120 : 183) : (bt709 ? 136 : 208)) * v;
                const int blue = y + (fullRange ? (bt709 ? 475 : 454) : (bt709 ? 541 : 516)) * u;
                output[column * 4] = Clamp(blue >> 8);
                output[column * 4 + 1] = Clamp(green >> 8);
                output[column * 4 + 2] = Clamp(red >> 8);
                output[column * 4 + 3] = 255;
            }
        }
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
            *outputWidth = static_cast<int>(width);
            *outputHeight = static_cast<int>(height);
            *pixels = bgra.data();
            *length = static_cast<int>(bgra.size());
            return S_OK;
        }
        return MF_E_INVALIDMEDIATYPE;
    }

    HRESULT Reset() {
        discontinuity = true;
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
