import SwiftUI
import AVFoundation

struct CameraPreviewView: UIViewRepresentable {
    let session: AVCaptureSession

    func makeUIView(context: Context) -> CameraPreviewSurface {
        let surface = CameraPreviewSurface()
        surface.preview.session = session
        return surface
    }

    func updateUIView(_ surface: CameraPreviewSurface, context: Context) {
        if surface.preview.session !== session { surface.preview.session = session }
    }

    static func dismantleUIView(_ surface: CameraPreviewSurface, coordinator: ()) {
        surface.preview.session = nil
    }
}

final class CameraPreviewSurface: UIView {
    let preview = AVCaptureVideoPreviewLayer()

    override init(frame: CGRect) {
        super.init(frame: frame)
        preview.videoGravity = .resizeAspectFill
        layer.addSublayer(preview)
    }

    required init?(coder: NSCoder) { fatalError("Not supported") }

    override func layoutSubviews() {
        super.layoutSubviews()
        preview.frame = bounds
        guard let connection = preview.connection, connection.isVideoOrientationSupported,
              let orientation = window?.windowScene?.interfaceOrientation else { return }
        switch orientation {
        case .portrait: connection.videoOrientation = .portrait
        case .portraitUpsideDown: connection.videoOrientation = .portraitUpsideDown
        case .landscapeLeft: connection.videoOrientation = .landscapeLeft
        case .landscapeRight: connection.videoOrientation = .landscapeRight
        default: break
        }
    }
}
