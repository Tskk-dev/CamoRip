// Include the implementation to exercise writer collisions without exposing
// fault-injection hooks in the production DLL's public API.
#include "FrameChannel.cpp"
#include <iostream>
#include <stdexcept>

std::atomic<long> g_objects{0};
std::atomic<long> g_locks{0};

void Require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

int wmain() {
    void* context = nullptr;
    try {
        wchar_t temp[MAX_PATH]{};
        Require(GetTempPathW(MAX_PATH, temp) != 0, "temporary directory");
        const auto path = std::wstring(temp) + L"CamoRipFrameTest-" + std::to_wstring(GetCurrentProcessId()) + L"\\frames.bin";
        Require(SUCCEEDED(CamoRipOpenWriter(path.c_str(), &context)), "open writer");
        FrameReader reader;
        reader.SetPath(path);
        std::vector<BYTE> source(FrameBytes, 91), destination(FrameBytes);
        Require(SUCCEEDED(CamoRipWriteFrame(context, source.data(), FrameBytes, 0)), "write frame");
        auto* writer = static_cast<Writer*>(context);
        const auto start = static_cast<ULONGLONG>(writer->header->updatedTick);

        // Read's clock is captured before a concurrent publication. A newer
        // timestamp must count as fresh, not cause a spurious blank.
        Require(reader.Read(destination.data(), start - 1), "concurrently published frame");
        Require(destination == source, "complete first frame");
        InterlockedIncrement64(&writer->header->sequence);
        auto* shared = reinterpret_cast<BYTE*>(writer->header) + sizeof(ChannelHeader);
        std::memset(shared, 0, 2ull * FrameBytes);
        Require(reader.Read(destination.data(), start + 20), "repeat during write collision");
        Require(destination == source, "collision must not expose torn pixels");
        InterlockedIncrement64(&writer->header->sequence);
        Require(SUCCEEDED(CamoRipWriteFrame(context, source.data(), FrameBytes, 1)), "restore publication");
        const auto tick = static_cast<ULONGLONG>(writer->header->updatedTick);
        Require(reader.Read(destination.data(), tick + 1800), "hold across keyframe recovery");
        Require(destination == source, "recovery repeats good frame");
        Require(reader.Read(destination.data(), tick + FrameHoldMs), "hold boundary");
        Require(!reader.Read(destination.data(), tick + FrameHoldMs + 1), "expired frame must not persist indefinitely");

        std::fill(source.begin(), source.end(), 16);
        Require(SUCCEEDED(CamoRipWriteFrame(context, source.data(), FrameBytes, 2)), "explicit neutral frame");
        const auto next = static_cast<ULONGLONG>(writer->header->updatedTick);
        Require(reader.Read(destination.data(), next), "stop frame replaces cache immediately");
        Require(destination == source, "no stale live image after stop");
        CamoRipCloseWriter(context); context = nullptr;
        Require(!reader.Read(destination.data(), next + 1), "close invalidates cache immediately");

        Require(SUCCEEDED(CamoRipOpenWriter(path.c_str(), &context)), "restart writer at same path");
        Require(SUCCEEDED(CamoRipWriteFrame(context, source.data(), FrameBytes, 3)), "restart publication");
        writer = static_cast<Writer*>(context);
        const auto restarted = static_cast<ULONGLONG>(writer->header->updatedTick);
        Require(reader.Read(destination.data(), restarted + 1000), "reopen replaced mapping");
        Require(destination == source, "restarted image");
        writer->header->slot = 2;
        InterlockedAdd64(&writer->header->sequence, 2);
        Require(!reader.Read(destination.data(), restarted + 1001), "reject invalid slot");
        CamoRipCloseWriter(context); context = nullptr;
        for (const NativeFormat format : {NativeFormat{1280,720,30}, NativeFormat{3840,2160,30}, NativeFormat{1920,1080,60}}) {
            reader.SetFormat(format);
            Require(SUCCEEDED(CamoRipOpenWriterMode(path.c_str(), format.width, format.height, &context)), "open selected mode");
            std::vector<BYTE> input(format.Bytes(), 129), output(format.Bytes());
            Require(SUCCEEDED(CamoRipWriteFrame(context, input.data(), format.Bytes(), 0)), "write selected mode");
            Require(reader.Read(output.data()), "read selected mode after cache resize");
            Require(input == output, "selected mode pixels");
            Require(CamoRipWriteFrame(context, input.data(), format.Bytes() - 1, 0) == E_INVALIDARG, "reject wrong byte length");
            CamoRipCloseWriter(context); context = nullptr;
            Require(!reader.Read(output.data()), "selected mode close invalidates cache");
        }
        Require(CamoRipOpenWriterMode(path.c_str(), 3841, 2160, &context) == E_INVALIDARG, "reject odd resolution");
        std::wcout << L"Frame channel cache, collision, expiry, stop and restart checks passed.\n";
        return 0;
    } catch (const std::exception& error) {
        if (context) CamoRipCloseWriter(context);
        std::cerr << error.what() << '\n';
        return 1;
    }
}
