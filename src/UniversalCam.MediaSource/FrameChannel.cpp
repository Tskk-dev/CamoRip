#include "FrameChannel.h"
#include <sddl.h>
#include <algorithm>
#include <cstring>

namespace {
struct Writer {
    ObjectLifetime lifetime;
    HANDLE file = INVALID_HANDLE_VALUE;
    HANDLE mapping = nullptr;
    ChannelHeader* header = nullptr;
    std::mutex mutex;
    ~Writer() {
        if (header) {
            InterlockedIncrement64(&header->sequence);
            header->updatedTick = 0;
            SecureZeroMemory(reinterpret_cast<BYTE*>(header) + sizeof(ChannelHeader), 2ull * FrameBytes);
            InterlockedIncrement64(&header->sequence);
            UnmapViewOfFile(header);
        }
        if (mapping) CloseHandle(mapping);
        if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    }
};

HRESULT SecurityForCurrentUser(PSECURITY_DESCRIPTOR* descriptor) {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return HRESULT_FROM_WIN32(GetLastError());
    DWORD bytes = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &bytes);
    std::vector<BYTE> info(bytes);
    if (!GetTokenInformation(token, TokenUser, info.data(), bytes, &bytes)) {
        const auto error = GetLastError(); CloseHandle(token); return HRESULT_FROM_WIN32(error);
    }
    CloseHandle(token);
    LPWSTR sid = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(info.data())->User.Sid, &sid)) return HRESULT_FROM_WIN32(GetLastError());
    // Only this user can write. LocalService hosts Windows Camera Frame Server.
    const std::wstring sddl = L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;" + std::wstring(sid) + L")(A;OICI;GRGX;;;LS)";
    LocalFree(sid);
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, descriptor, nullptr)) return HRESULT_FROM_WIN32(GetLastError());
    return S_OK;
}
}

extern "C" HRESULT __stdcall UCamOpenWriter(LPCWSTR path, void** result) {
    if (!path || !result) return E_POINTER;
    *result = nullptr;
    try {
        const std::wstring filePath(path);
        const auto separator = filePath.find_last_of(L"\\/");
        if (separator == std::wstring::npos || filePath.size() > 32700) return E_INVALIDARG;
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        CHECK_HR(SecurityForCurrentUser(&descriptor));
        SECURITY_ATTRIBUTES attributes{sizeof(attributes), descriptor, FALSE};
        const auto directory = filePath.substr(0, separator);
        if (!CreateDirectoryW(directory.c_str(), &attributes) && GetLastError() != ERROR_ALREADY_EXISTS) {
            const auto error = GetLastError(); LocalFree(descriptor); return HRESULT_FROM_WIN32(error);
        }
        auto writer = std::make_unique<Writer>();
        // Exclusive write sharing prevents a second companion from publishing here.
        writer->file = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_DELETE,
            &attributes, CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
        LocalFree(descriptor);
        if (writer->file == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
        LARGE_INTEGER size{}; size.QuadPart = ChannelBytes;
        if (!SetFilePointerEx(writer->file, size, nullptr, FILE_BEGIN) || !SetEndOfFile(writer->file)) return HRESULT_FROM_WIN32(GetLastError());
        writer->mapping = CreateFileMappingW(writer->file, nullptr, PAGE_READWRITE, 0, static_cast<DWORD>(ChannelBytes), nullptr);
        if (!writer->mapping) return HRESULT_FROM_WIN32(GetLastError());
        writer->header = static_cast<ChannelHeader*>(MapViewOfFile(writer->mapping, FILE_MAP_WRITE, 0, 0, ChannelBytes));
        if (!writer->header) return HRESULT_FROM_WIN32(GetLastError());
        ZeroMemory(writer->header, ChannelBytes);
        writer->header->magic = ChannelMagic;
        writer->header->version = 1;
        writer->header->width = FrameWidth;
        writer->header->height = FrameHeight;
        writer->header->bytes = FrameBytes;
        *result = writer.release();
        return S_OK;
    } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
      catch (...) { return E_FAIL; }
}

extern "C" HRESULT __stdcall UCamWriteFrame(void* context, const BYTE* nv12, DWORD bytes, LONGLONG ptsUs) {
    if (!context || !nv12) return E_POINTER;
    if (bytes != FrameBytes) return E_INVALIDARG;
    auto* writer = static_cast<Writer*>(context);
    std::lock_guard<std::mutex> lock(writer->mutex);
    auto* header = writer->header;
    const DWORD slot = 1 - header->slot;
    InterlockedIncrement64(&header->sequence); // odd = being written
    std::memcpy(reinterpret_cast<BYTE*>(header) + sizeof(ChannelHeader) + slot * FrameBytes, nv12, bytes);
    header->slot = slot;
    header->ptsUs = ptsUs;
    InterlockedExchange64(&header->updatedTick, static_cast<LONG64>(GetTickCount64()));
    InterlockedIncrement64(&header->sequence); // publish with a full memory barrier
    return S_OK;
}

extern "C" void __stdcall UCamCloseWriter(void* context) { delete static_cast<Writer*>(context); }

FrameReader::~FrameReader() { CloseMapping(); }

void FrameReader::CloseMapping() {
    if (header_) UnmapViewOfFile(header_);
    if (mapping_) CloseHandle(mapping_);
    if (file_ != INVALID_HANDLE_VALUE) CloseHandle(file_);
    header_ = nullptr;
    mapping_ = nullptr;
    file_ = INVALID_HANDLE_VALUE;
    cachedTick_ = 0;
    cachedSequence_ = -1;
}

bool FrameReader::Open(ULONGLONG now) {
    if (path_.empty() || (lastOpenAttempt_ && now - lastOpenAttempt_ < 1000)) return false;
    lastOpenAttempt_ = now;
    file_ = CreateFileW(path_.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file_ == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file_, &size) || size.QuadPart != ChannelBytes) { CloseHandle(file_); file_ = INVALID_HANDLE_VALUE; return false; }
    mapping_ = CreateFileMappingW(file_, nullptr, PAGE_READONLY, 0, 0, nullptr);
    if (mapping_) header_ = static_cast<const ChannelHeader*>(MapViewOfFile(mapping_, FILE_MAP_READ, 0, 0, ChannelBytes));
    if (!header_) {
        if (mapping_) CloseHandle(mapping_);
        mapping_ = nullptr; CloseHandle(file_); file_ = INVALID_HANDLE_VALUE;
        return false;
    }
    return true;
}

bool FrameReader::Repeat(BYTE* destination, ULONGLONG now) {
    if (!cachedTick_ || (now >= cachedTick_ && now - cachedTick_ > FrameHoldMs)) return false;
    std::memcpy(destination, cached_.data(), FrameBytes);
    return true;
}

bool FrameReader::Read(BYTE* destination, ULONGLONG now) {
    if (!destination || (!header_ && !Open(now))) return false;
    if (header_->magic != ChannelMagic || header_->version != 1 || header_->width != FrameWidth || header_->height != FrameHeight || header_->bytes != FrameBytes) return false;
    // A closed writer invalidates the cache immediately. Release this mapping
    // so a restarted companion can create a new file at the same path.
    const auto publishedTick = static_cast<ULONGLONG>(header_->updatedTick);
    if (!publishedTick) { CloseMapping(); return false; }
    if (now >= publishedTick && now - publishedTick > FrameHoldMs) { cachedTick_ = 0; return false; }
    try { if (cached_.empty()) cached_.resize(FrameBytes); }
    catch (const std::bad_alloc&) { return false; }
    for (int retry = 0; retry != 3; ++retry) {
        // Aligned volatile reads are atomic on x64. Interlocked would require a writable view.
        const auto before = header_->sequence;
        MemoryBarrier();
        if (before & 1) return Repeat(destination, now);
        const auto tick = header_->updatedTick;
        const DWORD slot = header_->slot;
        if (!tick) { CloseMapping(); return false; }
        if (slot > 1 || (now >= static_cast<ULONGLONG>(tick) && now - static_cast<ULONGLONG>(tick) > FrameHoldMs)) { cachedTick_ = 0; return false; }
        if (before == cachedSequence_) return Repeat(destination, now);
        std::memcpy(destination, reinterpret_cast<const BYTE*>(header_) + sizeof(ChannelHeader) + slot * FrameBytes, FrameBytes);
        MemoryBarrier();
        if (before == header_->sequence) {
            std::memcpy(cached_.data(), destination, FrameBytes);
            cachedTick_ = static_cast<ULONGLONG>(tick);
            cachedSequence_ = before;
            return true;
        }
    }
    // The destination may contain a torn copy after a collision. Restore only
    // a previously verified complete frame, never return the partial image.
    return Repeat(destination, now);
}

