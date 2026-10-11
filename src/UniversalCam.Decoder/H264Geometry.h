// Original implementation, MIT license. Only SPS geometry is read here; Windows
// remains responsible for decoding. All bit reads, dimensions and loops are bounded.
#pragma once
#include <array>
#include <cstdint>
#include <cstddef>

namespace H264 {
struct Geometry {
    uint32_t codedWidth = 0, codedHeight = 0;
    uint32_t width = 0, height = 0, left = 0, top = 0;
};

class Bits {
    const uint8_t* data_;
    size_t size_, bit_ = 0;
public:
    bool valid = true;
    Bits(const uint8_t* data, size_t size) : data_(data), size_(size) {}
    uint32_t Read(unsigned count) {
        if (count > 32 || bit_ + count > size_ * 8) { valid = false; return 0; }
        uint32_t result = 0;
        for (unsigned i = 0; i < count; ++i, ++bit_)
            result = (result << 1) | ((data_[bit_ / 8] >> (7 - bit_ % 8)) & 1);
        return result;
    }
    uint32_t Ue() {
        unsigned zeros = 0;
        while (valid && Read(1) == 0) {
            if (++zeros > 31) { valid = false; return 0; }
        }
        if (!valid) return 0;
        const auto suffix = Read(zeros);
        return ((uint32_t{1} << zeros) - 1) + suffix;
    }
    int64_t Se() {
        const auto code = Ue();
        return (code & 1) ? (static_cast<int64_t>(code) + 1) / 2 : -static_cast<int64_t>(code) / 2;
    }
};

inline bool ParseSps(const uint8_t* nal, size_t size, Geometry& result) {
    if (size < 2 || size > 65536 || (nal[0] & 0x1f) != 7 || (nal[0] & 0x80)) return false;
    std::array<uint8_t, 65536> rbsp{};
    size_t count = 0;
    unsigned zeros = 0;
    for (size_t i = 1; i < size; ++i) {
        const auto value = nal[i];
        if (zeros >= 2 && value == 3) {
            if (i + 1 >= size || nal[i + 1] > 3) return false;
            zeros = 0;
            continue;
        }
        rbsp[count++] = value;
        zeros = value == 0 ? zeros + 1 : 0;
    }
    Bits b(rbsp.data(), count);
    const auto profile = b.Read(8);
    b.Read(8); b.Read(8); // constraint flags and level
    if (b.Ue() > 31) return false; // seq_parameter_set_id
    uint32_t chroma = 1;
    bool separateColour = false;
    switch (profile) {
    case 100: case 110: case 122: case 244: case 44: case 83: case 86:
    case 118: case 128: case 138: case 139: case 134: case 135:
        chroma = b.Ue();
        if (chroma > 3) return false;
        if (chroma == 3) separateColour = b.Read(1) != 0;
        if (b.Ue() > 6 || b.Ue() > 6) return false; // bit_depth_minus8
        b.Read(1);
        if (b.Read(1)) {
            const unsigned lists = chroma == 3 ? 12 : 8;
            for (unsigned i = 0; i < lists; ++i) {
                if (!b.Read(1)) continue;
                int64_t last = 8, next = 8;
                const unsigned entries = i < 6 ? 16 : 64;
                for (unsigned j = 0; j < entries; ++j) {
                    if (next != 0) next = ((last + b.Se()) % 256 + 256) % 256;
                    if (next != 0) last = next;
                }
            }
        }
        break;
    default: break; // Baseline, Main and Extended have no chroma fields.
    }
    if (b.Ue() > 12) return false; // log2_max_frame_num_minus4
    const auto poc = b.Ue();
    if (poc == 0) { if (b.Ue() > 12) return false; }
    else if (poc == 1) {
        b.Read(1); b.Se(); b.Se();
        const auto cycle = b.Ue();
        if (cycle > 255) return false;
        for (uint32_t i = 0; i < cycle; ++i) b.Se();
    } else if (poc != 2) return false;
    b.Ue(); b.Read(1); // max_num_ref_frames, gaps flag
    const auto columnsMinus1 = b.Ue(), mapRowsMinus1 = b.Ue();
    if (columnsMinus1 > 255 || mapRowsMinus1 > 143) return false;
    const auto frameMbsOnly = b.Read(1);
    if (!frameMbsOnly) b.Read(1);
    b.Read(1); // direct_8x8_inference_flag
    uint64_t left = 0, right = 0, top = 0, bottom = 0;
    if (b.Read(1)) { left = b.Ue(); right = b.Ue(); top = b.Ue(); bottom = b.Ue(); }
    if (!b.valid) return false;
    const uint32_t codedWidth = (columnsMinus1 + 1) * 16;
    const uint32_t codedHeight = (mapRowsMinus1 + 1) * 16 * (2 - frameMbsOnly);
    const auto chromaArray = separateColour ? 0 : chroma;
    const uint32_t unitX = chromaArray == 1 || chromaArray == 2 ? 2 : 1;
    const uint32_t unitY = (chromaArray == 1 ? 2 : 1) * (2 - frameMbsOnly);
    left *= unitX; right *= unitX; top *= unitY; bottom *= unitY;
    if (codedHeight > 2304 || left + right >= codedWidth || top + bottom >= codedHeight) return false;
    const auto width = static_cast<uint32_t>(codedWidth - left - right);
    const auto height = static_cast<uint32_t>(codedHeight - top - bottom);
    if ((left | top | width | height) & 1) return false; // packed NV12 needs even geometry
    result = {codedWidth, codedHeight, width, height, static_cast<uint32_t>(left), static_cast<uint32_t>(top)};
    return true;
}

inline size_t StartCode(const uint8_t* data, size_t size, size_t from, size_t& prefix) {
    for (size_t i = from; i + 2 < size; ++i) {
        if (data[i] || data[i + 1]) continue;
        if (data[i + 2] == 1) { prefix = 3; return i; }
        if (i + 3 < size && !data[i + 2] && data[i + 3] == 1) { prefix = 4; return i; }
    }
    prefix = 0;
    return size;
}

inline bool FindGeometry(const uint8_t* data, size_t size, Geometry& result) {
    size_t prefix = 0, position = StartCode(data, size, 0, prefix);
    while (position < size) {
        const size_t begin = position + prefix;
        size_t nextPrefix = 0;
        const size_t next = StartCode(data, size, begin, nextPrefix);
        if (begin < next && (data[begin] & 0x1f) == 7) return ParseSps(data + begin, next - begin, result);
        position = next; prefix = nextPrefix;
    }
    return false;
}
}
