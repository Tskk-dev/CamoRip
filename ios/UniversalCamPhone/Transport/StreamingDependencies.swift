import AVFoundation

// Production adapters remain the real camera, VideoToolbox encoder and USB-only
// transport. Tests inject deterministic adapters into the same coordinator.
protocol PhoneCapture: AnyObject {
    var configuration: CaptureConfiguration? { get }
    var availableCameras: [CameraInfo] { get }
    var currentCameraID: String { get }
    func start()
    func stop()
    func turnTorchOff()
    func configure(width: Int32, height: Int32, fps: Int32,
                   completion: @escaping (Result<CaptureConfiguration, Error>) -> Void)
    func switchCamera(completion: ((CameraInfo?) -> Void)?)
    func switchToCamera(withID id: String, completion: ((CameraInfo?) -> Void)?)
    func readControls(completion: @escaping (CameraControlState) -> Void)
    func applyControls(_ request: CameraControlRequest, allowTorch: Bool,
                       completion: @escaping (CameraControlState) -> Void)
}

protocol PhoneEncoder: AnyObject {
    var onEncodedFrame: ((VideoEncoder.EncodedFrame) -> Void)? { get set }
    var onError: ((Error) -> Void)? { get set }
    func prepare(width: Int32, height: Int32, fps: Int32, bitrate: Int,
                 completion: @escaping (Bool) -> Void)
    func encode(_ sampleBuffer: CMSampleBuffer)
    func requestKeyframe()
    func flush()
    func invalidate(completion: (() -> Void)?)
}

extension PhoneEncoder {
    func invalidate() { invalidate(completion: nil) }
}

protocol PhoneTransport: AnyObject {
    var onControlMessage: ((Data) -> Void)? { get set }
    var onStateChange: ((USBTransport.USBConnectionState) -> Void)? { get set }
    var onVideoBackpressure: (() -> Void)? { get set }
    func startListening()
    func disconnect()
    func sendControl(_ message: ControlMessage)
    func sendVideoFrame(_ frame: VideoEncoder.EncodedFrame)
}

extension CameraSession: PhoneCapture {}
extension VideoEncoder: PhoneEncoder {}
extension USBTransport: PhoneTransport {}
