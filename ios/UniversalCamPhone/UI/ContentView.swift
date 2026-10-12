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
            (connection.state != .streaming || showStreamingPreview)
    }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            if cameraAuthorized {
                if showPreview {
                    CameraPreviewView(session: camera.captureSession).ignoresSafeArea()
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
                                Text("Diagnostics: Files › On My iPhone › UniversalCam › UniversalCam.log")
                                    .font(.caption2).foregroundStyle(.secondary).multilineTextAlignment(.center)
                            }
                            Text(hint).font(.callout).multilineTextAlignment(.center)
                            Toggle("Show preview while streaming", isOn: $showStreamingPreview)
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
                                .disabled(connection.activeTransport == nil)

                                Button { connection.switchCamera() } label: {
                                    Label("Switch", systemImage: "arrow.triangle.2.circlepath.camera")
                                }
                                .buttonStyle(.bordered)
                                .disabled(!camera.isRunning || camera.availableCameras.count < 2)
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
            if screenDimmer.isDimmed {
                Button { screenDimmer.userInteracted() } label: {
                    ZStack {
                        Color.black
                        VStack(spacing: 12) {
                            Image(systemName: "video.fill")
                            Text("Streaming over \(connection.activeTransport?.rawValue ?? "USB")")
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
            WirelessScannerView { code in
                do {
                    try connection.pairWirelessly(code: code)
                    showWirelessScanner = false
                } catch {
                    wirelessError = error.localizedDescription
                    showWirelessScanner = false
                }
            } onCancel: { showWirelessScanner = false }
            .interactiveDismissDisabled()
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
        .onChange(of: connection.state) { _ in updateScreenDimming() }
        .onChange(of: dimStreamingScreen) { _ in updateScreenDimming() }
        .onChange(of: showStreamingPreview) { value in
            DiagnosticLog.write("Streaming preview enabled=\(value)")
        }
        .onDisappear { screenDimmer.update(enabled: false) }
    }

    private var status: String {
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
        screenDimmer.update(enabled: dimStreamingScreen && connection.state == .streaming && scenePhase == .active)
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
