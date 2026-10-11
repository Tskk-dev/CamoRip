import SwiftUI
import UIKit

/// Owns temporary brightness changes; all calls and timer callbacks run on main.
final class StreamingScreenDimmer: ObservableObject {
    @Published private(set) var isDimmed = false
    @Published private(set) var thermalState = ProcessInfo.processInfo.thermalState
    private var enabled = false
    private var savedBrightness: CGFloat?
    private var timer: Timer?
    private var observers: [NSObjectProtocol] = []

    private let readBrightness: () -> CGFloat
    private let writeBrightness: (CGFloat) -> Void
    private let appIsActive: () -> Bool

    init(readBrightness: @escaping () -> CGFloat = { UIScreen.main.brightness },
         writeBrightness: @escaping (CGFloat) -> Void = { UIScreen.main.brightness = $0 },
         appIsActive: @escaping () -> Bool = { UIApplication.shared.applicationState == .active }) {
        self.readBrightness = readBrightness
        self.writeBrightness = writeBrightness
        self.appIsActive = appIsActive
        observers.append(NotificationCenter.default.addObserver(
            forName: UIApplication.willResignActiveNotification, object: nil, queue: .main) { [weak self] _ in
                self?.update(enabled: false)
            })
        observers.append(NotificationCenter.default.addObserver(
            forName: ProcessInfo.thermalStateDidChangeNotification, object: nil, queue: .main) { [weak self] _ in
                self?.thermalState = ProcessInfo.processInfo.thermalState
                DiagnosticLog.write("Thermal state changed: \(Self.thermalName(ProcessInfo.processInfo.thermalState))")
            })
    }

    deinit {
        timer?.invalidate()
        observers.forEach { NotificationCenter.default.removeObserver($0) }
    }

    func update(enabled: Bool) {
        self.enabled = enabled
        if enabled {
            if timer == nil && !isDimmed { schedule() }
        } else {
            timer?.invalidate()
            timer = nil
            restoreBrightness()
        }
    }

    func userInteracted() {
        restoreBrightness()
        timer?.invalidate()
        timer = nil
        if enabled { schedule() }
    }

    private func schedule() {
        timer = Timer.scheduledTimer(withTimeInterval: 15, repeats: false) { [weak self] _ in
            self?.dimIfEnabled()
        }
    }

    func dimIfEnabled() {
        timer?.invalidate()
        timer = nil
        guard enabled, !isDimmed, appIsActive() else { return }
        savedBrightness = readBrightness()
        writeBrightness(0)
        isDimmed = true
        DiagnosticLog.write("Display dimmed to minimum brightness; camera continues streaming")
    }

    private func restoreBrightness() {
        if let brightness = savedBrightness {
            writeBrightness(brightness)
            savedBrightness = nil
            DiagnosticLog.write("Display brightness restored")
        }
        isDimmed = false
    }

    static func thermalName(_ state: ProcessInfo.ThermalState) -> String {
        switch state {
        case .nominal: return "nominal"
        case .fair: return "fair"
        case .serious: return "serious"
        case .critical: return "critical"
        @unknown default: return "unknown"
        }
    }
}
