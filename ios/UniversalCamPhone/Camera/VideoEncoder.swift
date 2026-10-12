import Foundation
import VideoToolbox
import CoreMedia

/// Encodes raw YUV CMSampleBuffers to H.264 Annex B NAL units using VideoToolbox hardware encoder.
final class VideoEncoder {

    // MARK: - Types

    struct EncodedFrame {
        let data:      Data      // H.264 Annex B NAL unit(s)
        let ptsUs:     Int64     // Presentation timestamp in microseconds
        let isKeyframe: Bool
    }

    // MARK: - Public

    /// Called on the encoder's private queue for each encoded frame.
    var onEncodedFrame: ((EncodedFrame) -> Void)?

    /// Called when the encoder encounters an error.
    var onError: ((Error) -> Void)?

    // MARK: - Private (all accessed exclusively on encoderQueue)

    private var session:      VTCompressionSession?
    private let encoderQueue  = DispatchQueue(label: "com.universalcam.videoencoder", qos: .userInitiated)
    private var frameCount    = 0
    private let pendingLock = NSLock()
    private var pendingFrames = 0

    private var lastWidth: Int32 = 1920
    private var lastHeight: Int32 = 1080
    private var lastFPS: Int32 = 60
    private var lastBitrate: Int = 8_000_000

    // MARK: - Lifecycle

    func prepare(width: Int32, height: Int32, fps: Int32 = 60, bitrate: Int = 12_000_000,
                 completion: @escaping (Bool) -> Void) {
        encoderQueue.async { [weak self] in
            guard let self else { return }
            self.createSession(width: width, height: height, fps: fps, bitrate: bitrate)
            let ready = self.session != nil
            DispatchQueue.main.async { completion(ready) }
        }
    }

    /// Encode a video sample buffer. Safe to call from any thread —
    /// the actual encoding is dispatched to the encoder queue.
    func encode(_ sampleBuffer: CMSampleBuffer) {
        // Bound retained capture buffers when the encoder cannot keep up.
        pendingLock.lock()
        guard pendingFrames < 2 else {
            pendingLock.unlock()
            DiagnosticLog.record(.encodeDropped)
            return
        }
        pendingFrames += 1
        pendingLock.unlock()
        let retained = sampleBuffer
        encoderQueue.async { [weak self] in
            guard let self else { return }
            defer {
                self.pendingLock.lock()
                self.pendingFrames -= 1
                self.pendingLock.unlock()
            }
            self.encodeOnQueue(retained)
        }
    }

    func requestKeyframe() {
        encoderQueue.async { [weak self] in self?.frameCount = 0 }
    }

    func flush() {
        encoderQueue.async { [weak self] in
            guard let session = self?.session else { return }
            VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
        }
    }

    func invalidate(completion: (() -> Void)? = nil) {
        encoderQueue.async { [weak self] in
            if let self, let session = self.session {
                VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
                VTCompressionSessionInvalidate(session)
                self.session = nil
            }
            if let completion { DispatchQueue.main.async(execute: completion) }
        }
    }

    deinit {
        if let session {
            VTCompressionSessionInvalidate(session)
        }
    }

    // MARK: - Private (must run on encoderQueue)

    private func encodeOnQueue(_ sampleBuffer: CMSampleBuffer) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        // Only encode the negotiated dimensions. Capture can still deliver old
        // buffers around a format transition; they must not change the session.
        let bufW = Int32(CVPixelBufferGetWidth(pixelBuffer))
        let bufH = Int32(CVPixelBufferGetHeight(pixelBuffer))
        if session == nil || bufW != lastWidth || bufH != lastHeight {
            // A late capture buffer from the previous mode must never rebuild
            // the encoder back to the old dimensions after negotiation.
            DiagnosticLog.record(.encodeDropped)
            return
        }

        guard let session else { return }
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let dur = CMSampleBufferGetDuration(sampleBuffer)

        var flags: VTEncodeInfoFlags = []
        // Force a keyframe every two seconds at the negotiated capture rate.
        let forceKeyframe = frameCount % max(1, Int(lastFPS) * 2) == 0
        let frameProperties: CFDictionary? = forceKeyframe
            ? [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary
            : nil

        let status = VTCompressionSessionEncodeFrame(
            session, imageBuffer: pixelBuffer, presentationTimeStamp: pts,
            duration: dur, frameProperties: frameProperties,
            sourceFrameRefcon: nil, infoFlagsOut: &flags
        )

        if status != noErr {
            let error = NSError(domain: NSOSStatusErrorDomain, code: Int(status))
            onError?(error)
            recreateSession()
        }

        frameCount += 1
    }

    private func recreateSession() {
        if let session {
            VTCompressionSessionInvalidate(session)
            self.session = nil
        }
        createSession(width: lastWidth, height: lastHeight, fps: lastFPS, bitrate: lastBitrate)
    }

    private func createSession(width: Int32, height: Int32, fps: Int32, bitrate: Int) {
        if let previous = session {
            VTCompressionSessionCompleteFrames(previous, untilPresentationTimeStamp: .invalid)
            VTCompressionSessionInvalidate(previous)
            session = nil
        }
        lastWidth = width
        lastHeight = height
        lastFPS = fps
        lastBitrate = bitrate
        frameCount = 0  // always start new session with a forced keyframe

        var s: VTCompressionSession?
        let status = VTCompressionSessionCreate(
            allocator: nil,
            width: width,
            height: height,
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: nil,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            outputCallback: outputCallback,
            refcon: Unmanaged.passUnretained(self).toOpaque(),
            compressionSessionOut: &s
        )
        guard status == noErr, let s else {
            print("[VideoEncoder] Failed to create VTCompressionSession: \(status)")
            onError?(NSError(domain: NSOSStatusErrorDomain, code: Int(status)))
            return
        }
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_RealTime,          value: kCFBooleanTrue)
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        // High profile supports CABAC and the negotiated 4K modes.
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_ProfileLevel,      value: kVTProfileLevel_H264_High_AutoLevel)
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_AverageBitRate,    value: NSNumber(value: bitrate))
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: NSNumber(value: fps))
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: NSNumber(value: fps * 2))
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_H264EntropyMode,   value: kVTH264EntropyMode_CABAC)
        // Set data rate limits: [bytes per second, period in seconds]
        let dataRateLimit = [Double(bitrate) / 8.0 * 1.5, 1.0] as CFArray
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_DataRateLimits, value: dataRateLimit)
        let prepareStatus = VTCompressionSessionPrepareToEncodeFrames(s)
        guard prepareStatus == noErr else {
            VTCompressionSessionInvalidate(s)
            onError?(NSError(domain: NSOSStatusErrorDomain, code: Int(prepareStatus)))
            return
        }
        self.session = s
    }
}

// MARK: - VTCompressionOutputCallback (C function)

private func outputCallback(
    outputCallbackRefCon: UnsafeMutableRawPointer?,
    sourceFrameRefCon: UnsafeMutableRawPointer?,
    status: OSStatus,
    infoFlags: VTEncodeInfoFlags,
    sampleBuffer: CMSampleBuffer?
) {
    guard let refCon = outputCallbackRefCon else { return }
    let encoder = Unmanaged<VideoEncoder>.fromOpaque(refCon).takeUnretainedValue()
    guard status == noErr else {
        encoder.onError?(NSError(domain: NSOSStatusErrorDomain, code: Int(status)))
        return
    }
    guard
        let sampleBuffer,
        CMSampleBufferDataIsReady(sampleBuffer)
    else { return }

    // Detect keyframe by checking sample attachments
    let isKeyframe: Bool
    if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[CFString: Any]],
       let first = attachments.first {
        // If kCMSampleAttachmentKey_NotSync is absent or false, it's a keyframe
        isKeyframe = !(first[kCMSampleAttachmentKey_NotSync] as? Bool ?? false)
    } else {
        isKeyframe = true
    }

    guard let dataBuffer = CMSampleBufferGetDataBuffer(sampleBuffer) else { return }
    let totalLength = CMBlockBufferGetDataLength(dataBuffer)
    guard totalLength > 0 else { return }
    // Usually VideoToolbox gives contiguous AVCC bytes. Read them in place;
    // retain a copy only for the valid noncontiguous CMBlockBuffer case.
    let parameterSets: Data
    if isKeyframe, let format = CMSampleBufferGetFormatDescription(sampleBuffer) {
        parameterSets = annexBParameterSets(from: format)
    } else { parameterSets = Data() }
    var contiguousLength = 0
    var pointer: UnsafeMutablePointer<Int8>?
    let pointerStatus = CMBlockBufferGetDataPointer(dataBuffer, atOffset: 0,
        lengthAtOffsetOut: &contiguousLength, totalLengthOut: nil, dataPointerOut: &pointer)
    let annexB: Data?
    if pointerStatus == kCMBlockBufferNoErr, contiguousLength >= totalLength, let pointer {
        annexB = makeAnnexB(UnsafeRawBufferPointer(start: pointer, count: totalLength), parameterSets: parameterSets)
    } else {
        var avcc = Data(count: totalLength)
        let copyStatus = avcc.withUnsafeMutableBytes { bytes -> OSStatus in
            guard let address = bytes.baseAddress else { return -1 }
            return CMBlockBufferCopyDataBytes(dataBuffer, atOffset: 0, dataLength: totalLength, destination: address)
        }
        guard copyStatus == kCMBlockBufferNoErr else { return }
        annexB = avcc.withUnsafeBytes { makeAnnexB($0, parameterSets: parameterSets) }
    }
    guard let annexB, !annexB.isEmpty else { return }
    let timestamp = CMTimeConvertScale(CMSampleBufferGetPresentationTimeStamp(sampleBuffer), timescale: 1_000_000, method: .default)
    guard timestamp.isValid else { return }
    let ptsUs = timestamp.value
    let frame = VideoEncoder.EncodedFrame(data: annexB, ptsUs: ptsUs, isKeyframe: isKeyframe)
    DiagnosticLog.record(.encoded)
    encoder.onEncodedFrame?(frame)
}

/// Extract SPS and PPS from the format description and return as Annex B data.
private func annexBParameterSets(from formatDescription: CMFormatDescription) -> Data {
    var data = Data()
    var paramSetCount = 0
    CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
        formatDescription, parameterSetIndex: 0,
        parameterSetPointerOut: nil, parameterSetSizeOut: nil,
        parameterSetCountOut: &paramSetCount, nalUnitHeaderLengthOut: nil
    )
    for i in 0..<paramSetCount {
        var paramSetPtr: UnsafePointer<UInt8>?
        var paramSetSize = 0
        let status = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
            formatDescription, parameterSetIndex: i,
            parameterSetPointerOut: &paramSetPtr, parameterSetSizeOut: &paramSetSize,
            parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil
        )
        if status == noErr, let ptr = paramSetPtr {
            data.append(contentsOf: [0x00, 0x00, 0x00, 0x01])
            data.append(ptr, count: paramSetSize)
        }
    }
    return data
}
