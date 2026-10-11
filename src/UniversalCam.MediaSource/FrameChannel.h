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
inline constexpr DWORD ChannelMagic = 0x4D414355; // UCAM
inline constexpr size_t ChannelBytes = sizeof(ChannelHeader) + 2ull * FrameBytes;

class FrameReader {
    HANDLE file_ = INVALID_HANDLE_VALUE;
    HANDLE mapping_ = nullptr;
    const ChannelHeader* header_ = nullptr;
    std::wstring path_;
    ULONGLONG lastOpenAttempt_ = 0;
public:
    ~FrameReader();
    void SetPath(std::wstring path) { path_ = std::move(path); }
    bool Read(BYTE* destination);
private:
    bool Open();
};

