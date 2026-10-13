import Foundation
import Network
import Security
import CryptoKit

/// Framed camera transport. USB listens on loopback for usbmux. Wireless
/// instances connect outbound to a QR-pinned, authenticated local TLS receiver.
final class USBTransport {
    private let wireless: Bool
    var connectionKind: ConnectionManager.TransportType { wireless ? .wifi : .usb }
    init(wireless: Bool = false) { self.wireless = wireless }
    static let port: UInt16 = 7780
    enum USBConnectionState: Equatable {
        case listening, connected, disconnected, failed(String)
    }

    var onControlMessage: ((Data) -> Void)?
    var onStateChange: ((USBConnectionState) -> Void)?
    var onVideoBackpressure: (() -> Void)?

    private let queue = DispatchQueue(label: "com.camorip.usb", qos: .userInteractive)
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

    func connectWireless(_ peer: WirelessPeer, endpoint: NWEndpoint? = nil) {
        queue.async { [weak self] in
            guard let self else { return }
            let tls = NWProtocolTLS.Options()
            sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, trust, complete in
                let reference = sec_trust_copy_ref(trust).takeRetainedValue()
                guard let certificate = SecTrustGetCertificateAtIndex(reference, 0) else { complete(false); return }
                let bytes = SecCertificateCopyData(certificate) as Data
                let digest = SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
                complete(digest == peer.fingerprint)
            }, self.queue)
            let tcp = NWProtocolTCP.Options(); tcp.noDelay = true
            tcp.enableKeepalive = true; tcp.keepaliveIdle = 3; tcp.keepaliveInterval = 2; tcp.keepaliveCount = 3
            let parameters = NWParameters(tls: tls, tcp: tcp)
            parameters.requiredInterfaceType = .wifi
            let target = endpoint ?? .hostPort(host: NWEndpoint.Host(peer.hosts[0]), port: NWEndpoint.Port(rawValue: peer.port)!)
            let incoming = NWConnection(to: target, using: parameters)
            self.accept(incoming, peer: peer)
            self.queue.asyncAfter(deadline: .now() + 8) { [weak self, weak incoming] in
                guard let self, let incoming, self.connection === incoming,
                      self.wirelessAuthenticating else { return }
                self.disconnect()
            }
        }
    }

    private var wirelessAuthenticating = false
    private func accept(_ incoming: NWConnection, peer: WirelessPeer? = nil) {
        // Keep one controller; stale cancellation callbacks cannot clear a newer link.
        connection?.cancel()
        connection = incoming
        receiveBuffer.removeAll(keepingCapacity: true)
        videoSendsInFlight = 0
        waitingForKeyframe = true
        wirelessAuthenticating = peer != nil
        incoming.stateUpdateHandler = { [weak self, weak incoming] state in
            guard let self, let incoming, self.connection === incoming else { return }
            switch state {
            case .ready:
                if let peer { self.authenticate(incoming, peer: peer); return }
                self.onStateChange?(.connected)
                self.receiveLoop(incoming)
            case .failed(let error):
                self.clear(incoming)
                self.onStateChange?(.failed("\(self.connectionKind.rawValue) connection interrupted: \(error.localizedDescription)"))
            case .cancelled:
                self.clear(incoming)
                self.onStateChange?(.disconnected)
            default: break
            }
        }
        incoming.start(queue: queue)
    }

    private func authenticate(_ incoming: NWConnection, peer: WirelessPeer) {
        guard let data = try? JSONSerialization.data(withJSONObject: ["v": 1, "pc": peer.id,
            "token": peer.token, "pair": !peer.paired]) else { incoming.cancel(); return }
        incoming.send(content: data + Data([10]), completion: .contentProcessed { [weak self, weak incoming] error in
            guard let self, let incoming, self.connection === incoming else { return }
            if error != nil { incoming.cancel(); return }
            self.receiveAuthentication(incoming, peer: peer, accumulated: Data())
        })
    }
    private func receiveAuthentication(_ incoming: NWConnection, peer: WirelessPeer, accumulated: Data) {
        incoming.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self, weak incoming] data, _, complete, error in
            guard let self, let incoming, self.connection === incoming else { return }
            let bytes = accumulated + (data ?? Data())
            guard bytes.count <= 8192, error == nil, (!complete || bytes.contains(10)) else { incoming.cancel(); return }
            guard let newline = bytes.firstIndex(of: 10) else {
                guard bytes.count < 4096 else { incoming.cancel(); return }
                self.receiveAuthentication(incoming, peer: peer, accumulated: bytes); return
            }
            guard newline < 4096, let response = try? JSONSerialization.jsonObject(with: bytes.prefix(upTo: newline)) as? [String: Any],
                  response["ok"] as? Bool == true else {
                self.onStateChange?(.failed("Wireless pairing was rejected. Refresh the QR code on your PC."));
                self.disconnect(); return
            }
            var saved = peer
            if !peer.paired {
                guard let token = response["token"] as? String, WirelessPeer.hex(token, count: 64) else { incoming.cancel(); return }
                saved.token = token; saved.paired = true
                do { try saved.save() }
                catch { self.onStateChange?(.failed(error.localizedDescription)); self.disconnect(); return }
            }
            self.wirelessAuthenticating = false
            self.onStateChange?(.connected)
            self.receiveBuffer.append(bytes.suffix(from: bytes.index(after: newline)))
            guard self.processReceiveBuffer() else { incoming.cancel(); return }
            self.receiveLoop(incoming)
        }
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
                DiagnosticLog.record(.usbBackpressure)
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
                else { DiagnosticLog.record(.usbSent) }
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
        var packet = Data(capacity: frame.data.count + 14)
        packet.append(0x01)
        var length = UInt32(frame.data.count).littleEndian
        var pts = frame.ptsUs.littleEndian
        withUnsafeBytes(of: &length) { packet.append(contentsOf: $0) }
        withUnsafeBytes(of: &pts) { packet.append(contentsOf: $0) }
        packet.append(frame.isKeyframe ? 1 : 0)
        packet.append(frame.data)
        return packet
    }
}
