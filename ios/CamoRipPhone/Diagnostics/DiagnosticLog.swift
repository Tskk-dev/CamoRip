import Foundation

struct CaptureStats: Codable {
    let configuredFPS: Int
    let resolution: String
    let capturedFrames: Int
    let droppedFrames: Int
    let sampleSeconds: Double
    let active: Bool

    enum CodingKeys: String, CodingKey {
        case configuredFPS = "configured_fps", capturedFrames = "captured_frames"
        case droppedFrames = "dropped_frames", sampleSeconds = "sample_seconds"
        case resolution, active
    }
}

/// Small on-device log and per-stage frame counters for diagnosing slow streams.
/// The Documents log is exposed in Files so it can be copied from the iPhone.
enum DiagnosticLog {
    enum Stage: String, CaseIterable { case captured, captureDropped, encodeDropped, encoded, usbSent, usbBackpressure }
    private static let lock = NSLock()
    private static var counts = Dictionary(uniqueKeysWithValues: Stage.allCases.map { ($0, 0) })
    private static var capturedTotal = 0
    private static var droppedTotal = 0
    private static let queue = DispatchQueue(label: "com.camorip.diagnostics")

    private static var fileURL: URL {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("CamoRip.log")
    }

    static func record(_ stage: Stage) {
        lock.lock()
        counts[stage, default: 0] += 1
        if stage == .captured { capturedTotal += 1 }
        if stage == .captureDropped { droppedTotal += 1 }
        lock.unlock()
    }

    static func captureSnapshot() -> (frames: Int, dropped: Int, time: Double) {
        lock.lock(); defer { lock.unlock() }
        return (capturedTotal, droppedTotal, ProcessInfo.processInfo.systemUptime)
    }

    static func takeMetrics() -> String {
        lock.lock()
        let snapshot = counts
        for stage in Stage.allCases { counts[stage] = 0 }
        lock.unlock()
        return Stage.allCases.map { "\($0.rawValue)=\(snapshot[$0, default: 0])" }.joined(separator: " ")
    }

    static func write(_ message: String) {
        let line = "\(ISO8601DateFormatter().string(from: Date())) \(message)\n"
        queue.async {
            let url = fileURL
            do {
                if let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
                   ((attributes[.size] as? NSNumber)?.intValue ?? 0) > 1_048_576 {
                    let previous = url.deletingPathExtension().appendingPathExtension("previous.log")
                    try? FileManager.default.removeItem(at: previous)
                    try FileManager.default.moveItem(at: url, to: previous)
                }
                if !FileManager.default.fileExists(atPath: url.path) {
                    FileManager.default.createFile(atPath: url.path, contents: nil)
                }
                let handle = try FileHandle(forWritingTo: url)
                try handle.seekToEnd()
                try handle.write(contentsOf: Data(line.utf8))
                try handle.close()
            } catch { NSLog("CamoRip diagnostics write failed: %@", error.localizedDescription) }
        }
    }
}
