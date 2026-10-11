// Media Foundation lifecycle and stream attributes follow Microsoft's MIT-licensed
// Windows-Camera VirtualCamera sample. See MICROSOFT-LICENSE.txt and README.md.
#include <initguid.h>
#include "Common.h"
#include "FrameChannel.h"
#include <condition_variable>
#include <thread>
#include <deque>
#include <chrono>
#include <cstring>
#include <algorithm>
#include <propvarutil.h>

std::atomic<long> g_objects{0};
std::atomic<long> g_locks{0};
static HMODULE g_module = nullptr;

namespace {

HRESULT CreateVideoType(IMFMediaType** result) {
    ComPtr<IMFMediaType> type;
    CHECK_HR(MFCreateMediaType(&type));
    CHECK_HR(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
    CHECK_HR(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
    CHECK_HR(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
    CHECK_HR(type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
    CHECK_HR(type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE));
    CHECK_HR(type->SetUINT32(MF_MT_SAMPLE_SIZE, FrameBytes));
    CHECK_HR(type->SetUINT32(MF_MT_DEFAULT_STRIDE, FrameWidth));
    CHECK_HR(type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235));
    CHECK_HR(type->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709));
    CHECK_HR(type->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709));
    CHECK_HR(type->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709));
    CHECK_HR(MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, FrameWidth, FrameHeight));
    CHECK_HR(MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, 60, 1));
    CHECK_HR(MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
    return type.CopyTo(result);
}

HRESULT SetStreamAttributes(IMFAttributes* attributes) {
    CHECK_HR(attributes->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE));
    CHECK_HR(attributes->SetUINT32(MF_DEVICESTREAM_STREAM_ID, 0));
    CHECK_HR(attributes->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1));
    CHECK_HR(attributes->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color));
    return S_OK;
}

// GetEvent must never hold a state lock while blocking. A local queue reference
// remains valid if Shutdown races with a pending event request.
#define EVENT_METHODS() \
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** result) override { \
        ComPtr<IMFMediaEventQueue> queue; \
        { std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN; queue = events_; } \
        return queue->GetEvent(flags, result); } \
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override { \
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN; return events_->BeginGetEvent(callback, state); } \
    STDMETHODIMP EndGetEvent(IMFAsyncResult* async, IMFMediaEvent** result) override { \
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN; return events_->EndGetEvent(async, result); } \
    STDMETHODIMP QueueEvent(MediaEventType type, REFGUID extended, HRESULT status, const PROPVARIANT* value) override { \
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN; return events_->QueueEventParamVar(type, extended, status, value); }

class MediaStream final : public RuntimeClass<RuntimeClassFlags<ClassicCom>,
    ChainInterfaces<IMFMediaStream2, IMFMediaStream, IMFMediaEventGenerator>, FtmBase> {
    ObjectLifetime lifetime_;
    std::mutex mutex_;
    std::condition_variable wake_;
    std::thread worker_;
    ComPtr<IMFMediaSource> parent_;
    ComPtr<IMFMediaEventQueue> events_;
    ComPtr<IMFStreamDescriptor> descriptor_;
    ComPtr<IMFAttributes> attributes_;
    ComPtr<IMFMediaType> type_;
    ComPtr<IMFVideoSampleAllocator> allocator_;
    std::deque<ComPtr<IUnknown>> requests_;
    FrameReader reader_;
    std::vector<BYTE> pixels_;
    MF_STREAM_STATE state_ = MF_STREAM_STATE_STOPPED;
    bool shutdown_ = false;
    bool firstSample_ = true;
    bool lastLive_ = false;
    unsigned generation_ = 0;

    void Placeholder() {
        // Neutral NV12 with a centered camera-shaped block, also works before any publisher exists.
        std::fill(pixels_.begin(), pixels_.begin() + FrameWidth * FrameHeight, 32);
        std::fill(pixels_.begin() + FrameWidth * FrameHeight, pixels_.end(), 128);
        for (DWORD y = 400; y < 680; ++y) {
            std::fill(pixels_.begin() + y * FrameWidth + 760, pixels_.begin() + y * FrameWidth + 1160, 52);
            for (DWORD x = 880; x < 1040; ++x) {
                const auto dx = static_cast<int>(x) - 960, dy = static_cast<int>(y) - 540;
                if (dx * dx + dy * dy < 75 * 75) pixels_[y * FrameWidth + x] = 90;
            }
        }
    }

    HRESULT FillSample(IMFSample* sample, bool& live) {
        live = reader_.Read(pixels_.data());
        if (!live) Placeholder();
        ComPtr<IMFMediaBuffer> buffer;
        CHECK_HR(sample->GetBufferByIndex(0, &buffer));
        ComPtr<IMF2DBuffer2> buffer2d;
        if (SUCCEEDED(buffer.As(&buffer2d))) {
            BYTE* scanline = nullptr; BYTE* start = nullptr; LONG pitch = 0; DWORD length = 0;
            CHECK_HR(buffer2d->Lock2DSize(MF2DBuffer_LockFlags_Write, &scanline, &pitch, &start, &length));
            HRESULT hr = S_OK;
            // NV12 uses equal positive strides for its Y and interleaved UV planes.
            if (!scanline || pitch < static_cast<LONG>(FrameWidth) ||
                length < static_cast<ULONGLONG>(pitch) * FrameHeight * 3 / 2) hr = MF_E_BUFFERTOOSMALL;
            else {
                for (DWORD y = 0; y < FrameHeight * 3 / 2; ++y)
                    std::memcpy(scanline + static_cast<size_t>(y) * pitch, pixels_.data() + y * FrameWidth, FrameWidth);
            }
            const auto unlock = buffer2d->Unlock2D();
            CHECK_HR(hr); CHECK_HR(unlock);
        } else {
            BYTE* destination = nullptr; DWORD maximum = 0;
            CHECK_HR(buffer->Lock(&destination, &maximum, nullptr));
            const auto hr = maximum >= FrameBytes ? S_OK : MF_E_BUFFERTOOSMALL;
            if (SUCCEEDED(hr)) std::memcpy(destination, pixels_.data(), FrameBytes);
            const auto unlock = buffer->Unlock();
            CHECK_HR(hr); CHECK_HR(unlock);
        }
        CHECK_HR(buffer->SetCurrentLength(FrameBytes));
        return S_OK;
    }

    void Produce() {
        const HRESULT comResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(comResult)) {
            std::lock_guard<std::mutex> lock(mutex_);
            events_->QueueEventParamVar(MEError, GUID_NULL, comResult, nullptr);
            return;
        }
        auto next = std::chrono::steady_clock::now();
        std::unique_lock<std::mutex> lock(mutex_);
        while (!shutdown_) {
            wake_.wait(lock, [this] { return shutdown_ || (state_ == MF_STREAM_STATE_RUNNING && !requests_.empty()); });
            if (shutdown_) break;
            const auto epoch = generation_;
            const auto now = std::chrono::steady_clock::now();
            if (next < now - std::chrono::milliseconds(100)) next = now;
            if (wake_.wait_until(lock, next, [this, epoch] { return shutdown_ || epoch != generation_ || state_ != MF_STREAM_STATE_RUNNING; })) continue;
            next += std::chrono::nanoseconds(1'000'000'000 / 60);
            auto token = requests_.front(); requests_.pop_front();
            ComPtr<IMFSample> sample;
            HRESULT hr = allocator_->AllocateSample(&sample);
            bool live = false;
            if (SUCCEEDED(hr)) hr = FillSample(sample.Get(), live);
            if (SUCCEEDED(hr)) hr = sample->SetSampleTime(MFGetSystemTime());
            if (SUCCEEDED(hr)) hr = sample->SetSampleDuration(FrameDuration);
            if (SUCCEEDED(hr) && token) hr = sample->SetUnknown(MFSampleExtension_Token, token.Get());
            if (SUCCEEDED(hr) && !token) sample->DeleteItem(MFSampleExtension_Token);
            if (SUCCEEDED(hr)) hr = sample->SetUINT32(MFSampleExtension_Discontinuity, firstSample_ || live != lastLive_);
            if (SUCCEEDED(hr)) {
                firstSample_ = false; lastLive_ = live;
                hr = events_->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get());
            }
            if (FAILED(hr)) events_->QueueEventParamVar(MEError, GUID_NULL, hr, nullptr);
        }
        lock.unlock();
        CoUninitialize();
    }

public:
    HRESULT Initialize(IMFMediaSource* parent, const std::wstring& path) {
        parent_ = parent; reader_.SetPath(path);
        pixels_.resize(FrameBytes);
        CHECK_HR(MFCreateEventQueue(&events_));
        CHECK_HR(MFCreateAttributes(&attributes_, 4));
        CHECK_HR(SetStreamAttributes(attributes_.Get()));
        CHECK_HR(CreateVideoType(&type_));
        IMFMediaType* types[]{type_.Get()};
        CHECK_HR(MFCreateStreamDescriptor(0, 1, types, &descriptor_));
        CHECK_HR(SetStreamAttributes(descriptor_.Get()));
        ComPtr<IMFMediaTypeHandler> handler;
        CHECK_HR(descriptor_->GetMediaTypeHandler(&handler));
        CHECK_HR(handler->SetCurrentMediaType(type_.Get()));
        worker_ = std::thread([this] { Produce(); });
        return S_OK;
    }
    ~MediaStream() { Shutdown(); }
    EVENT_METHODS()
    STDMETHODIMP GetMediaSource(IMFMediaSource** result) override {
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN;
        return parent_.CopyTo(result);
    }
    STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** result) override {
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN;
        return descriptor_.CopyTo(result);
    }
    STDMETHODIMP RequestSample(IUnknown* token) override {
        std::lock_guard<std::mutex> lock(mutex_);
        if (shutdown_) return MF_E_SHUTDOWN;
        if (state_ != MF_STREAM_STATE_RUNNING) return MF_E_INVALIDREQUEST;
        if (requests_.size() >= 4) return MF_E_NOTACCEPTING;
        requests_.emplace_back(token); wake_.notify_one(); return S_OK;
    }
    STDMETHODIMP GetStreamState(MF_STREAM_STATE* result) override {
        if (!result) return E_POINTER;
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN;
        *result = state_; return S_OK;
    }
    STDMETHODIMP SetStreamState(MF_STREAM_STATE state) override {
        if (state == MF_STREAM_STATE_RUNNING) return Start(nullptr, false);
        if (state == MF_STREAM_STATE_STOPPED) return Stop(false);
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN;
        if (state != MF_STREAM_STATE_PAUSED || state_ != MF_STREAM_STATE_RUNNING) return MF_E_INVALID_STATE_TRANSITION;
        state_ = state; ++generation_; wake_.notify_all(); return S_OK;
    }
    HRESULT Start(IMFMediaType* type, bool sendEvent = true) {
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN;
        if (type) {
            GUID subtype{}; UINT32 width = 0, height = 0, rate = 0, denominator = 0;
            CHECK_HR(type->GetGUID(MF_MT_SUBTYPE, &subtype));
            CHECK_HR(MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &width, &height));
            CHECK_HR(MFGetAttributeRatio(type, MF_MT_FRAME_RATE, &rate, &denominator));
            if (subtype != MFVideoFormat_NV12 || width != FrameWidth || height != FrameHeight ||
                denominator == 0 || static_cast<UINT64>(rate) != 60ull * denominator) return MF_E_INVALIDMEDIATYPE;
            ComPtr<IMFMediaTypeHandler> handler;
            CHECK_HR(descriptor_->GetMediaTypeHandler(&handler));
            CHECK_HR(handler->IsMediaTypeSupported(type, nullptr));
            CHECK_HR(handler->SetCurrentMediaType(type));
        }
        if (state_ != MF_STREAM_STATE_RUNNING) {
            // FrameServer supplies its CPU allocator; standalone source clients use the same MF allocator locally.
            if (!allocator_) CHECK_HR(MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&allocator_)));
            CHECK_HR(allocator_->InitializeSampleAllocator(10, type_.Get()));
        }
        state_ = MF_STREAM_STATE_RUNNING; firstSample_ = true; ++generation_;
        if (sendEvent) CHECK_HR(events_->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr));
        wake_.notify_all(); return S_OK;
    }
    HRESULT Stop(bool sendEvent = true) {
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN;
        state_ = MF_STREAM_STATE_STOPPED; requests_.clear(); ++generation_;
        if (allocator_) allocator_->UninitializeSampleAllocator();
        if (sendEvent) CHECK_HR(events_->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr));
        wake_.notify_all(); return S_OK;
    }
    HRESULT GetAttributes(IMFAttributes** result) {
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN;
        return attributes_.CopyTo(result);
    }
    HRESULT SetAllocator(IUnknown* allocator) {
        if (!allocator) return E_POINTER;
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN;
        if (state_ == MF_STREAM_STATE_RUNNING) return MF_E_INVALIDREQUEST;
        return allocator->QueryInterface(IID_PPV_ARGS(&allocator_));
    }
    void Shutdown() {
        { std::lock_guard<std::mutex> lock(mutex_);
          if (shutdown_) return;
          shutdown_ = true; requests_.clear(); parent_.Reset();
          if (events_) events_->Shutdown(); }
        wake_.notify_all();
        if (worker_.joinable()) worker_.join();
        if (allocator_) allocator_->UninitializeSampleAllocator();
    }
};

class MediaSource final : public RuntimeClass<RuntimeClassFlags<ClassicCom>,
    ChainInterfaces<IMFMediaSourceEx, IMFMediaSource, IMFMediaEventGenerator>,
    IMFGetService, IKsControl, IMFSampleAllocatorControl, FtmBase> {
    ObjectLifetime lifetime_;
    std::mutex mutex_;
    ComPtr<IMFMediaEventQueue> events_;
    ComPtr<IMFAttributes> attributes_;
    ComPtr<IMFPresentationDescriptor> descriptor_;
    ComPtr<MediaStream> stream_;
    bool shutdown_ = false;
    bool started_ = false;
    bool announced_ = false;
public:
    HRESULT Initialize(IMFAttributes* activation) {
        CHECK_HR(MFCreateEventQueue(&events_));
        CHECK_HR(MFCreateAttributes(&attributes_, 8));
        if (activation) CHECK_HR(activation->CopyAllItems(attributes_.Get()));
        ComPtr<IMFSensorProfileCollection> profiles;
        ComPtr<IMFSensorProfile> legacy;
        CHECK_HR(MFCreateSensorProfileCollection(&profiles));
        CHECK_HR(MFCreateSensorProfile(KSCAMERAPROFILE_Legacy, 0, nullptr, &legacy));
        CHECK_HR(legacy->AddProfileFilter(0, L"((RES==;FRT<=60,1;SUT==))"));
        CHECK_HR(profiles->AddProfile(legacy.Get()));
        CHECK_HR(attributes_->SetUnknown(MF_DEVICEMFT_SENSORPROFILE_COLLECTION, profiles.Get()));
        WCHAR* path = nullptr; UINT32 pathLength = 0;
        const auto pathResult = attributes_->GetAllocatedString(UCAM_FRAME_PATH, &path, &pathLength);
        std::wstring framePath = SUCCEEDED(pathResult) ? path : L"";
        CoTaskMemFree(path);
        stream_ = Make<MediaStream>();
        if (!stream_) return E_OUTOFMEMORY;
        CHECK_HR(stream_->Initialize(this, framePath));
        ComPtr<IMFStreamDescriptor> streamDescriptor;
        CHECK_HR(stream_->GetStreamDescriptor(&streamDescriptor));
        IMFStreamDescriptor* streams[]{streamDescriptor.Get()};
        CHECK_HR(MFCreatePresentationDescriptor(1, streams, &descriptor_));
        CHECK_HR(descriptor_->SelectStream(0));
        return S_OK;
    }
    ~MediaSource() { Shutdown(); }
    EVENT_METHODS()
    STDMETHODIMP GetCharacteristics(DWORD* result) override {
        if (!result) return E_POINTER;
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN;
        *result = MFMEDIASOURCE_IS_LIVE; return S_OK;
    }
    STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** result) override {
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN;
        return descriptor_->Clone(result);
    }
    STDMETHODIMP Start(IMFPresentationDescriptor* presentation, const GUID* format, const PROPVARIANT* position) override {
        if (!presentation || !position) return E_POINTER;
        if (format && *format != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;
        if (position->vt != VT_EMPTY && (position->vt != VT_I8 || position->hVal.QuadPart != 0)) return MF_E_INVALIDREQUEST;
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN;
        DWORD count = 0;
        CHECK_HR(presentation->GetStreamDescriptorCount(&count));
        if (count != 1) return E_INVALIDARG;
        BOOL selected = FALSE; ComPtr<IMFStreamDescriptor> descriptor;
        CHECK_HR(presentation->GetStreamDescriptorByIndex(0, &selected, &descriptor));
        DWORD id = 1; CHECK_HR(descriptor->GetStreamIdentifier(&id));
        if (!selected || id != 0) return E_INVALIDARG;
        ComPtr<IMFMediaTypeHandler> handler; ComPtr<IMFMediaType> type;
        CHECK_HR(descriptor->GetMediaTypeHandler(&handler));
        CHECK_HR(handler->GetCurrentMediaType(&type));
        CHECK_HR(events_->QueueEventParamUnk(announced_ ? MEUpdatedStream : MENewStream, GUID_NULL, S_OK,
            static_cast<IMFMediaStream*>(stream_.Get())));
        CHECK_HR(stream_->Start(type.Get()));
        PROPVARIANT time{}; CHECK_HR(InitPropVariantFromInt64(MFGetSystemTime(), &time));
        const auto hr = events_->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &time);
        PropVariantClear(&time); CHECK_HR(hr);
        announced_ = true; started_ = true; return S_OK;
    }
    STDMETHODIMP Stop() override {
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN;
        CHECK_HR(stream_->Stop()); started_ = false;
        return events_->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
    }
    STDMETHODIMP Pause() override {
        std::lock_guard<std::mutex> lock(mutex_);
        return shutdown_ ? MF_E_SHUTDOWN : MF_E_INVALID_STATE_TRANSITION;
    }
    STDMETHODIMP Shutdown() override {
        ComPtr<MediaStream> stream;
        { std::lock_guard<std::mutex> lock(mutex_);
          if (shutdown_) return MF_E_SHUTDOWN;
          shutdown_ = true; stream = stream_; stream_.Reset();
          if (events_) events_->Shutdown(); }
        if (stream) stream->Shutdown();
        return S_OK;
    }
    STDMETHODIMP GetSourceAttributes(IMFAttributes** result) override {
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN;
        return attributes_.CopyTo(result);
    }
    STDMETHODIMP GetStreamAttributes(DWORD id, IMFAttributes** result) override {
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN;
        if (id != 0) return MF_E_INVALIDSTREAMNUMBER;
        return stream_->GetAttributes(result);
    }
    STDMETHODIMP SetD3DManager(IUnknown*) override {
        std::lock_guard<std::mutex> lock(mutex_); return shutdown_ ? MF_E_SHUTDOWN : E_NOTIMPL;
    }
    STDMETHODIMP GetService(REFGUID, REFIID, LPVOID* result) override {
        if (!result) return E_POINTER; *result = nullptr;
        std::lock_guard<std::mutex> lock(mutex_); return shutdown_ ? MF_E_SHUTDOWN : MF_E_UNSUPPORTED_SERVICE;
    }
    STDMETHODIMP KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG* returned) override {
        if (returned) *returned = 0; return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }
    STDMETHODIMP KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG* returned) override {
        if (returned) *returned = 0; return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }
    STDMETHODIMP KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG* returned) override {
        if (returned) *returned = 0; return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }
    STDMETHODIMP SetDefaultAllocator(DWORD id, IUnknown* allocator) override {
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN;
        if (id != 0) return MF_E_INVALIDSTREAMNUMBER;
        return stream_->SetAllocator(allocator);
    }
    STDMETHODIMP GetAllocatorUsage(DWORD id, DWORD* input, MFSampleAllocatorUsage* usage) override {
        if (!input || !usage) return E_POINTER;
        std::lock_guard<std::mutex> lock(mutex_); if (shutdown_) return MF_E_SHUTDOWN;
        if (id != 0) return MF_E_INVALIDSTREAMNUMBER;
        *input = 0; *usage = MFSampleAllocatorUsage_UsesProvidedAllocator; return S_OK;
    }
};

class Activation final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, ChainInterfaces<IMFActivate, IMFAttributes>, FtmBase> {
    ObjectLifetime lifetime_;
    std::mutex mutex_;
    ComPtr<IMFAttributes> attributes_;
    ComPtr<MediaSource> source_;
public:
    HRESULT Initialize() { return MFCreateAttributes(&attributes_, 8); }
    STDMETHODIMP ActivateObject(REFIID iid, void** result) override {
        if (!result) return E_POINTER; *result = nullptr;
        std::lock_guard<std::mutex> lock(mutex_);
        if (!source_) {
            auto source = Make<MediaSource>();
            if (!source) return E_OUTOFMEMORY;
            HRESULT hr = E_FAIL;
            try { hr = source->Initialize(attributes_.Get()); }
            catch (const std::bad_alloc&) { hr = E_OUTOFMEMORY; }
            catch (...) { hr = E_FAIL; }
            if (FAILED(hr)) { source->Shutdown(); return hr; }
            source_ = source;
        }
        return source_->QueryInterface(iid, result);
    }
    STDMETHODIMP ShutdownObject() override {
        std::lock_guard<std::mutex> lock(mutex_);
        if (source_) { source_->Shutdown(); source_.Reset(); } return S_OK;
    }
    STDMETHODIMP DetachObject() override { std::lock_guard<std::mutex> lock(mutex_); source_.Reset(); return S_OK; }
    STDMETHODIMP GetItem(REFGUID key, PROPVARIANT* value) override { return attributes_->GetItem(key, value); }
    STDMETHODIMP GetItemType(REFGUID key, MF_ATTRIBUTE_TYPE* value) override { return attributes_->GetItemType(key, value); }
    STDMETHODIMP CompareItem(REFGUID key, REFPROPVARIANT value, BOOL* match) override { return attributes_->CompareItem(key, value, match); }
    STDMETHODIMP Compare(IMFAttributes* theirs, MF_ATTRIBUTES_MATCH_TYPE type, BOOL* match) override { return attributes_->Compare(theirs, type, match); }
    STDMETHODIMP GetUINT32(REFGUID key, UINT32* value) override { return attributes_->GetUINT32(key, value); }
    STDMETHODIMP GetUINT64(REFGUID key, UINT64* value) override { return attributes_->GetUINT64(key, value); }
    STDMETHODIMP GetDouble(REFGUID key, double* value) override { return attributes_->GetDouble(key, value); }
    STDMETHODIMP GetGUID(REFGUID key, GUID* value) override { return attributes_->GetGUID(key, value); }
    STDMETHODIMP GetStringLength(REFGUID key, UINT32* value) override { return attributes_->GetStringLength(key, value); }
    STDMETHODIMP GetString(REFGUID key, LPWSTR value, UINT32 size, UINT32* length) override { return attributes_->GetString(key, value, size, length); }
    STDMETHODIMP GetAllocatedString(REFGUID key, LPWSTR* value, UINT32* length) override { return attributes_->GetAllocatedString(key, value, length); }
    STDMETHODIMP GetBlobSize(REFGUID key, UINT32* value) override { return attributes_->GetBlobSize(key, value); }
    STDMETHODIMP GetBlob(REFGUID key, UINT8* value, UINT32 size, UINT32* length) override { return attributes_->GetBlob(key, value, size, length); }
    STDMETHODIMP GetAllocatedBlob(REFGUID key, UINT8** value, UINT32* length) override { return attributes_->GetAllocatedBlob(key, value, length); }
    STDMETHODIMP GetUnknown(REFGUID key, REFIID iid, LPVOID* result) override { return attributes_->GetUnknown(key, iid, result); }
    STDMETHODIMP SetItem(REFGUID key, REFPROPVARIANT value) override { return attributes_->SetItem(key, value); }
    STDMETHODIMP DeleteItem(REFGUID key) override { return attributes_->DeleteItem(key); }
    STDMETHODIMP DeleteAllItems() override { return attributes_->DeleteAllItems(); }
    STDMETHODIMP SetUINT32(REFGUID key, UINT32 value) override { return attributes_->SetUINT32(key, value); }
    STDMETHODIMP SetUINT64(REFGUID key, UINT64 value) override { return attributes_->SetUINT64(key, value); }
    STDMETHODIMP SetDouble(REFGUID key, double value) override { return attributes_->SetDouble(key, value); }
    STDMETHODIMP SetGUID(REFGUID key, REFGUID value) override { return attributes_->SetGUID(key, value); }
    STDMETHODIMP SetString(REFGUID key, LPCWSTR value) override { return attributes_->SetString(key, value); }
    STDMETHODIMP SetBlob(REFGUID key, const UINT8* value, UINT32 size) override { return attributes_->SetBlob(key, value, size); }
    STDMETHODIMP SetUnknown(REFGUID key, IUnknown* value) override { return attributes_->SetUnknown(key, value); }
    STDMETHODIMP LockStore() override { return attributes_->LockStore(); }
    STDMETHODIMP UnlockStore() override { return attributes_->UnlockStore(); }
    STDMETHODIMP GetCount(UINT32* value) override { return attributes_->GetCount(value); }
    STDMETHODIMP GetItemByIndex(UINT32 index, GUID* key, PROPVARIANT* value) override { return attributes_->GetItemByIndex(index, key, value); }
    STDMETHODIMP CopyAllItems(IMFAttributes* target) override { return attributes_->CopyAllItems(target); }
};

class Factory final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IClassFactory, FtmBase> {
    ObjectLifetime lifetime_;
public:
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** result) override {
        if (!result) return E_POINTER; *result = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        try {
            auto activation = Make<Activation>(); if (!activation) return E_OUTOFMEMORY;
            CHECK_HR(activation->Initialize()); return activation->QueryInterface(iid, result);
        } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; } catch (...) { return E_FAIL; }
    }
    STDMETHODIMP LockServer(BOOL lock) override { if (lock) ++g_locks; else --g_locks; return S_OK; }
};
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) { g_module = module; DisableThreadLibraryCalls(module); }
    return TRUE;
}
extern "C" HRESULT __stdcall DllCanUnloadNow() { return g_objects == 0 && g_locks == 0 ? S_OK : S_FALSE; }
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** result) {
    if (!result) return E_POINTER; *result = nullptr;
    if (clsid != CLSID_UniversalCam) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = Make<Factory>(); return factory ? factory->QueryInterface(iid, result) : E_OUTOFMEMORY;
}
extern "C" HRESULT __stdcall DllRegisterServer() {
    WCHAR path[32768];
    const auto length = GetModuleFileNameW(g_module, path, ARRAYSIZE(path));
    if (!length || length == ARRAYSIZE(path)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    const auto keyPath = L"Software\\Classes\\CLSID\\" + std::wstring(UniversalCamClsid) + L"\\InProcServer32";
    HKEY key = nullptr;
    auto status = RegCreateKeyExW(HKEY_LOCAL_MACHINE, keyPath.c_str(), 0, nullptr, 0, KEY_WRITE | KEY_WOW64_64KEY, nullptr, &key, nullptr);
    if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
    status = RegSetValueExW(key, nullptr, 0, REG_SZ, reinterpret_cast<BYTE*>(path), (length + 1) * sizeof(WCHAR));
    if (status == ERROR_SUCCESS) {
        const WCHAR threading[] = L"Both";
        status = RegSetValueExW(key, L"ThreadingModel", 0, REG_SZ, reinterpret_cast<const BYTE*>(threading), sizeof(threading));
    }
    RegCloseKey(key); return HRESULT_FROM_WIN32(status);
}
extern "C" HRESULT __stdcall DllUnregisterServer() {
    const auto keyPath = L"Software\\Classes\\CLSID\\" + std::wstring(UniversalCamClsid);
    const auto status = RegDeleteTreeW(HKEY_LOCAL_MACHINE, keyPath.c_str());
    return status == ERROR_FILE_NOT_FOUND ? S_OK : HRESULT_FROM_WIN32(status);
}

