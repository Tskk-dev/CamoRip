import SwiftUI
import AVFoundation

struct WirelessScannerView: UIViewControllerRepresentable {
    var onScan: (String) -> Void
    var onCancel: () -> Void
    func makeUIViewController(context: Context) -> QRScannerController {
        QRScannerController(onScan: onScan, onCancel: onCancel)
    }
    func updateUIViewController(_ controller: QRScannerController, context: Context) {}
    static func dismantleUIViewController(_ controller: QRScannerController, coordinator: ()) { controller.stop() }
}

final class QRScannerController: UIViewController, AVCaptureMetadataOutputObjectsDelegate {
    private let session = AVCaptureSession()
    private let queue = DispatchQueue(label: "com.camorip.qr-scanner")
    private var preview: AVCaptureVideoPreviewLayer?
    private var finished = false
    private let onScan: (String) -> Void
    private let onCancel: () -> Void
    init(onScan: @escaping (String) -> Void, onCancel: @escaping () -> Void) {
        self.onScan = onScan; self.onCancel = onCancel
        super.init(nibName: nil, bundle: nil)
    }
    required init?(coder: NSCoder) { fatalError("Not supported") }
    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        let layer = AVCaptureVideoPreviewLayer(session: session)
        layer.videoGravity = .resizeAspectFill; preview = layer
        view.layer.addSublayer(layer)
        let instructions = UILabel()
        instructions.text = "Scan the pairing QR code in CamoRip on your PC."
        instructions.textColor = .white; instructions.numberOfLines = 0
        instructions.textAlignment = .center; instructions.translatesAutoresizingMaskIntoConstraints = false
        instructions.backgroundColor = UIColor.black.withAlphaComponent(0.6)
        view.addSubview(instructions)
        let cancel = UIButton(type: .system)
        cancel.setTitle("Cancel", for: .normal); cancel.tintColor = .white
        cancel.translatesAutoresizingMaskIntoConstraints = false
        cancel.addTarget(self, action: #selector(cancelScan), for: .touchUpInside)
        view.addSubview(cancel)
        NSLayoutConstraint.activate([
            cancel.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 12),
            cancel.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 20),
            cancel.heightAnchor.constraint(greaterThanOrEqualToConstant: 44),
            instructions.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 24),
            instructions.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -24),
            instructions.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -24)
        ])
        queue.async { [weak self] in
            guard let self else { return }
            do {
                guard let camera = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .back)
                    else { throw WirelessPeer.failure("The rear camera is unavailable.") }
                let input = try AVCaptureDeviceInput(device: camera)
                let output = AVCaptureMetadataOutput()
                self.session.beginConfiguration()
                guard self.session.canAddInput(input), self.session.canAddOutput(output) else {
                    self.session.commitConfiguration()
                    throw WirelessPeer.failure("The QR scanner could not open the camera.")
                }
                self.session.addInput(input); self.session.addOutput(output)
                output.setMetadataObjectsDelegate(self, queue: .main); output.metadataObjectTypes = [.qr]
                self.session.commitConfiguration(); self.session.startRunning()
            } catch {
                DispatchQueue.main.async { [weak self] in
                    guard let self, !self.finished else { return }
                    let alert = UIAlertController(title: "Cannot scan QR code", message: error.localizedDescription, preferredStyle: .alert)
                    alert.addAction(UIAlertAction(title: "Close", style: .default) { _ in self.cancelScan() })
                    self.present(alert, animated: true)
                }
            }
        }
    }
    override func viewDidLayoutSubviews() { super.viewDidLayoutSubviews(); preview?.frame = view.bounds }
    func metadataOutput(_ output: AVCaptureMetadataOutput, didOutput objects: [AVMetadataObject], from connection: AVCaptureConnection) {
        guard !finished, let code = objects.compactMap({ ($0 as? AVMetadataMachineReadableCodeObject)?.stringValue })
            .first(where: { $0.hasPrefix("camorip://pair?") }) else { return }
        finished = true
        stop { self.onScan(code) }
    }
    @objc private func cancelScan() {
        guard !finished else { return }; finished = true
        stop { self.onCancel() }
    }
    func stop(completion: (() -> Void)? = nil) {
        queue.async { [self] in
            if session.isRunning { session.stopRunning() }
            DispatchQueue.main.async { completion?() }
        }
    }
}
