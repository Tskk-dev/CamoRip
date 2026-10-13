import XCTest
import AVFoundation

final class VideoEncoderTests: XCTestCase {
    @MainActor func testInvalidSettingsFailPreparation() async {
        let encoder = VideoEncoder()
        let ready = await withCheckedContinuation { continuation in
            encoder.prepare(width: 0, height: 64, fps: 30, bitrate: 1_000_000) {
                continuation.resume(returning: $0)
            }
        }
        XCTAssertFalse(ready)
    }

    @MainActor func testCompressionProducesTimestampedAnnexBKeyframe() async throws {
        let encoder = VideoEncoder()
        let ready = await withCheckedContinuation { continuation in
            encoder.prepare(width: 64, height: 64, fps: 30, bitrate: 1_000_000) {
                continuation.resume(returning: $0)
            }
        }
        guard ready else { return XCTFail("VideoToolbox could not prepare H.264 compression") }
        let delivered = expectation(description: "Encoded frame")
        var result: VideoEncoder.EncodedFrame?
        encoder.onEncodedFrame = { frame in
            result = frame
            delivered.fulfill()
        }
        encoder.onError = { error in
            XCTFail(error.localizedDescription)
            delivered.fulfill()
        }

        var pixels: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(nil, 64, 64,
            kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange, nil, &pixels), kCVReturnSuccess)
        let buffer = try XCTUnwrap(pixels)
        CVPixelBufferLockBaseAddress(buffer, [])
        for plane in 0..<CVPixelBufferGetPlaneCount(buffer) {
            if let address = CVPixelBufferGetBaseAddressOfPlane(buffer, plane) {
                let count = CVPixelBufferGetBytesPerRowOfPlane(buffer, plane) * CVPixelBufferGetHeightOfPlane(buffer, plane)
                address.initializeMemory(as: UInt8.self, repeating: plane == 0 ? 16 : 128, count: count)
            }
        }
        CVPixelBufferUnlockBaseAddress(buffer, [])
        var format: CMVideoFormatDescription?
        XCTAssertEqual(CMVideoFormatDescriptionCreateForImageBuffer(allocator: nil,
            imageBuffer: buffer, formatDescriptionOut: &format), noErr)
        var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: 30),
            presentationTimeStamp: CMTime(value: 3, timescale: 1), decodeTimeStamp: .invalid)
        var sample: CMSampleBuffer?
        XCTAssertEqual(CMSampleBufferCreateReadyWithImageBuffer(allocator: nil,
            imageBuffer: buffer, formatDescription: try XCTUnwrap(format),
            sampleTiming: &timing, sampleBufferOut: &sample), noErr)
        encoder.encode(try XCTUnwrap(sample))
        encoder.flush()
        await fulfillment(of: [delivered], timeout: 10)
        let frame = try XCTUnwrap(result)
        XCTAssertEqual(frame.ptsUs, 3_000_000)
        XCTAssertTrue(frame.isKeyframe)
        XCTAssertEqual(Array(frame.data.prefix(4)), [0, 0, 0, 1])
        await withCheckedContinuation { continuation in
            encoder.invalidate { continuation.resume() }
        }
    }
}
