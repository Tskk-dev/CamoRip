import Foundation
import Network

/// Selection and reconnect decisions run on main; socket IO stays on transport queues.
final class AutomaticPhoneTransport: PhoneTransport {
    var onControlMessage: ((Data) -> Void)?
    var onStateChange: ((USBTransport.USBConnectionState) -> Void)?
    var onVideoBackpressure: (() -> Void)?
    var onWirelessStatus: ((String) -> Void)?
    var connectionKind: ConnectionManager.TransportType { active?.connectionKind ?? .usb }
    private let usb = USBTransport()
    private let wifi = USBTransport(wireless: true)
    /// Mutated on main only. Callers off main (encoded video, AIR state from the capture
    /// queue) read `videoOutput`, a lock-protected copy.
    private var active: USBTransport? {
        didSet { outputLock.lock(); videoOutput = active; outputLock.unlock() }
    }
    private var peer: WirelessPeer?
    private var connecting = false
    private var timer: Timer?
    private var browser: NWBrowser?
    private var discovered: NWEndpoint?
    private var addressIndex = 0

    init() { bind(usb); bind(wifi) }
    deinit { timer?.invalidate(); browser?.cancel(); usb.stopListening(); wifi.disconnect() }
    private func bind(_ transport: USBTransport) {
        transport.onStateChange = { [weak self, weak transport] state in
            DispatchQueue.main.async {
                guard let self, let transport else { return }
                if state == .connected {
                    if transport === self.wifi && self.active === self.usb {
                        self.peer = WirelessPeer.load() ?? self.peer
                        self.onWirelessStatus?("Paired. USB is active; Wi-Fi reconnects when unplugged.")
                        self.wifi.disconnect(); self.connecting = false; return
                    }
                    if self.active != nil && self.active !== transport { self.onStateChange?(.disconnected) }
                    self.active = transport
                    if transport === self.usb { self.wifi.disconnect(); self.connecting = false }
                    else { self.peer = WirelessPeer.load() ?? self.peer; self.connecting = false
                        self.onWirelessStatus?("Connected to \(self.peer?.name ?? "PC") over Wi-Fi") }
                    self.onStateChange?(.connected); return
                }
                if transport === self.wifi {
                    self.connecting = false
                    if self.active === self.usb { return }
                    if self.active === self.wifi { self.active = nil; self.onStateChange?(state) }
                    if case .failed(let message) = state { self.onWirelessStatus?(message) }
                    else { self.onWirelessStatus?("Reconnecting to paired PC on Wi-Fi…") }
                } else {
                    // .listening describes the USB listener, not the connection. A listener
                    // that becomes ready again must not orphan a live USB link (control and
                    // video would be silently dropped until the heartbeat disconnects).
                    if case .listening = state {
                        if self.active == nil { self.onStateChange?(state) }
                        return
                    }
                    if self.active === self.usb { self.active = nil }
                    if self.active == nil { self.onStateChange?(state) }
                }
            }
        }
        transport.onControlMessage = { [weak self, weak transport] data in
            DispatchQueue.main.async {
                guard let self, self.active === transport else { return }
                self.onControlMessage?(data)
            }
        }
        // Encoded output stays off main: each USBTransport bounds its own in-flight sends
        // and discards delta frames until a keyframe after any drop.
        transport.onVideoBackpressure = { [weak self] in self?.onVideoBackpressure?() }
    }
    func startListening() {
        usb.startListening()
        if peer == nil { peer = WirelessPeer.load() }
        if peer != nil { startReconnect() }
    }
    func pair(code: String) throws {
        let candidate = try WirelessPeer.parse(code)
        wifi.disconnect(); peer = candidate; discovered = nil; connecting = false; addressIndex = 0
        startBrowser(candidate); startReconnect()
        onWirelessStatus?("Connecting to \(candidate.name)…")
        attempt()
    }
    func forgetWireless() {
        peer = nil; WirelessPeer.forget(); timer?.invalidate(); timer = nil
        browser?.cancel(); browser = nil; discovered = nil; connecting = false
        wifi.disconnect(); onWirelessStatus?("Wireless pairing removed")
    }
    func disconnect() { usb.disconnect(); wifi.disconnect() }
    func sendControl(_ message: ControlMessage) {
        // Also called from the capture queue (AIR state), so read the lock-protected copy.
        outputLock.lock()
        let output = videoOutput
        outputLock.unlock()
        output?.sendControl(message)
    }
    func sendVideoFrame(_ frame: VideoEncoder.EncodedFrame) {
        // Do not hop through main. Dropping an encoded frame there (when SwiftUI or the
        // system kept main busy for two frame intervals) skipped USBTransport's keyframe
        // gate, so later delta frames referenced a frame Windows never received and the
        // picture smeared until the next IDR. The transport's own queue bounds sends and
        // a new connection always starts by waiting for a keyframe.
        outputLock.lock()
        let output = videoOutput
        outputLock.unlock()
        output?.sendVideoFrame(frame)
    }
    private let outputLock = NSLock()
    private var videoOutput: USBTransport?
    private func startReconnect() {
        if browser == nil, let peer { startBrowser(peer) }
        guard timer == nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in self?.attempt() }
    }
    private func attempt() {
        guard !connecting, var peer else { return }
        if let saved = WirelessPeer.load(), saved.id == peer.id { peer = saved; self.peer = saved }
        guard active == nil || (active === usb && !peer.paired) else { return }
        guard peer.paired || peer.expires > Int64(Date().timeIntervalSince1970) else {
            onWirelessStatus?("QR expired. Refresh it on your PC and scan again."); return
        }
        connecting = true
        var target = peer
        target.hosts = [peer.hosts[addressIndex % peer.hosts.count]]
        addressIndex += 1
        wifi.connectWireless(target, endpoint: discovered)
    }
    private func startBrowser(_ peer: WirelessPeer) {
        browser?.cancel()
        let parameters = NWParameters.tcp; parameters.requiredInterfaceType = .wifi
        let browser = NWBrowser(for: .bonjour(type: "_camorip._tcp", domain: "local."), using: parameters)
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            DispatchQueue.main.async {
                guard let self, self.peer?.id == peer.id else { return }
                self.discovered = results.first(where: { result in
                    if case .service(let name, _, _, _) = result.endpoint { return name == peer.service }
                    return false
                })?.endpoint
            }
        }
        browser.stateUpdateHandler = { [weak self] state in
            if case .waiting = state { DispatchQueue.main.async { self?.onWirelessStatus?("Allow Local Network access and connect both devices to the same network.") } }
        }
        self.browser = browser; browser.start(queue: DispatchQueue(label: "com.camorip.discovery"))
    }
}
