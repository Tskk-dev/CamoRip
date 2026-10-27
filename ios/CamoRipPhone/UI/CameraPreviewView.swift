import SwiftUI
import AVFoundation

struct CameraPreviewView: UIViewRepresentable {
    let session: AVCaptureSession
    var fixedLandscape = false

    func makeUIView(context: Context) -> CameraPreviewSurface {
        let surface = CameraPreviewSurface()
        surface.preview.session = session
        surface.fixedLandscape = fixedLandscape
        return surface
    }

    func updateUIView(_ surface: CameraPreviewSurface, context: Context) {
        if surface.preview.session !== session { surface.preview.session = session }
        surface.fixedLandscape = fixedLandscape
        surface.setNeedsLayout()
    }

    static func dismantleUIView(_ surface: CameraPreviewSurface, coordinator: ()) {
        surface.preview.session = nil
    }
}

final class CameraPreviewSurface: UIView {
    let preview = AVCaptureVideoPreviewLayer()
    var fixedLandscape = false

    override init(frame: CGRect) {
        super.init(frame: frame)
        preview.videoGravity = .resizeAspectFill
        layer.addSublayer(preview)
    }

    required init?(coder: NSCoder) { fatalError("Not supported") }

    override func layoutSubviews() {
        super.layoutSubviews()
        preview.frame = bounds
        preview.videoGravity = fixedLandscape ? .resizeAspect : .resizeAspectFill
        if let connection = preview.connection {
            disableCaptureMirroring(on: connection)
            if fixedLandscape && connection.isVideoOrientationSupported {
                connection.videoOrientation = .landscapeRight
                return
            }
        }
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
