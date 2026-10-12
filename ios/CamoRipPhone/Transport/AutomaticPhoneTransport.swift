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
    private var active: USBTransport? {
        didSet { outputLock.lock(); outputGeneration += 1; outputLock.unlock() }
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
        // Encoded output stays on the encoder queue; select the output on main
        // below before enqueueing each bounded socket send.
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
    func sendControl(_ message: ControlMessage) { active?.sendControl(message) }
    func sendVideoFrame(_ frame: VideoEncoder.EncodedFrame) {
        // Connection selection is main-isolated. Bound queued frames to avoid
        // retaining a growing list of video packets if main is busy.
        outputLock.lock()
        guard pendingOutputs < 2 else { outputLock.unlock(); onVideoBackpressure?(); return }
        let generation = outputGeneration
        pendingOutputs += 1; outputLock.unlock()
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.outputLock.lock(); self.pendingOutputs -= 1
            let current = generation == self.outputGeneration; self.outputLock.unlock()
            guard current else { return }
            self.active?.sendVideoFrame(frame)
        }
    }
    private let outputLock = NSLock()
    private var pendingOutputs = 0
    private var outputGeneration = 0
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
