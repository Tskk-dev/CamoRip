import Foundation

/// Four-byte AVCC lengths become Annex B start codes. Append from the original
/// buffer directly, avoiding a temporary Data allocation for every NAL unit.
func makeAnnexB(_ avcc: UnsafeRawBufferPointer, parameterSets: Data) -> Data? {
    var output = Data(capacity: avcc.count + parameterSets.count)
    output.append(parameterSets)
    var offset = 0
    while offset + 4 <= avcc.count {
        let length = Int(avcc[offset]) << 24 | Int(avcc[offset + 1]) << 16 |
            Int(avcc[offset + 2]) << 8 | Int(avcc[offset + 3])
        offset += 4
        guard length > 0, length <= avcc.count - offset, let base = avcc.baseAddress else { return nil }
        output.append(contentsOf: [0, 0, 0, 1])
        output.append(base.advanced(by: offset).assumingMemoryBound(to: UInt8.self), count: length)
        offset += length
    }
    return offset == avcc.count && offset > 0 ? output : nil
}
