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

    let sessionQueue = DispatchQueue(label: "com.camorip.camera.session", qos: .userInteractive)
    private let videoOutput = AVCaptureVideoDataOutput()
    private var configured = false
    private var requestedWidth: Int32 = 1920
    private var requestedHeight: Int32 = 1080
    private var requestedFPS: Int32 = 60

    override init() {
        super.init()
        NotificationCenter.default.addObserver(self, selector: #selector(runtimeError(_:)),
            name: .AVCaptureSessionRuntimeError, object: captureSession)
        NotificationCenter.default.addObserver(self, selector: #selector(interrupted(_:)),
            name: .AVCaptureSessionWasInterrupted, object: captureSession)
    }

    deinit { NotificationCenter.default.removeObserver(self) }

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
            return a.width * a.height == b.width * b.height ? lhs.1 < rhs.1 : a.width * a.height < b.width * b.height
        }) else { throw failure("This camera does not support a 16:9 format at up to \(requestedFPS) fps.") }
        try device.lockForConfiguration()
        defer { device.unlockForConfiguration() }
        if captureSession.canSetSessionPreset(.inputPriority) { captureSession.sessionPreset = .inputPriority }
        // Do not carry a locked 60 fps duration into a 30 fps-only format.
        device.activeVideoMinFrameDuration = .invalid
        device.activeVideoMaxFrameDuration = .invalid
        device.activeFormat = format
        let duration = CMTime(value: 1, timescale: actualFPS)
        device.activeVideoMinFrameDuration = duration
        device.activeVideoMaxFrameDuration = duration
        let d = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
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
        let message = (notification.userInfo?[AVCaptureSessionErrorKey] as? Error)?.localizedDescription
        report(message ?? "Camera capture was interrupted. Stop and start the webcam to retry.")
    }

    @objc private func interrupted(_ notification: Notification) {
        report("Camera capture is unavailable while another app is using it or the phone is locked.")
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
