import SwiftUI

@main
struct CamoRipPhoneApp: App {

    @UIApplicationDelegateAdaptor(AppDelegate.self) var appDelegate

    @StateObject private var connectionManager: ConnectionManager
    @StateObject private var cameraSession: CameraSession

    init() {
        let manager = ConnectionManager()
        let camera = CameraSession()
        manager.cameraSession = camera
        camera.onVideoSampleBuffer = { [weak manager] sample in manager?.handleVideoSample(sample) }
        camera.onError = { [weak manager] message in manager?.cameraFailed(message) }
        camera.onInterrupted = { [weak manager] message in manager?.cameraInterrupted(message) }
        camera.onInterruptionEnded = { [weak manager] in manager?.cameraInterruptionEnded() }
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
