import Foundation

struct CameraControlRequest: Codable {
    let request_id: Int64
    let camera_id: String
    let values: [String: Double]
}
struct CameraControlRange: Codable {
    let min: Double
    let max: Double
}
struct CameraControlState: Codable {
    var request_id: Int64 = 0
    var camera_id = ""
    var error: String?
    var torch_available = false
    var torch_on = false
    var auto_exposure_supported = false
    var auto_focus_supported = false
    var auto_white_balance_supported = false
    var ranges: [String: CameraControlRange] = [:]
    var values: [String: Double] = [:]
}

extension CameraControlState {
    func validationError(for request: CameraControlRequest) -> String? {
        guard request.request_id > 0, request.camera_id == camera_id else { return "Camera changed. Wait for updated controls." }
        let flags: Set<String> = ["auto_exposure", "auto_focus", "auto_white_balance", "torch_on", "reset"]
        for (key, value) in request.values {
            guard value.isFinite else { return "Invalid camera value." }
            if flags.contains(key) {
                guard value == 0 || value == 1 else { return "Invalid camera mode." }
            } else if let range = ranges[key] {
                guard value >= range.min && value <= range.max else { return "\(key) is outside the camera's range." }
            } else { return "\(key) is unavailable on this camera." }
        }
        return nil
    }
}
