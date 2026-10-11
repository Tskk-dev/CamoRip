import Foundation
import Combine
import AVFoundation
import UIKit

/// UI state and protocol decisions run on the main queue. Capture, encoding,
/// and network IO each have their own serial queue.
final class ConnectionManager: ObservableObject {
    enum State: Equatable { case idle, discovering, connected, streaming, error(String) }
    enum TransportType: String { case usb = "USB" }
    @Published private(set) var state: State = .idle
    @Published private(set) var latencyMs = 0
    @Published private(set) var activeTransport: TransportType?
    @Published private(set) var usbListening = false
    @Published private(set) var wantsStreaming = false
    @Published private(set) var formatLabel = "Rear camera: up to 1080p60"
    weak var cameraSession: CameraSession?

    private let transport = USBTransport()
    private let encoder = VideoEncoder()
    private var negotiated = false
    private var configuring = false
    private var startRequested = false
    private var pendingStartNotifiesPeer = false
    private var handshakeGeneration = 0
    private var operationGeneration = 0
    private var bitrate = 8_000_000
    private var pingTimer: Timer?
    private var lastPong = Date()
    private var backgroundObserver: NSObjectProtocol?

    init() {
        transport.onStateChange = { [weak self] state in
            DispatchQueue.main.async { self?.transportChanged(state) }
        }
        transport.onControlMessage = { [weak self] data in
            DispatchQueue.main.async { self?.receive(data) }
        }
        transport.onVideoBackpressure = { [weak self] in self?.encoder.requestKeyframe() }
        encoder.onEncodedFrame = { [weak self] frame in
            DispatchQueue.main.async {
                guard let self, self.state == .streaming else { return }
                self.transport.sendVideoFrame(frame)
            }
        }
        encoder.onError = { [weak self] error in
            DispatchQueue.main.async { self?.fail(code: "encoder_failed", message: error.localizedDescription) }
        }
        backgroundObserver = NotificationCenter.default.addObserver(
            forName: UIApplication.didEnterBackgroundNotification, object: nil, queue: .main) { [weak self] _ in
                self?.stopStreaming()
                self?.cameraSession?.stop()
            }
    }

    deinit {
        pingTimer?.invalidate()
        if let backgroundObserver { NotificationCenter.default.removeObserver(backgroundObserver) }
    }

    func startDiscovery() {
        if activeTransport == nil { state = .discovering }
        transport.startListening()
    }

    func disconnect() {
        stopStreaming()
        transport.disconnect()
    }

    /// Called by the phone's Start button or a Windows start_stream request.
    /// The callback waits for capture configuration before enabling frame delivery.
    func startStreaming(notifyPeer: Bool = true) {
        guard activeTransport == .usb else { return }
        guard UIApplication.shared.applicationState == .active else {
            fail(code: "app_in_background", message: "Open UniversalCam on the iPhone to start video.")
            return
        }
        guard AVCaptureDevice.authorizationStatus(for: .video) == .authorized else {
            fail(code: "camera_denied", message: "Enable Camera permission in iPhone Settings."); return
        }
        wantsStreaming = true
        if configuring || !negotiated {
            startRequested = true
            pendingStartNotifiesPeer = pendingStartNotifiesPeer || notifyPeer
            return
        }
        guard state != .streaming else {
            if notifyPeer { transport.sendControl(.startStream) }
            return
        }
        cameraSession?.start()
        encoder.requestKeyframe()
        UIApplication.shared.isIdleTimerDisabled = true
        state = .streaming
        if notifyPeer { transport.sendControl(.startStream) }
    }

    func stopStreaming(notifyPeer: Bool = true) {
        startRequested = false
        pendingStartNotifiesPeer = false
        wantsStreaming = false
        // Stop accepting raw frames before flushing the old compression session.
        if activeTransport == .usb { state = .connected }
        UIApplication.shared.isIdleTimerDisabled = false
        encoder.flush()
        if notifyPeer { transport.sendControl(.stopStream) }
    }

    func handleVideoSample(_ sample: CMSampleBuffer) {
        DispatchQueue.main.async { [weak self] in
            guard let self, self.state == .streaming else { return }
            self.encoder.encode(sample)
        }
    }

    func cameraFailed(_ message: String) { fail(code: "camera_unavailable", message: message) }

    func switchCamera(cameraID: String? = nil) {
        guard let camera = cameraSession else { return }
        let wasStreaming = state == .streaming
        if wasStreaming { state = .connected }
        configuring = true
        operationGeneration += 1
        let generation = handshakeGeneration
        let operation = operationGeneration
        let completion: (CameraInfo?) -> Void = { [weak self] info in
            guard let self, generation == self.handshakeGeneration,
                  operation == self.operationGeneration else { return }
            self.configuring = false
            guard let info, let config = camera.configuration else {
                self.fail(code: "camera_switch_failed", message: "Could not switch camera."); return
            }
            self.prepareEncoder(config)
            if self.activeTransport != nil {
                self.transport.sendControl(.configureAck(resolution: config.resolution, fps: Int(config.fps)))
                self.transport.sendControl(.switchCameraAck(cameraID: info.id, cameraName: info.name))
            }
            if self.wantsStreaming || self.startRequested {
                let notify = self.pendingStartNotifiesPeer
                self.startRequested = false
                self.pendingStartNotifiesPeer = false
                self.startStreaming(notifyPeer: notify)
            }
        }
        if let cameraID { camera.switchToCamera(withID: cameraID, completion: completion) }
        else { camera.switchCamera(completion: completion) }
    }

    private func transportChanged(_ state: USBTransport.USBConnectionState) {
        switch state {
        case .listening:
            usbListening = true
            if activeTransport == nil { self.state = .discovering }
        case .connected:
            resetSession()
            activeTransport = .usb
            self.state = .connected
            // Windows initiates the existing welcome -> hello -> configure handshake.
            startHeartbeat()
        case .disconnected:
            resetSession()
            activeTransport = nil
            self.state = .discovering
        case .failed(let message):
            resetSession()
            activeTransport = nil
            self.state = .error(message)
            // Existing listener accepts another USB connection after a cable failure.
            transport.startListening()
        }
    }

    private func resetSession() {
        handshakeGeneration += 1
        operationGeneration += 1
        negotiated = false
        configuring = false
        startRequested = false
        pendingStartNotifiesPeer = false
        wantsStreaming = false
        UIApplication.shared.isIdleTimerDisabled = false
        encoder.invalidate()
        pingTimer?.invalidate()
        pingTimer = nil
        latencyMs = 0
    }

    private func receive(_ data: Data) {
        guard let message = try? JSONDecoder().decode(ControlMessage.self, from: data) else {
            fail(code: "invalid_control", message: "Windows sent an invalid control message."); return
        }
        switch message {
        case .welcome:
            transport.sendControl(.hello(deviceName: UIDevice.current.name, capabilities: ["h264", "usb", "video_only"]))
            transport.sendControl(.orientationChanged(orientation: "landscape"))
            sendCameras()
        case .configure(let resolution, let fps, let codec, let requestedBitrate):
            let dimensions = resolution.split(separator: "x")
            guard codec.lowercased() == "h264", dimensions.count == 2,
                  let width = Int32(dimensions[0]), let height = Int32(dimensions[1]),
                  (640...1920).contains(width), (360...1080).contains(height),
                  (15...60).contains(fps), (500_000...20_000_000).contains(requestedBitrate),
                  let camera = cameraSession else {
                fail(code: "unsupported_configuration", message: "Use H.264, up to 1920x1080 at 15–60 fps."); return
            }
            stopStreaming(notifyPeer: false)
            configuring = true
            operationGeneration += 1
            negotiated = false
            bitrate = requestedBitrate
            let generation = handshakeGeneration
            let operation = operationGeneration
            camera.configure(width: width, height: height, fps: Int32(fps)) { [weak self] result in
                guard let self, generation == self.handshakeGeneration,
                      operation == self.operationGeneration else { return }
                self.configuring = false
                switch result {
                case .success(let config):
                    self.prepareEncoder(config)
                    self.negotiated = true
                    self.state = .connected
                    self.transport.sendControl(.configureAck(resolution: config.resolution, fps: Int(config.fps)))
                    self.sendCameras()
                    if self.startRequested {
                        let notify = self.pendingStartNotifiesPeer
                        self.startRequested = false
                        self.pendingStartNotifiesPeer = false
                        self.startStreaming(notifyPeer: notify)
                    }
                case .failure(let error):
                    self.fail(code: "capture_configuration_failed", message: error.localizedDescription)
                }
            }
        // Windows interprets these replies as accepted state acknowledgements;
        // its incoming handlers update UI only and do not echo another request.
        case .startStream: startStreaming()
        case .stopStream: stopStreaming()
        case .switchCamera(let id): switchCamera(cameraID: id)
        case .listCameras: sendCameras()
        case .ping(let ts): transport.sendControl(.pong(ts: ts))
        case .pong(let ts):
            lastPong = Date()
            latencyMs = max(0, Int((Date().timeIntervalSince1970 * 1000 - Double(ts)) / 2))
        case .error(_, let message):
            stopStreaming(notifyPeer: false)
            state = .error(message)
        default: break
        }
    }

    private func prepareEncoder(_ config: CaptureConfiguration) {
        encoder.prepare(width: config.width, height: config.height, fps: config.fps, bitrate: bitrate)
        formatLabel = "\(config.width) × \(config.height) · \(config.fps) fps"
    }

    private func sendCameras() {
        guard let camera = cameraSession else { return }
        transport.sendControl(.availableCameras(cameras: camera.availableCameras, currentCameraID: camera.currentCameraID))
    }

    private func fail(code: String, message: String) {
        // An asynchronous capture result must not revive a session after a
        // permission, encoder, or camera interruption error has stopped it.
        operationGeneration += 1
        configuring = false
        stopStreaming(notifyPeer: false)
        state = .error(message)
        transport.sendControl(.error(code: code, message: message))
    }

    private func startHeartbeat() {
        lastPong = Date()
        pingTimer?.invalidate()
        pingTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            guard let self else { return }
            if Date().timeIntervalSince(self.lastPong) > 10 { self.transport.disconnect(); return }
            self.transport.sendControl(.ping(ts: Int64(Date().timeIntervalSince1970 * 1000)))
        }
    }
}
// MARK: - Control Message Types

enum ControlMessage: Codable {
    case hello(deviceName: String, capabilities: [String])
    case welcome
    case configure(resolution: String, fps: Int, codec: String, bitrate: Int)
    case configureAck(resolution: String, fps: Int)
    case startStream
    case stopStream
    case switchCamera(cameraID: String?)
    case switchCameraAck(cameraID: String, cameraName: String)
    case listCameras
    case availableCameras(cameras: [CameraInfo], currentCameraID: String)
    case ping(ts: Int64)
    case pong(ts: Int64)
    case error(code: String, message: String)
    /// iOS → PC: device orientation changed. orientation = "portrait" | "landscape"
    case orientationChanged(orientation: String)

    private enum CodingKeys: String, CodingKey {
        case type, deviceName, capabilities, resolution, fps, codec, bitrate, ts
        case cameraID, cameraName, cameras, currentCameraID
        case orientation
        case code, message
    }

    var type: String {
        switch self {
        case .hello:               return "hello"
        case .welcome:             return "welcome"
        case .configure:           return "configure"
        case .configureAck:        return "configure_ack"
        case .startStream:         return "start_stream"
        case .stopStream:          return "stop_stream"
        case .switchCamera:        return "switch_camera"
        case .switchCameraAck:     return "switch_camera_ack"
        case .listCameras:         return "list_cameras"
        case .availableCameras:    return "available_cameras"
        case .ping:                return "ping"
        case .pong:                return "pong"
        case .orientationChanged:  return "orientation_changed"
        case .error:               return "error"
        }
    }

    // MARK: - Codable Implementation

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(type, forKey: .type)

        switch self {
        case .hello(let deviceName, let capabilities):
            try container.encode(deviceName, forKey: .deviceName)
            try container.encode(capabilities, forKey: .capabilities)
        case .welcome:
            break
        case .configure(let resolution, let fps, let codec, let bitrate):
            try container.encode(resolution, forKey: .resolution)
            try container.encode(fps, forKey: .fps)
            try container.encode(codec, forKey: .codec)
            try container.encode(bitrate, forKey: .bitrate)
        case .configureAck(let resolution, let fps):
            try container.encode(resolution, forKey: .resolution)
            try container.encode(fps, forKey: .fps)
        case .startStream, .stopStream, .listCameras:
            break
        case .switchCamera(let cameraID):
            try container.encodeIfPresent(cameraID, forKey: .cameraID)
        case .switchCameraAck(let cameraID, let cameraName):
            try container.encode(cameraID, forKey: .cameraID)
            try container.encode(cameraName, forKey: .cameraName)
        case .availableCameras(let cameras, let currentCameraID):
            try container.encode(cameras, forKey: .cameras)
            try container.encode(currentCameraID, forKey: .currentCameraID)
        case .ping(let ts), .pong(let ts):
            try container.encode(ts, forKey: .ts)
        case .orientationChanged(let orientation):
            try container.encode(orientation, forKey: .orientation)
        case .error(let code, let message):
            try container.encode(code, forKey: .code)
            try container.encode(message, forKey: .message)
        }
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let type = try container.decode(String.self, forKey: .type)

        switch type {
        case "hello":
            let deviceName = try container.decode(String.self, forKey: .deviceName)
            let capabilities = try container.decode([String].self, forKey: .capabilities)
            self = .hello(deviceName: deviceName, capabilities: capabilities)
        case "welcome":
            self = .welcome
        case "configure":
            let resolution = try container.decode(String.self, forKey: .resolution)
            let fps = try container.decode(Int.self, forKey: .fps)
            let codec = try container.decode(String.self, forKey: .codec)
            let bitrate = try container.decode(Int.self, forKey: .bitrate)
            self = .configure(resolution: resolution, fps: fps, codec: codec, bitrate: bitrate)
        case "configure_ack":
            let resolution = try container.decode(String.self, forKey: .resolution)
            let fps = try container.decode(Int.self, forKey: .fps)
            self = .configureAck(resolution: resolution, fps: fps)
        case "start_stream":
            self = .startStream
        case "stop_stream":
            self = .stopStream
        case "switch_camera":
            let cameraID = try container.decodeIfPresent(String.self, forKey: .cameraID)
            self = .switchCamera(cameraID: cameraID)
        case "switch_camera_ack":
            let cameraID = try container.decode(String.self, forKey: .cameraID)
            let cameraName = try container.decode(String.self, forKey: .cameraName)
            self = .switchCameraAck(cameraID: cameraID, cameraName: cameraName)
        case "list_cameras":
            self = .listCameras
        case "available_cameras":
            let cameras = try container.decode([CameraInfo].self, forKey: .cameras)
            let currentCameraID = try container.decode(String.self, forKey: .currentCameraID)
            self = .availableCameras(cameras: cameras, currentCameraID: currentCameraID)
        case "ping":
            let ts = try container.decode(Int64.self, forKey: .ts)
            self = .ping(ts: ts)
        case "pong":
            let ts = try container.decode(Int64.self, forKey: .ts)
            self = .pong(ts: ts)
        case "orientation_changed":
            let orientation = try container.decode(String.self, forKey: .orientation)
            self = .orientationChanged(orientation: orientation)
        case "error":
            self = .error(code: try container.decode(String.self, forKey: .code),
                          message: try container.decode(String.self, forKey: .message))
        default:
            throw DecodingError.dataCorruptedError(
                forKey: .type,
                in: container,
                debugDescription: "Unknown message type: \(type)"
            )
        }
    }
}

