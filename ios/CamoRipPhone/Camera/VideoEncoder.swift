import Foundation
import VideoToolbox
import CoreMedia

final class VideoEncoder {
    struct EncodedFrame {
        let data: Data
        let ptsUs: Int64
        let isKeyframe: Bool
    }

    private struct Settings {
        let width: Int32
        let height: Int32
        let fps: Int32
        let bitrate: Int
    }

    var onEncodedFrame: ((EncodedFrame) -> Void)?
    var onError: ((Error) -> Void)?
    private let queue = DispatchQueue(label: "com.camorip.encoder", qos: .userInitiated)
    private let admission = NSLock()
    private var queuedSamples = 0
    private var compression: VTCompressionSession?
    private var settings: Settings?
    private var sequence: UInt64 = 0

    deinit {
        if let compression { VTCompressionSessionInvalidate(compression) }
    }

    func prepare(width: Int32, height: Int32, fps: Int32 = 60, bitrate: Int = 12_000_000,
                 completion: @escaping (Bool) -> Void) {
        queue.async { [weak self] in
            guard let self else { return }
            self.retire()
            var ready = false
            do {
                let settings = Settings(width: width, height: height, fps: fps, bitrate: bitrate)
                self.compression = try self.open(settings)
                self.settings = settings
                self.sequence = 0
                ready = true
            } catch { self.onError?(error) }
            DispatchQueue.main.async { completion(ready) }
        }
    }

    func encode(_ sample: CMSampleBuffer) {
        admission.lock()
        guard queuedSamples < 2 else {
            admission.unlock()
            DiagnosticLog.record(.encodeDropped)
            return
        }
        queuedSamples += 1
        admission.unlock()
        queue.async { [weak self] in
            guard let self else { return }
            defer {
                self.admission.lock()
                self.queuedSamples -= 1
                self.admission.unlock()
            }
            self.submit(sample)
        }
    }

    func requestKeyframe() { queue.async { [weak self] in self?.sequence = 0 } }

    func flush() {
        queue.async { [weak self] in
            if let compression = self?.compression {
                VTCompressionSessionCompleteFrames(compression, untilPresentationTimeStamp: .invalid)
            }
        }
    }

    func invalidate(completion: (() -> Void)? = nil) {
        queue.async { [weak self] in
            self?.retire()
            if let completion { DispatchQueue.main.async(execute: completion) }
        }
    }

    private func retire() {
        guard let compression else { return }
        VTCompressionSessionCompleteFrames(compression, untilPresentationTimeStamp: .invalid)
        VTCompressionSessionInvalidate(compression)
        self.compression = nil
        settings = nil
    }

    private func submit(_ sample: CMSampleBuffer) {
        guard let compression, let settings, let pixels = CMSampleBufferGetImageBuffer(sample),
              CVPixelBufferGetWidth(pixels) == Int(settings.width),
              CVPixelBufferGetHeight(pixels) == Int(settings.height) else {
            DiagnosticLog.record(.encodeDropped)
            return
        }
        // Capture can deliver the old format while a mode change drains.
        let keyframe = sequence % UInt64(max(1, settings.fps) * 2) == 0
        let options: CFDictionary? = keyframe ? [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary : nil
        let result = VTCompressionSessionEncodeFrame(compression, imageBuffer: pixels,
            presentationTimeStamp: CMSampleBufferGetPresentationTimeStamp(sample),
            duration: CMSampleBufferGetDuration(sample), frameProperties: options,
            sourceFrameRefcon: nil, infoFlagsOut: nil)
        sequence &+= 1
        if result != noErr { onError?(Self.error(result)) }
    }

    private func open(_ settings: Settings) throws -> VTCompressionSession {
        guard settings.width > 0, settings.height > 0, settings.fps > 0,
              CaptureEncodingLimits.bitrateRange.contains(settings.bitrate) else {
            throw NSError(domain: "CamoRip.Encoder", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "Invalid video encoder settings."])
        }
        var created: VTCompressionSession?
        let result = VTCompressionSessionCreate(allocator: nil, width: settings.width, height: settings.height,
            codecType: kCMVideoCodecType_H264, encoderSpecification: nil, imageBufferAttributes: nil,
            compressedDataAllocator: nil, outputCallback: Self.output,
            refcon: Unmanaged.passUnretained(self).toOpaque(), compressionSessionOut: &created)
        guard result == noErr, let session = created else { throw Self.error(result) }
        do {
            try Self.require(VTSessionSetProperty(session, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue))
            try Self.require(VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse))
            try Self.require(VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ProfileLevel, value: kVTProfileLevel_H264_High_AutoLevel))
            try Self.require(VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate, value: NSNumber(value: settings.bitrate)))
            VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: NSNumber(value: settings.fps))
            VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: NSNumber(value: settings.fps * 2))
            VTSessionSetProperty(session, key: kVTCompressionPropertyKey_H264EntropyMode, value: kVTH264EntropyMode_CABAC)
            VTSessionSetProperty(session, key: kVTCompressionPropertyKey_DataRateLimits,
                value: [Double(settings.bitrate) * 1.5 / 8, 1.0] as CFArray)
            let delayResult = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxFrameDelayCount, value: NSNumber(value: 1))
            var bounded = delayResult == noErr
            var prepared = VTCompressionSessionPrepareToEncodeFrames(session)
            // Older encoders may accept the delay property but reject preparation.
            if prepared != noErr && bounded {
                VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxFrameDelayCount,
                                     value: NSNumber(value: kVTUnlimitedFrameDelayCount))
                prepared = VTCompressionSessionPrepareToEncodeFrames(session)
                bounded = false
            }
            try Self.require(prepared)
            var hardware = "query unavailable"
            if #available(iOS 17.4, *) {
                var value: CFTypeRef?
                let status = VTSessionCopyProperty(session, key: kVTCompressionPropertyKey_UsingHardwareAcceleratedVideoEncoder,
                                                  allocator: nil, valueOut: &value)
                hardware = (value as? Bool).map { $0 ? "true" : "false" } ?? "unknown (status \(status))"
            }
            DiagnosticLog.write("Encoder ready: H.264 High \(settings.width)x\(settings.height) @ \(settings.fps), target=\(settings.bitrate), hardware=\(hardware), maxFrameDelay=\(bounded ? "1" : "default"), delayPropertyStatus=\(delayResult)")
            return session
        } catch {
            VTCompressionSessionInvalidate(session)
            throw error
        }
    }

    private static func require(_ status: OSStatus) throws {
        if status != noErr { throw error(status) }
    }

    private static func error(_ status: OSStatus) -> NSError {
        NSError(domain: NSOSStatusErrorDomain, code: Int(status))
    }

    private static let output: VTCompressionOutputCallback = { reference, _, status, flags, sample in
        guard let reference else { return }
        let owner = Unmanaged<VideoEncoder>.fromOpaque(reference).takeUnretainedValue()
        guard status == noErr else { owner.onError?(Self.error(status)); return }
        guard !flags.contains(.frameDropped), let sample, CMSampleBufferDataIsReady(sample),
              let frame = Self.packet(sample) else { return }
        DiagnosticLog.record(.encoded)
        owner.onEncodedFrame?(frame)
    }

    private static func packet(_ sample: CMSampleBuffer) -> EncodedFrame? {
        guard let block = CMSampleBufferGetDataBuffer(sample) else { return nil }
        let size = CMBlockBufferGetDataLength(block)
        guard size > 0 else { return nil }
        let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[CFString: Any]]
        let keyframe = !(attachments?.first?[kCMSampleAttachmentKey_NotSync] as? Bool ?? false)
        let prefix: Data
        if keyframe, let format = CMSampleBufferGetFormatDescription(sample) { prefix = parameterSets(format) }
        else { prefix = Data() }

        var count = 0
        var pointer: UnsafeMutablePointer<Int8>?
        let status = CMBlockBufferGetDataPointer(block, atOffset: 0, lengthAtOffsetOut: &count,
                                                totalLengthOut: nil, dataPointerOut: &pointer)
        let data: Data?
        if status == noErr, count >= size, let pointer {
            data = makeAnnexB(UnsafeRawBufferPointer(start: pointer, count: size), parameterSets: prefix)
        } else {
            var copy = Data(count: size)
            let copied = copy.withUnsafeMutableBytes { bytes -> OSStatus in
                guard let address = bytes.baseAddress else { return -1 }
                return CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: size, destination: address)
            }
            guard copied == noErr else { return nil }
            data = copy.withUnsafeBytes { makeAnnexB($0, parameterSets: prefix) }
        }
        let timestamp = CMTimeConvertScale(CMSampleBufferGetPresentationTimeStamp(sample), timescale: 1_000_000, method: .default)
        guard timestamp.isValid, let data, !data.isEmpty else { return nil }
        return EncodedFrame(data: data, ptsUs: timestamp.value, isKeyframe: keyframe)
    }

    private static func parameterSets(_ format: CMFormatDescription) -> Data {
        var count = 0
        guard CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, parameterSetIndex: 0,
            parameterSetPointerOut: nil, parameterSetSizeOut: nil, parameterSetCountOut: &count,
            nalUnitHeaderLengthOut: nil) == noErr else { return Data() }
        var result = Data()
        for index in 0..<count {
            var pointer: UnsafePointer<UInt8>?
            var size = 0
            if CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, parameterSetIndex: index,
                parameterSetPointerOut: &pointer, parameterSetSizeOut: &size,
                parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil) == noErr, let pointer {
                result.append(contentsOf: [0, 0, 0, 1])
                result.append(pointer, count: size)
            }
        }
        return result
    }
}
