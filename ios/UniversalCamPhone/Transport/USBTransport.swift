import Foundation
import Network

/// Accepts the Windows usbmux tunnel on the phone's loopback address only.
/// usbmux connects to a listening localhost socket on the iPhone; there is no
/// Bonjour service, Wi-Fi listener, or outbound network connection.
final class USBTransport {
    static let port: UInt16 = 7780
    enum USBConnectionState: Equatable {
        case listening, connected, disconnected, failed(String)
    }

    var onControlMessage: ((Data) -> Void)?
    var onStateChange: ((USBConnectionState) -> Void)?
    var onVideoBackpressure: (() -> Void)?

    private let queue = DispatchQueue(label: "com.universalcam.usb", qos: .userInteractive)
    private var listener: NWListener?
    private var connection: NWConnection?
    private var receiveBuffer = Data()
    private var videoSendsInFlight = 0
    private var waitingForKeyframe = true
    private static let maxVideoSendsInFlight = 3
    private static let maxControlBytes = 64 * 1024

    func startListening() {
        queue.async { [weak self] in
            guard let self, self.listener == nil else { return }
            do {
                let options = NWProtocolTCP.Options()
                options.noDelay = true
                options.enableKeepalive = true
                options.keepaliveIdle = 3
                options.keepaliveInterval = 2
                options.keepaliveCount = 3
                let parameters = NWParameters(tls: nil, tcp: options)
                parameters.requiredLocalEndpoint = .hostPort(
                    host: "127.0.0.1", port: NWEndpoint.Port(rawValue: Self.port)!)
                let listener = try NWListener(using: parameters)
                listener.newConnectionHandler = { [weak self] incoming in self?.accept(incoming) }
                listener.stateUpdateHandler = { [weak self, weak listener] state in
                    guard let self, let listener, self.listener === listener else { return }
                    switch state {
                    case .ready:
                        self.onStateChange?(.listening)
                    case .failed(let error):
                        self.listener = nil
                        listener.cancel()
                        self.onStateChange?(.failed("USB listener failed: \(error.localizedDescription)"))
                    default: break
                    }
                }
                self.listener = listener
                listener.start(queue: self.queue)
            } catch {
                self.onStateChange?(.failed("USB listener could not start: \(error.localizedDescription)"))
            }
        }
    }

    private func accept(_ incoming: NWConnection) {
        // Keep one controller; stale cancellation callbacks cannot clear a newer link.
        connection?.cancel()
        connection = incoming
        receiveBuffer.removeAll(keepingCapacity: true)
        videoSendsInFlight = 0
        waitingForKeyframe = true
        incoming.stateUpdateHandler = { [weak self, weak incoming] state in
            guard let self, let incoming, self.connection === incoming else { return }
            switch state {
            case .ready:
                self.onStateChange?(.connected)
                self.receiveLoop(incoming)
            case .failed(let error):
                self.clear(incoming)
                self.onStateChange?(.failed("USB connection interrupted: \(error.localizedDescription)"))
            case .cancelled:
                self.clear(incoming)
                self.onStateChange?(.disconnected)
            default: break
            }
        }
        incoming.start(queue: queue)
    }

    private func clear(_ incoming: NWConnection) {
        guard connection === incoming else { return }
        connection = nil
        receiveBuffer.removeAll(keepingCapacity: true)
        videoSendsInFlight = 0
        waitingForKeyframe = true
    }

    func stopListening() {
        queue.async { [weak self] in
            guard let self else { return }
            if let current = self.connection { self.clear(current); current.cancel() }
            self.listener?.cancel()
            self.listener = nil
            self.onStateChange?(.disconnected)
        }
    }

    func disconnect() {
        queue.async { [weak self] in
            guard let self, let current = self.connection else { return }
            self.clear(current)
            current.cancel()
            self.onStateChange?(.disconnected)
        }
    }

    func sendControl(_ message: ControlMessage) {
        guard let data = try? JSONEncoder().encode(message) else { return }
        var packet = Data([0x00])
        packet.append(data)
        packet.append(0x0A)
        queue.async { [weak self] in
            guard let self, let connection = self.connection else { return }
            connection.send(content: packet, completion: .contentProcessed { [weak self, weak connection] error in
                if error != nil, let self, let connection, self.connection === connection {
                    connection.cancel()
                }
            })
        }
    }

    func sendVideoFrame(_ frame: VideoEncoder.EncodedFrame) {
        queue.async { [weak self] in
            guard let self, let connection = self.connection else { return }
            guard self.videoSendsInFlight < Self.maxVideoSendsInFlight else {
                // A dropped H.264 reference frame invalidates following delta frames.
                self.waitingForKeyframe = true
                self.onVideoBackpressure?()
                return
            }
            if self.waitingForKeyframe && !frame.isKeyframe { return }
            self.waitingForKeyframe = false
            self.videoSendsInFlight += 1
            let packet = self.makeFramePacket(frame)
            connection.send(content: packet, completion: .contentProcessed { [weak self, weak connection] error in
                guard let self, let connection, self.connection === connection else { return }
                self.videoSendsInFlight = max(0, self.videoSendsInFlight - 1)
                if error != nil { connection.cancel() }
            })
        }
    }

    private func receiveLoop(_ incoming: NWConnection) {
        incoming.receive(minimumIncompleteLength: 1, maximumLength: 16 * 1024) { [weak self] data, _, complete, error in
            guard let self, self.connection === incoming else { return }
            if let data, !data.isEmpty {
                self.receiveBuffer.append(data)
                guard self.processReceiveBuffer() else { incoming.cancel(); return }
            }
            if complete || error != nil { incoming.cancel(); return }
            self.receiveLoop(incoming)
        }
    }

    private func processReceiveBuffer() -> Bool {
        while !receiveBuffer.isEmpty {
            // Windows sends control only: type byte 0, UTF-8 JSON, newline.
            guard receiveBuffer.first == 0x00 else { return false }
            guard let newline = receiveBuffer.dropFirst().firstIndex(of: 0x0A) else {
                return receiveBuffer.count <= Self.maxControlBytes
            }
            guard newline - receiveBuffer.startIndex <= Self.maxControlBytes else { return false }
            let message = Data(receiveBuffer[(receiveBuffer.startIndex + 1)..<newline])
            receiveBuffer.removeSubrange(...newline)
            if !message.isEmpty { onControlMessage?(message) }
        }
        return true
    }

    private func makeFramePacket(_ frame: VideoEncoder.EncodedFrame) -> Data {
        var packet = Data([0x01])
        var length = UInt32(frame.data.count).littleEndian
        var pts = frame.ptsUs.littleEndian
        withUnsafeBytes(of: &length) { packet.append(contentsOf: $0) }
        withUnsafeBytes(of: &pts) { packet.append(contentsOf: $0) }
        packet.append(frame.isKeyframe ? 1 : 0)
        packet.append(frame.data)
        return packet
    }
}
