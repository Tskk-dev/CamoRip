import SwiftUI
import AVFoundation

struct ContentView: View {
    @EnvironmentObject var connection: ConnectionManager
    @EnvironmentObject var camera: CameraSession
    @Environment(\.scenePhase) private var scenePhase
    @State private var cameraAuthorized = false
    @State private var permissionChecked = false
    @AppStorage("showStreamingPreview") private var showStreamingPreview = false
    @AppStorage("dimStreamingScreen") private var dimStreamingScreen = true
    @StateObject private var screenDimmer = StreamingScreenDimmer()

    @State private var showWirelessScanner = false
    @State private var wirelessError: String?

    private var showPreview: Bool {
        !showWirelessScanner && cameraAuthorized && scenePhase == .active && !screenDimmer.isDimmed &&
            (connection.airConfiguration.enabled ? showStreamingPreview : connection.state != .streaming || showStreamingPreview)
    }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            if cameraAuthorized {
                if showPreview {
                    CameraPreviewView(session: camera.captureSession, fixedLandscape: connection.airConfiguration.enabled).ignoresSafeArea()
                    if connection.airConfiguration.enabled, let configuration = camera.configuration {
                        AirZonesOverlay(configuration: connection.airConfiguration, mask: connection.airMask,
                                        width: Int(configuration.width), height: Int(configuration.height))
                            .ignoresSafeArea().allowsHitTesting(false)
                    }
                }
                VStack(spacing: 16) {
                    HStack {
                        Label(status, systemImage: connection.state == .streaming ? "video.fill" : "cable.connector")
                            .font(.callout.weight(.semibold))
                            .padding(12)
                            .background(.ultraThinMaterial, in: Capsule())
                        Spacer()
                    }
                    Spacer()
                    ScrollView {
                        VStack(spacing: 10) {
                            Text(connection.formatLabel).font(.headline)
                            if connection.state == .streaming {
                                Text("Diagnostics: Files › On My iPhone › CamoRip › CamoRip.log")
                                    .font(.caption2).foregroundStyle(.secondary).multilineTextAlignment(.center)
                            }
                            Text(hint).font(.callout).multilineTextAlignment(.center)
                            Toggle("Show preview while streaming or in AIR mode", isOn: $showStreamingPreview)
                                .font(.callout)
                            Toggle("Dim screen after 15 seconds", isOn: $dimStreamingScreen)
                                .font(.callout)
                            if connection.state == .streaming && !showStreamingPreview {
                                Text("Phone preview is off. Video still reaches your PC.")
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                            if screenDimmer.thermalState == .serious || screenDimmer.thermalState == .critical {
                                Text("iPhone is hot. Pause streaming if it needs to cool down.")
                                    .font(.callout).foregroundStyle(.orange)
                            }
                            if connection.state == .streaming && connection.latencyMs > 0 {
                                Text("Control latency: \(connection.latencyMs) ms").font(.caption)
                            }
                            HStack(spacing: 16) {
                                Button {
                                    if connection.wantsStreaming { connection.stopStreaming() }
                                    else { connection.startStreaming() }
                                } label: {
                                    Label(connection.wantsStreaming ? "Stop" : "Start",
                                        systemImage: connection.wantsStreaming ? "stop.fill" : "play.fill")
                                        .frame(minWidth: 100)
                                }
                                .buttonStyle(.borderedProminent)
                                .disabled(connection.activeTransport == nil || connection.airConfiguration.enabled)

                                Button { connection.switchCamera() } label: {
                                    Label("Switch", systemImage: "arrow.triangle.2.circlepath.camera")
                                }
                                .buttonStyle(.bordered)
                                .disabled(!camera.isRunning || camera.availableCameras.count < 2 || connection.airConfiguration.enabled)
                            }
                            Button {
                                connection.stopStreaming()
                                screenDimmer.update(enabled: false)
                                camera.pauseForScanner { showWirelessScanner = true }
                            } label: { Label("Connect wirelessly", systemImage: "qrcode.viewfinder") }
                            .buttonStyle(.bordered)
                            Text(connection.wirelessStatus).font(.caption).multilineTextAlignment(.center)
                            Button("Forget paired PC") { connection.forgetWireless() }.font(.caption)
                            if case .error = connection.state {
                                Button("Retry USB connection") {
                                    camera.start()
                                    connection.disconnect()
                                    connection.startDiscovery()
                                }.buttonStyle(.bordered)
                            }
                        }
                    }
                    .frame(maxHeight: 340)
                    .padding(20)
                    .frame(maxWidth: .infinity)
                    .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 20))
                    .opacity(connection.airConfiguration.enabled ? 0 : 1)
                    .allowsHitTesting(!connection.airConfiguration.enabled)
                    .accessibilityHidden(connection.airConfiguration.enabled)
                }
                .foregroundStyle(.white)
                .padding()
                .accessibilityHidden(screenDimmer.isDimmed)
            } else if permissionChecked {
                VStack(spacing: 20) {
                    Image(systemName: "camera.fill").font(.system(size: 48))
                    Text("Camera access required").font(.title2.bold())
                    Text("Allow Camera access in Settings to send video to your Windows PC.")
                        .multilineTextAlignment(.center)
                    Button("Open Settings") {
                        if let url = URL(string: UIApplication.openSettingsURLString) { UIApplication.shared.open(url) }
                    }.buttonStyle(.borderedProminent)
                }
                .foregroundStyle(.white)
                .padding(32)
            } else { ProgressView().tint(.white) }
            if connection.airConfiguration.enabled {
                VStack {
                    Spacer()
                    HStack {
                        Button("Stop AIR") { connection.stopStreaming() }.buttonStyle(.borderedProminent)
                        Button(showStreamingPreview ? "Hide zones" : "Show zones") { showStreamingPreview.toggle() }
                            .buttonStyle(.bordered)
                        Spacer()
                    }
                }.padding().foregroundStyle(.white).accessibilityHidden(screenDimmer.isDimmed)
            }
            if screenDimmer.isDimmed {
                Button { screenDimmer.userInteracted() } label: {
                    ZStack {
                        Color.black
                        VStack(spacing: 12) {
                            Image(systemName: "video.fill")
                            Text(connection.airConfiguration.enabled ? "AIR controller active" : "Streaming over \(connection.activeTransport?.rawValue ?? "USB")")
                            Text("Tap to restore controls").font(.caption)
                        }.foregroundStyle(.gray)
                    }.frame(maxWidth: .infinity, maxHeight: .infinity)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Streaming. Restore screen brightness and controls")
                .ignoresSafeArea()
            }
        }
        .simultaneousGesture(TapGesture().onEnded { screenDimmer.userInteracted() })
        .fullScreenCover(isPresented: $showWirelessScanner, onDismiss: { camera.start() }) {
            WirelessScannerScreen { code in
                do {
                    try connection.pairWirelessly(code: code)
                    showWirelessScanner = false
                } catch {
                    wirelessError = error.localizedDescription
                    showWirelessScanner = false
                }
            }
        }
        .alert("Wireless pairing", isPresented: Binding(get: { wirelessError != nil }, set: { if !$0 { wirelessError = nil } })) {
            Button("OK") { wirelessError = nil }
        } message: { Text(wirelessError ?? "") }
        .onAppear { updateScreenDimming() }
        .task { await checkPermission() }
        .onChange(of: scenePhase) { phase in
            updateScreenDimming()
            if phase == .active { Task { await checkPermission() } }
        }
        .onChange(of: connection.state) { state in
            if showWirelessScanner && connection.activeTransport == .wifi && (state == .connected || state == .streaming) {
                showWirelessScanner = false
            }
            updateScreenDimming()
        }
        .onChange(of: dimStreamingScreen) { _ in updateScreenDimming() }
        .onChange(of: connection.airConfiguration.enabled) { _ in updateScreenDimming() }
        .onChange(of: showStreamingPreview) { value in
            DiagnosticLog.write("Streaming preview enabled=\(value)")
        }
        .onDisappear { screenDimmer.update(enabled: false) }
    }

    private var status: String {
        if connection.airConfiguration.enabled { return "AIR controller active" }
        switch connection.state {
        case .idle, .discovering: return "Waiting for PC"
        case .connected: return "\(connection.activeTransport?.rawValue ?? "USB") connected"
        case .streaming: return "Streaming over \(connection.activeTransport?.rawValue ?? "USB")"
        case .error: return "Connection needs attention"
        }
    }

    private var hint: String {
        switch connection.state {
        case .idle, .discovering:
            return "Connect the cable, unlock and trust this PC, then open CamoRip on Windows. Or scan its wireless pairing QR code."
        case .connected:
            return "Ready. Keep this app open and hold the phone sideways."
        case .streaming:
            return dimStreamingScreen ? "Keep this app open. Tap the dimmed screen to restore controls."
                : "Keep this app open. The screen stays awake while streaming."
        case .error(let message): return message
        }
    }

    private func updateScreenDimming() {
        screenDimmer.update(enabled: dimStreamingScreen && (connection.state == .streaming || connection.airConfiguration.enabled) && scenePhase == .active)
    }

    @MainActor private func checkPermission() async {
        let status = AVCaptureDevice.authorizationStatus(for: .video)
        let granted: Bool
        if status == .notDetermined { granted = await AVCaptureDevice.requestAccess(for: .video) }
        else { granted = status == .authorized }
        cameraAuthorized = granted
        permissionChecked = true
        if granted && !showWirelessScanner { camera.start(); connection.startDiscovery() }
        else { connection.stopStreaming(); camera.stop() }
    }
}

private struct AirZonesOverlay: View {
    let configuration: AirConfiguration
    let mask: Int
    let width: Int
    let height: Int

    var body: some View {
        GeometryReader { geometry in
            let scale = min(geometry.size.width / CGFloat(width), geometry.size.height / CGFloat(height))
            let left = (geometry.size.width - CGFloat(width) * scale) / 2
            let top = (geometry.size.height - CGFloat(height) * scale) / 2
            let rectangles = configuration.rectangles(width: width, height: height)
            ForEach(0..<6, id: \.self) { index in
                let rectangle = rectangles[index]
                Rectangle().stroke(mask & (1 << index) == 0 ? Color.white : Color.green, lineWidth: 3)
                    .frame(width: rectangle.width * scale, height: rectangle.height * scale)
                    .position(x: left + rectangle.midX * scale, y: top + rectangle.midY * scale)
            }
        }
    }
}
