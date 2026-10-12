import XCTest
import AVFoundation

/// Runs the production phone coordinator against a simulated PC controller,
/// camera and encoder. No USB tunnel, physical camera or H.264 codec is emulated.
final class StreamingSwitchTests: XCTestCase {
    private final class Trace { var events: [String] = [] }

    private final class PC: PhoneTransport {
        var onControlMessage: ((Data) -> Void)?
        var onStateChange: ((USBTransport.USBConnectionState) -> Void)?
        var onVideoBackpressure: (() -> Void)?
        var controls: [ControlMessage] = []
        var frames: [VideoEncoder.EncodedFrame] = []
        func startListening() { onStateChange?(.listening) }
        func disconnect() { onStateChange?(.disconnected) }
        func sendControl(_ message: ControlMessage) {
            // Exercise the production Codable contract in both directions.
            controls.append(try! JSONDecoder().decode(ControlMessage.self,
                from: JSONEncoder().encode(message)))
        }
        func sendVideoFrame(_ frame: VideoEncoder.EncodedFrame) { frames.append(frame) }
        func send(_ message: ControlMessage) throws {
            onControlMessage?(try JSONEncoder().encode(message))
        }
    }

    private final class Encoder: PhoneEncoder {
        let trace: Trace
        var onEncodedFrame: ((VideoEncoder.EncodedFrame) -> Void)?
        var onError: ((Error) -> Void)?
        var holdDrain = false
        var holdPrepare = false
        var ready = true
        var drains: [() -> Void] = []
        var preparations: [(Bool) -> Void] = []
        var encoded = 0
        var keyframe = true
        init(_ trace: Trace) { self.trace = trace }
        func prepare(width: Int32, height: Int32, fps: Int32, bitrate: Int,
                     completion: @escaping (Bool) -> Void) {
            trace.events.append("prepare:\(width)x\(height)@\(fps)")
            if holdPrepare { preparations.append(completion) } else { completion(ready) }
        }
        func encode(_ sampleBuffer: CMSampleBuffer) {
            encoded += 1
            // VideoToolbox delivers output asynchronously. Delivering inline
            // would reenter the coordinator while its raw-frame gate is locked.
            DispatchQueue.main.async { self.emit() }
        }
        func emit() {
            onEncodedFrame?(.init(data: Data([0, 0, 0, 1, 0x65]),
                                 ptsUs: Int64(encoded) * 16_667, isKeyframe: keyframe))
            keyframe = false
        }
        func requestKeyframe() { trace.events.append("keyframe"); keyframe = true }
        func flush() { trace.events.append("flush") }
        func invalidate(completion: (() -> Void)?) {
            trace.events.append("drain")
            guard let completion else { return }
            if holdDrain { drains.append(completion) } else { completion() }
        }
    }

    private final class Camera: PhoneCapture {
        let trace: Trace
        var configuration: CaptureConfiguration? = .init(width: 1920, height: 1080, fps: 60)
        var currentCameraID = "rear"
        var holdSwitch = false
        var failSwitch = false
        var switches: [() -> Void] = []
        var availableCameras: [CameraInfo] {
            ["rear", "front"].map { .init(id: $0, name: $0, position: $0,
                                         type: "wide_angle", zoomFactor: 1) }
        }
        init(_ trace: Trace) { self.trace = trace }
        func start() { trace.events.append("start") }
        func stop() { trace.events.append("stop") }
        func turnTorchOff() { trace.events.append("torchOff") }
        func configure(width: Int32, height: Int32, fps: Int32,
                       completion: @escaping (Result<CaptureConfiguration, Error>) -> Void) {
            configuration = .init(width: width, height: height, fps: fps)
            completion(.success(configuration!))
        }
        func switchCamera(completion: ((CameraInfo?) -> Void)?) {
            switchToCamera(withID: currentCameraID == "rear" ? "front" : "rear", completion: completion)
        }
        func switchToCamera(withID id: String, completion: ((CameraInfo?) -> Void)?) {
            trace.events.append("switch:\(id)")
            let finish: () -> Void = {
                guard !self.failSwitch else { completion?(nil); return }
                self.currentCameraID = id
                // Simulate the 6s Plus front camera negotiating a smaller mode.
                self.configuration = id == "front"
                    ? .init(width: 1280, height: 720, fps: 30)
                    : .init(width: 1920, height: 1080, fps: 60)
                completion?(self.availableCameras.first { $0.id == id })
            }
            if holdSwitch { switches.append(finish) } else { finish() }
        }
        func readControls(completion: @escaping (CameraControlState) -> Void) {
            var snapshot = CameraControlState(); snapshot.camera_id = currentCameraID
            completion(snapshot)
        }
        func applyControls(_ request: CameraControlRequest, allowTorch: Bool,
                           completion: @escaping (CameraControlState) -> Void) {
            readControls(completion: completion)
        }
    }

    @MainActor private final class Rig {
        let trace = Trace()
        let pc = PC()
        let encoder: Encoder
        let camera: Camera
        let phone: ConnectionManager
        init() {
            encoder = Encoder(trace); camera = Camera(trace)
            phone = ConnectionManager(transport: pc, encoder: encoder,
                applicationIsActive: { true }, cameraIsAuthorized: { true },
                setIdleTimerDisabled: { _ in }, enableTimers: false)
            phone.cameraSession = camera
        }
    }

    @MainActor private func settle() async {
        // Drain callbacks that production dispatches to the main queue.
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            DispatchQueue.main.async { continuation.resume() }
        }
    }

    @MainActor private func connected(streaming: Bool = true) async throws -> Rig {
        let rig = Rig()
        rig.pc.onStateChange?(.connected); await settle()
        try rig.pc.send(.welcome); await settle()
        try rig.pc.send(.configure(resolution: "1920x1080", fps: 60, codec: "h264", bitrate: 12_000_000))
        await settle()
        if streaming { try rig.pc.send(.startStream); await settle() }
        XCTAssertEqual(rig.phone.state, streaming ? .streaming : .connected)
        rig.trace.events.removeAll(); rig.pc.controls.removeAll()
        return rig
    }

    private func sample() throws -> CMSampleBuffer {
        var pixel: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(nil, 16, 16,
            kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange, nil, &pixel), kCVReturnSuccess)
        let buffer = try XCTUnwrap(pixel)
        var format: CMVideoFormatDescription?
        XCTAssertEqual(CMVideoFormatDescriptionCreateForImageBuffer(allocator: nil,
            imageBuffer: buffer, formatDescriptionOut: &format), noErr)
        var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: 60),
            presentationTimeStamp: .zero, decodeTimeStamp: .invalid)
        var result: CMSampleBuffer?
        XCTAssertEqual(CMSampleBufferCreateReadyWithImageBuffer(allocator: nil,
            imageBuffer: buffer, formatDescription: try XCTUnwrap(format),
            sampleTiming: &timing, sampleBufferOut: &result), noErr)
        return try XCTUnwrap(result)
    }

    @MainActor func testOneHundredPCSwitchesResumeFramesAndAcknowledgeActualMode() async throws {
        let rig = try await connected()
        let frame = try sample()
        for index in 0..<100 {
            try rig.pc.send(.switchCamera(cameraID: nil)); await settle()
            XCTAssertEqual(rig.phone.state, .streaming)
            XCTAssertTrue(rig.phone.wantsStreaming)
            XCTAssertEqual(rig.camera.currentCameraID, index % 2 == 0 ? "front" : "rear")
            rig.phone.handleVideoSample(frame)
            await settle()
            XCTAssertEqual(rig.pc.frames.count, index + 1)
            XCTAssertTrue(rig.pc.frames.last!.isKeyframe)
        }
        XCTAssertEqual(rig.pc.controls.filter { $0.type == "switch_camera_ack" }.count, 100)
        let modes = rig.pc.controls.compactMap { message -> String? in
            if case .configureAck(let resolution, let fps, _) = message { return "\(resolution)@\(fps)" }
            return nil
        }
        XCTAssertEqual(Array(modes.prefix(2)), ["1280x720@30", "1920x1080@60"])
    }

    @MainActor func testFramesAreGatedUntilOldEncoderDrainsAndNewEncoderIsReady() async throws {
        let rig = try await connected(); let frame = try sample()
        rig.encoder.holdDrain = true; rig.encoder.holdPrepare = true
        rig.phone.switchCamera()
        rig.phone.handleVideoSample(frame); rig.encoder.emit()
        XCTAssertTrue(rig.pc.frames.isEmpty)
        XCTAssertFalse(rig.trace.events.contains("switch:front"))
        rig.encoder.drains.removeFirst()()
        XCTAssertTrue(rig.trace.events.contains("switch:front"))
        rig.phone.handleVideoSample(frame); rig.encoder.emit()
        XCTAssertTrue(rig.pc.frames.isEmpty)
        XCTAssertFalse(rig.pc.controls.contains { $0.type == "switch_camera_ack" })
        rig.encoder.preparations.removeFirst()(true)
        rig.phone.handleVideoSample(frame)
        await settle()
        XCTAssertEqual(rig.pc.frames.count, 1)
        XCTAssertEqual(rig.trace.events.filter { $0 == "start" }.count, 1)
        let drain = try XCTUnwrap(rig.trace.events.firstIndex(of: "drain"))
        let change = try XCTUnwrap(rig.trace.events.firstIndex(of: "switch:front"))
        let prepare = try XCTUnwrap(rig.trace.events.firstIndex(of: "prepare:1280x720@30"))
        let start = try XCTUnwrap(rig.trace.events.firstIndex(of: "start"))
        XCTAssertLessThan(drain, change); XCTAssertLessThan(change, prepare); XCTAssertLessThan(prepare, start)
    }

    @MainActor func testStopDuringSwitchPreventsAutomaticResume() async throws {
        let rig = try await connected(); rig.encoder.holdPrepare = true
        rig.phone.switchCamera(); rig.phone.stopStreaming()
        rig.encoder.preparations.removeFirst()(true)
        rig.phone.handleVideoSample(try sample())
        XCTAssertEqual(rig.phone.state, .connected)
        XCTAssertFalse(rig.phone.wantsStreaming); XCTAssertTrue(rig.pc.frames.isEmpty)
        XCTAssertFalse(rig.trace.events.contains("start"))
    }

    @MainActor func testSwitchWhileStoppedDoesNotStartVideo() async throws {
        let rig = try await connected(streaming: false)
        rig.phone.switchCamera()
        XCTAssertEqual(rig.phone.state, .connected)
        XCTAssertFalse(rig.phone.wantsStreaming)
        XCTAssertFalse(rig.trace.events.contains("start"))
    }

    @MainActor func testDisconnectDuringDrainIgnoresLateCompletionAndReconnects() async throws {
        let rig = try await connected(); rig.encoder.holdDrain = true
        rig.phone.switchCamera()
        rig.pc.disconnect(); await settle()
        rig.encoder.drains.removeFirst()()
        XCTAssertEqual(rig.phone.state, .discovering)
        XCTAssertFalse(rig.trace.events.contains("switch:front"))
        XCTAssertFalse(rig.pc.controls.contains { $0.type == "switch_camera_ack" })
        rig.encoder.holdDrain = false
        rig.pc.onStateChange?(.connected); await settle()
        try rig.pc.send(.configure(resolution: "1920x1080", fps: 60, codec: "h264", bitrate: 12_000_000))
        await settle(); try rig.pc.send(.startStream); await settle()
        rig.phone.handleVideoSample(try sample())
        await settle()
        XCTAssertEqual(rig.phone.state, .streaming); XCTAssertEqual(rig.pc.frames.count, 1)
    }

    @MainActor func testDisconnectDuringPreparationCannotReviveSession() async throws {
        let rig = try await connected(); rig.encoder.holdPrepare = true
        rig.phone.switchCamera(); rig.pc.disconnect(); await settle()
        rig.encoder.preparations.removeFirst()(true)
        rig.phone.handleVideoSample(try sample())
        XCTAssertEqual(rig.phone.state, .discovering); XCTAssertTrue(rig.pc.frames.isEmpty)
        XCTAssertFalse(rig.pc.controls.contains { $0.type == "switch_camera_ack" })
    }

    @MainActor func testSupersededCameraCallbackDoesNotPrepareEncoderOrAcknowledge() async throws {
        let rig = try await connected(); rig.camera.holdSwitch = true
        rig.phone.switchCamera(cameraID: "front")
        rig.phone.switchCamera(cameraID: "rear")
        rig.camera.switches.removeFirst()()
        XCTAssertFalse(rig.trace.events.contains { $0.hasPrefix("prepare:") })
        rig.camera.switches.removeFirst()()
        XCTAssertEqual(rig.phone.state, .streaming)
        XCTAssertEqual(rig.pc.controls.filter { $0.type == "switch_camera_ack" }.count, 1)
    }

    @MainActor func testEncoderPreparationFailureStopsOutputAndReportsError() async throws {
        let rig = try await connected(); rig.encoder.ready = false
        rig.phone.switchCamera(); rig.phone.handleVideoSample(try sample())
        guard case .error = rig.phone.state else { return XCTFail("Expected encoder error") }
        XCTAssertTrue(rig.pc.frames.isEmpty)
        XCTAssertFalse(rig.pc.controls.contains { $0.type == "switch_camera_ack" })
        XCTAssertTrue(rig.pc.controls.contains { $0.type == "error" })
    }

    @MainActor func testCameraSwitchFailureStopsOutputAndReportsError() async throws {
        let rig = try await connected(); rig.camera.failSwitch = true
        rig.phone.switchCamera(); rig.phone.handleVideoSample(try sample())
        guard case .error = rig.phone.state else { return XCTFail("Expected camera error") }
        XCTAssertTrue(rig.pc.frames.isEmpty)
        XCTAssertFalse(rig.trace.events.contains { $0.hasPrefix("prepare:") })
    }
}
