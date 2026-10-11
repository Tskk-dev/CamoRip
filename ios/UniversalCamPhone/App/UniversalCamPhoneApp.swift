import SwiftUI

@main
struct UniversalCamPhoneApp: App {

    @UIApplicationDelegateAdaptor(AppDelegate.self) var appDelegate

    @StateObject private var connectionManager: ConnectionManager
    @StateObject private var cameraSession: CameraSession

    init() {
        let manager = ConnectionManager()
        let camera = CameraSession()
        manager.cameraSession = camera
        camera.onVideoSampleBuffer = { [weak manager] sample in manager?.handleVideoSample(sample) }
        camera.onError = { [weak manager] message in manager?.cameraFailed(message) }
        _connectionManager = StateObject(wrappedValue: manager)
        _cameraSession = StateObject(wrappedValue: camera)
    }

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(connectionManager)
                .environmentObject(cameraSession)
        }
    }
}
