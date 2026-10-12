import AVFoundation

// Keep the ordering testable without a physical camera or capture session.
protocol CaptureMirroringConnection: AnyObject {
    var isVideoMirroringSupported: Bool { get }
    var automaticallyAdjustsVideoMirroring: Bool { get set }
    var isVideoMirrored: Bool { get set }
}

extension AVCaptureConnection: CaptureMirroringConnection {}

func disableCaptureMirroring<Connection: CaptureMirroringConnection>(on connection: Connection) {
    guard connection.isVideoMirroringSupported else { return }
    // Replacing an input creates a new connection with automatic mirroring on.
    // Assigning isVideoMirrored in that state raises an Objective-C exception,
    // which Swift do/catch cannot handle. Take ownership on EVERY new connection.
    connection.automaticallyAdjustsVideoMirroring = false
    connection.isVideoMirrored = false
}
