import SwiftUI
import AVFoundation

/// Local preview; preview rotation does not alter the landscape webcam stream.
struct CameraPreviewView: UIViewRepresentable {

    let session: AVCaptureSession
    var blurred: Bool = false

    func makeUIView(context: Context) -> PreviewUIView {
        let view = PreviewUIView()
        view.videoPreviewLayer.session      = session
        view.videoPreviewLayer.videoGravity = .resizeAspectFill
        view.setBlurred(blurred)
        return view
    }

    func updateUIView(_ uiView: PreviewUIView, context: Context) {
        uiView.setBlurred(blurred)
    }

    static func dismantleUIView(_ uiView: PreviewUIView, coordinator: ()) {
        // Removing the SwiftUI view must detach capture, not merely cover live rendering.
        uiView.videoPreviewLayer.session = nil
    }
}

// MARK: - PreviewUIView

final class PreviewUIView: UIView {

    override class var layerClass: AnyClass { AVCaptureVideoPreviewLayer.self }

    var videoPreviewLayer: AVCaptureVideoPreviewLayer {
        layer as! AVCaptureVideoPreviewLayer
    }

    private var blurView: UIVisualEffectView?

    func setBlurred(_ blurred: Bool) {
        if blurred {
            if blurView == nil {
                let blur = UIBlurEffect(style: .regular)
                let effectView = UIVisualEffectView(effect: blur)
                effectView.autoresizingMask = [.flexibleWidth, .flexibleHeight]
                effectView.frame = bounds
                addSubview(effectView)
                blurView = effectView
            }
        } else {
            blurView?.removeFromSuperview()
            blurView = nil
        }
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        videoPreviewLayer.frame = bounds
        blurView?.frame = bounds
        if let connection = videoPreviewLayer.connection, connection.isVideoOrientationSupported,
           let orientation = window?.windowScene?.interfaceOrientation,
           let videoOrientation = AVCaptureVideoOrientation(rawValue: orientation.rawValue) {
            connection.videoOrientation = videoOrientation
        }
    }
}
