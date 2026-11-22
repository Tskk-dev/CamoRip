import AVFoundation
import Combine

struct CameraInfo: Codable, Identifiable, Equatable {
    let id: String
    let name: String
    let position: String
    let type: String
    let zoomFactor: Double
    enum CodingKeys: String, CodingKey {
        case id, name, position, type
        case zoomFactor = "zoom_factor"
    }
}

struct CaptureConfiguration: Equatable {
    let width: Int32
    let height: Int32
    let fps: Int32
    var resolution: String { "\(width)x\(height)" }

    func ranksBelow(_ other: CaptureConfiguration, preferFrameRate: Bool) -> Bool {
        if preferFrameRate && fps != other.fps { return fps < other.fps }
        let pixels = Int64(width) * Int64(height)
        let otherPixels = Int64(other.width) * Int64(other.height)
        return pixels == otherPixels ? fps < other.fps : pixels < otherPixels
    }
}

/// All capture configuration and frame callbacks run on sessionQueue. UI updates
/// and completions are delivered on main. A 6s Plus rear camera can capture
/// 1080p60; a front camera negotiates the best format and rate it supports.
final class CameraSession: NSObject, ObservableObject {
    @Published private(set) var isRunning = false
    @Published private(set) var currentCameraID = ""
    @Published private(set) var availableCameras: [CameraInfo] = []
    @Published private(set) var configuration: CaptureConfiguration?
    let captureSession = AVCaptureSession()
    var onVideoSampleBuffer: ((CMSampleBuffer) -> Void)?
    var onError: ((String) -> Void)?
    /// Capture paused by the system (another app, a call, media services reset). Main queue.
    var onInterrupted: ((String) -> Void)?
    /// Capture is running again after an interruption. Main queue.
    var onInterruptionEnded: (() -> Void)?

    let sessionQueue = DispatchQueue(label: "com.camorip.camera.session", qos: .userInteractive)
    private let videoOutput = AVCaptureVideoDataOutput()
    private var configured = false
    private var requestedWidth: Int32 = 1920
    private var requestedHeight: Int32 = 1080
    private var requestedFPS: Int32 = 60
    // sessionQueue only: the active format's frame rate, and the rate applied after any
    // system-pressure limit.
    private var formatFPS: Int32 = 0
    private var appliedFPS: Int32 = 0
    private var pressureObservation: NSKeyValueObservation?
    private static let pressureLimitedFPS: Int32 = 30

    override init() {
        super.init()
        NotificationCenter.default.addObserver(self, selector: #selector(runtimeError(_:)),
            name: .AVCaptureSessionRuntimeError, object: captureSession)
        NotificationCenter.default.addObserver(self, selector: #selector(interrupted(_:)),
            name: .AVCaptureSessionWasInterrupted, object: captureSession)
        NotificationCenter.default.addObserver(self, selector: #selector(interruptionEnded(_:)),
            name: .AVCaptureSessionInterruptionEnded, object: captureSession)
    }

    deinit {
        pressureObservation?.invalidate()
        NotificationCenter.default.removeObserver(self)
    }

    func start() {
        sessionQueue.async { [weak self] in
            guard let self else { return }
            guard AVCaptureDevice.authorizationStatus(for: .video) == .authorized else {
                self.report("Camera permission is denied. Enable Camera in Settings.")
                return
            }
            do {
                if !self.configured { try self.setUp() }
                if !self.captureSession.isRunning { self.captureSession.startRunning() }
                let running = self.captureSession.isRunning
                DispatchQueue.main.async { self.isRunning = running }
                if !running { self.report("The camera could not start. Close other camera apps and retry.") }
            } catch { self.report(error.localizedDescription) }
        }
    }

    func stop() {
        turnTorchOff()
        sessionQueue.async { [weak self] in
            guard let self else { return }
            if self.captureSession.isRunning { self.captureSession.stopRunning() }
            DispatchQueue.main.async { self.isRunning = false }
        }
    }

    func pauseForScanner(completion: @escaping () -> Void) {
        stop()
        sessionQueue.async { DispatchQueue.main.async(execute: completion) }
    }

    func configure(width: Int32, height: Int32, fps: Int32,
                   completion: @escaping (Result<CaptureConfiguration, Error>) -> Void) {
        sessionQueue.async { [weak self] in
            guard let self else { return }
            do {
                // Stop outside the configuration transaction. This also drains
                // capture delivery on sessionQueue before replacing the format.
                DiagnosticLog.write("Capture reconfiguration: stopping session")
                if self.captureSession.isRunning { self.captureSession.stopRunning() }
                DispatchQueue.main.async { self.isRunning = false }
                if !self.configured { try self.setUp() }
                self.requestedWidth = width
                self.requestedHeight = height
                self.requestedFPS = fps
                guard let device = self.currentDevice else { throw self.failure("No camera is available.") }
                self.captureSession.beginConfiguration()
                let result: Result<CaptureConfiguration, Error>
                do { result = .success(try self.applyFormat(to: device)) }
                catch { result = .failure(error) }
                self.captureSession.commitConfiguration()
                self.setOutputOrientation()
                DiagnosticLog.write("Capture reconfiguration: format committed")
                DispatchQueue.main.async {
                    if case .success(let config) = result { self.configuration = config }
                    completion(result)
                }
            } catch { DispatchQueue.main.async { completion(.failure(error)) } }
        }
    }

    func switchCamera(completion: ((CameraInfo?) -> Void)? = nil) {
        sessionQueue.async { [weak self] in
            guard let self else { return }
            let position: AVCaptureDevice.Position = self.currentDevice?.position == .back ? .front : .back
            guard let device = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: position) else {
                DispatchQueue.main.async { completion?(nil) }; return
            }
            self.changeCamera(to: device, completion: completion)
        }
    }

    func switchToCamera(withID id: String, completion: ((CameraInfo?) -> Void)? = nil) {
        sessionQueue.async { [weak self] in
            guard let self else { return }
            guard let device = AVCaptureDevice(uniqueID: id), device.hasMediaType(.video) else {
                DispatchQueue.main.async { completion?(nil) }; return
            }
            self.changeCamera(to: device, completion: completion)
        }
    }

    var currentDevice: AVCaptureDevice? {
        captureSession.inputs.compactMap { $0 as? AVCaptureDeviceInput }.first?.device
    }

    private func setUp() throws {
        guard AVCaptureDevice.authorizationStatus(for: .video) == .authorized else {
            throw failure("Camera permission is denied. Enable Camera in Settings.")
        }
        let cameras = AVCaptureDevice.DiscoverySession(deviceTypes: [.builtInWideAngleCamera],
            mediaType: .video, position: .unspecified).devices
        guard let device = cameras.first(where: { $0.position == .back }) ?? cameras.first else {
            throw failure("No camera is available.")
        }
        let input = try AVCaptureDeviceInput(device: device)
        captureSession.beginConfiguration()
        defer { captureSession.commitConfiguration() }
        guard captureSession.canAddInput(input), captureSession.canAddOutput(videoOutput) else {
            throw failure("Could not configure camera capture.")
        }
        captureSession.addInput(input)
        videoOutput.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String:
            kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange]
        videoOutput.alwaysDiscardsLateVideoFrames = true
        videoOutput.setSampleBufferDelegate(self, queue: sessionQueue)
        captureSession.addOutput(videoOutput)
        let config: CaptureConfiguration
        do { config = try applyFormat(to: device) }
        catch {
            captureSession.removeOutput(videoOutput)
            captureSession.removeInput(input)
            throw error
        }
        configured = true
        observePressure(of: device)
        // Set after commit as well; configuration commits can reset connection state.
        sessionQueue.async { [weak self] in self?.setOutputOrientation() }
        DispatchQueue.main.async {
            self.availableCameras = cameras.map(self.info)
            self.currentCameraID = device.uniqueID
            self.configuration = config
        }
    }

    private func applyFormat(to device: AVCaptureDevice) throws -> CaptureConfiguration {
        let formats = device.formats.filter { format in
            let d = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
            return d.width <= requestedWidth && d.height <= requestedHeight && d.width >= 640
                && abs(Double(d.width) / Double(d.height) - 16.0 / 9.0) < 0.03
        }
        let supportsRequestedRate = formats.filter { format in
            format.videoSupportedFrameRateRanges.contains {
                $0.minFrameRate <= Double(requestedFPS) && $0.maxFrameRate >= Double(requestedFPS)
            }
        }
        let candidates = (supportsRequestedRate.isEmpty ? formats : supportsRequestedRate).compactMap { format -> (AVCaptureDevice.Format, Int32)? in
            let fps: Int32
            if !supportsRequestedRate.isEmpty {
                fps = requestedFPS
            } else {
                guard let range = format.videoSupportedFrameRateRanges.max(by: { $0.maxFrameRate < $1.maxFrameRate }) else { return nil }
                fps = min(requestedFPS, Int32(range.maxFrameRate.rounded(.down)))
                guard fps > 0, range.minFrameRate <= Double(fps) else { return nil }
            }
            return (format, fps)
        }
        guard let (format, actualFPS) = candidates.max(by: { lhs, rhs in
            let a = CMVideoFormatDescriptionGetDimensions(lhs.0.formatDescription)
            let b = CMVideoFormatDescriptionGetDimensions(rhs.0.formatDescription)
            return CaptureConfiguration(width: a.width, height: a.height, fps: lhs.1).ranksBelow(
                CaptureConfiguration(width: b.width, height: b.height, fps: rhs.1),
                preferFrameRate: requestedFPS > 60)
        }) else { throw failure("This camera does not support a 16:9 format at up to \(requestedFPS) fps.") }
        try device.lockForConfiguration()
        defer { device.unlockForConfiguration() }
        if captureSession.canSetSessionPreset(.inputPriority) { captureSession.sessionPreset = .inputPriority }
        // Do not carry a locked 60 fps duration into a 30 fps-only format.
        device.activeVideoMinFrameDuration = .invalid
        device.activeVideoMaxFrameDuration = .invalid
        device.activeFormat = format
        formatFPS = actualFPS
        // Under thermal/system pressure start at the reduced rate rather than at 60 fps.
        let capturedFPS = pressureAdjustedFPS(for: device)
        let duration = CMTime(value: 1, timescale: capturedFPS)
        device.activeVideoMinFrameDuration = duration
        device.activeVideoMaxFrameDuration = duration
        appliedFPS = capturedFPS
        let d = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
        // Report the format's rate: the encoder and Windows keep the nominal mode, and the
        // Windows camera repeats frames if capture is temporarily slower.
        return CaptureConfiguration(width: d.width, height: d.height, fps: actualFPS)
    }

    private func changeCamera(to device: AVCaptureDevice, completion: ((CameraInfo?) -> Void)?) {
        do {
            DiagnosticLog.write("Camera switch: begin to=\(device.position == .front ? "front" : "back")")
            if captureSession.isRunning { captureSession.stopRunning() }
            DispatchQueue.main.async { self.isRunning = false }
            if !configured { try setUp() }
            let replacement = try AVCaptureDeviceInput(device: device)
            captureSession.beginConfiguration()
            let previous = captureSession.inputs.compactMap { $0 as? AVCaptureDeviceInput }.first
            if let previous { disableTorch(previous.device); captureSession.removeInput(previous) }
            guard captureSession.canAddInput(replacement) else {
                if let previous { captureSession.addInput(previous) }
                captureSession.commitConfiguration()
                throw failure("Could not switch to this camera.")
            }
            captureSession.addInput(replacement)
            DiagnosticLog.write("Camera switch: replacement input added; selecting format")
            let config: CaptureConfiguration
            do { config = try applyFormat(to: device) }
            catch {
                captureSession.removeInput(replacement)
                if let previous, captureSession.canAddInput(previous) { captureSession.addInput(previous) }
                captureSession.commitConfiguration()
                throw error
            }
            captureSession.commitConfiguration()
            observePressure(of: device)
            DiagnosticLog.write("Camera switch: format committed \(config.width)x\(config.height) @ \(config.fps)")
            setOutputOrientation()
            DiagnosticLog.write("Camera switch: output connection configured")
            DispatchQueue.main.async {
                self.currentCameraID = device.uniqueID
                self.configuration = config
                completion?(self.info(device))
            }
        } catch {
            DiagnosticLog.write("Camera switch failed: \(error.localizedDescription)")
            report(error.localizedDescription)
            DispatchQueue.main.async { completion?(nil) }
        }
    }

    private func setOutputOrientation() {
        guard let connection = videoOutput.connection(with: .video) else { return }
        if connection.isVideoOrientationSupported { connection.videoOrientation = .landscapeRight }
        disableCaptureMirroring(on: connection)
    }

    private func info(_ device: AVCaptureDevice) -> CameraInfo {
        CameraInfo(id: device.uniqueID, name: device.localizedName,
            position: device.position == .front ? "front" : "back", type: "wide_angle", zoomFactor: 1)
    }

    private func failure(_ message: String) -> NSError {
        NSError(domain: "CamoRip.Camera", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
    }

    private func report(_ message: String) { DispatchQueue.main.async { self.onError?(message) } }

    @objc private func runtimeError(_ notification: Notification) {
        let error = notification.userInfo?[AVCaptureSessionErrorKey] as? AVError
        let message = error?.localizedDescription ?? "Camera capture was interrupted."
        DiagnosticLog.write("Capture runtime error: \(message)")
        DispatchQueue.main.async { self.onInterrupted?(message) }
        // Apple's guidance: restart the session after a runtime error (for example when
        // media services were reset). Resume streaming once it is running again.
        sessionQueue.asyncAfter(deadline: .now() + 1) { [weak self] in
            guard let self, AVCaptureDevice.authorizationStatus(for: .video) == .authorized else { return }
            if !self.captureSession.isRunning { self.captureSession.startRunning() }
            let running = self.captureSession.isRunning
            DispatchQueue.main.async {
                self.isRunning = running
                if running { self.onInterruptionEnded?() }
                else { self.onError?("The camera could not restart. Close other camera apps and retry.") }
            }
        }
    }

    @objc private func interrupted(_ notification: Notification) {
        var reason = "unknown"
        if let raw = notification.userInfo?[AVCaptureSessionInterruptionReasonKey] as? Int,
           let value = AVCaptureSession.InterruptionReason(rawValue: raw) {
            switch value {
            case .videoDeviceNotAvailableInBackground: reason = "background"
            case .audioDeviceInUseByAnotherClient: reason = "audio in use"
            case .videoDeviceInUseByAnotherClient: reason = "camera in use by another app"
            case .videoDeviceNotAvailableWithMultipleForegroundApps: reason = "multiple foreground apps"
            case .videoDeviceNotAvailableDueToSystemPressure: reason = "system pressure (too hot)"
            default: reason = "code \(raw)"
            }
        }
        DiagnosticLog.write("Capture interrupted: reason=\(reason)")
        let message = "Camera capture is unavailable while another app is using it or the phone is locked."
        DispatchQueue.main.async { self.onInterrupted?(message) }
    }

    @objc private func interruptionEnded(_ notification: Notification) {
        DiagnosticLog.write("Capture interruption ended")
        DispatchQueue.main.async { self.onInterruptionEnded?() }
    }

    // MARK: System pressure

    /// Apple recommends lowering the frame rate when the camera reports serious system
    /// pressure. Otherwise iOS throttles capture unpredictably and, at the shutdown level,
    /// interrupts it. This is common in long wired sessions: charging plus 60 fps capture
    /// and H.264 encoding heats the phone.
    private func pressureAdjustedFPS(for device: AVCaptureDevice) -> Int32 {
        let level = device.systemPressureState.level
        guard level == .serious || level == .critical || level == .shutdown,
              formatFPS > Self.pressureLimitedFPS,
              device.activeFormat.videoSupportedFrameRateRanges.contains(where: {
                  $0.minFrameRate <= Double(Self.pressureLimitedFPS) && $0.maxFrameRate >= Double(Self.pressureLimitedFPS)
              }) else { return formatFPS }
        return Self.pressureLimitedFPS
    }

    private func observePressure(of device: AVCaptureDevice) {
        pressureObservation?.invalidate()
        pressureObservation = device.observe(\.systemPressureState, options: [.new]) { [weak self] device, _ in
            self?.sessionQueue.async { self?.applyPressureLimit(to: device) }
        }
    }

    private func applyPressureLimit(to device: AVCaptureDevice) {
        guard configured, device === currentDevice, formatFPS > 0 else { return }
        let target = pressureAdjustedFPS(for: device)
        guard target != appliedFPS else { return }
        do {
            try device.lockForConfiguration()
            defer { device.unlockForConfiguration() }
            let duration = CMTime(value: 1, timescale: target)
            device.activeVideoMinFrameDuration = .invalid
            device.activeVideoMaxFrameDuration = .invalid
            device.activeVideoMinFrameDuration = duration
            device.activeVideoMaxFrameDuration = duration
            appliedFPS = target
            DiagnosticLog.write("System pressure \(device.systemPressureState.level.rawValue): capture at \(target) fps (format \(formatFPS) fps)")
        } catch {
            DiagnosticLog.write("System pressure frame-rate change failed: \(error.localizedDescription)")
        }
    }
}

extension CameraSession: AVCaptureVideoDataOutputSampleBufferDelegate {
    func captureOutput(_ output: AVCaptureOutput, didDrop sampleBuffer: CMSampleBuffer,
                       from connection: AVCaptureConnection) {
        DiagnosticLog.record(.captureDropped)
    }

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer,
                       from connection: AVCaptureConnection) {
        DiagnosticLog.record(.captured)
        onVideoSampleBuffer?(sampleBuffer)
    }
}
