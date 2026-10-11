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

