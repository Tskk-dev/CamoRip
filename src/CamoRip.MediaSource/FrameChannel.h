#pragma once
#include "Common.h"

// All counters are naturally aligned, and all changes use Interlocked operations.
// The reader opens the same temporary file in FrameServer's service session.
struct alignas(64) ChannelHeader {
    volatile LONG64 sequence;
    volatile LONG64 updatedTick;
    DWORD magic;
    DWORD version;
    DWORD width;
    DWORD height;
    DWORD bytes;
    DWORD slot;
    LONG64 ptsUs;
    BYTE reserved[16];
};
static_assert(sizeof(ChannelHeader) == 64);
inline constexpr DWORD ChannelMagic = 0x4D414355;
inline constexpr size_t ChannelBytes = sizeof(ChannelHeader) + 2ull * FrameBytes;
// Covers the phone's two-second IDR interval plus scheduling jitter. Explicit
// stop/disconnect still publishes a neutral frame immediately; close sets tick=0.
inline constexpr ULONGLONG FrameHoldMs = 2500;

class FrameReader {
    NativeFormat format_;
    HANDLE file_ = INVALID_HANDLE_VALUE;
    HANDLE mapping_ = nullptr;
    const ChannelHeader* header_ = nullptr;
    std::wstring path_;
    ULONGLONG lastOpenAttempt_ = 0;
    std::vector<BYTE> cached_;
    ULONGLONG cachedTick_ = 0;
    LONG64 cachedSequence_ = -1;
public:
    ~FrameReader();
    void SetFormat(NativeFormat format) { format_ = format; CloseMapping(); cached_.clear(); lastOpenAttempt_ = 0; }
    void SetPath(std::wstring path) { path_ = std::move(path); }
    bool Read(BYTE* destination, ULONGLONG now = GetTickCount64());
private:
    bool Open(ULONGLONG now);
    void CloseMapping();
    bool Repeat(BYTE* destination, ULONGLONG now);
    size_t MappingBytes() const { return sizeof(ChannelHeader) + 2ull * format_.Bytes(); }
};

