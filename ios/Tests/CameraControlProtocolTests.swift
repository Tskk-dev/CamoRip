import XCTest

final class CameraControlProtocolTests: XCTestCase {
    func testWindowsRequestDecodesWithCameraIdentityAndLatestValues() throws {
        let data = Data(#"{"type":"camera_settings","request_id":42,"camera_id":"rear","values":{"auto_focus":0,"focus":0.25,"exposure_bias":-0.7}}"#.utf8)
        let request = try JSONDecoder().decode(CameraControlRequest.self, from: data)
        XCTAssertEqual(request.request_id, 42)
        XCTAssertEqual(request.camera_id, "rear")
        XCTAssertEqual(request.values["focus"], 0.25)
    }
    func testActualStateUsesSharedWireNames() throws {
        var state = CameraControlState()
        state.request_id = 42; state.camera_id = "rear"; state.torch_available = true
        state.ranges["iso"] = .init(min: 25, max: 1600); state.values["iso"] = 100
        let data = try JSONEncoder().encode(state)
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(json["request_id"] as? Int, 42)
        XCTAssertEqual(json["torch_available"] as? Bool, true)
        let round = try JSONDecoder().decode(CameraControlState.self, from: data)
        XCTAssertEqual(round.ranges["iso"]?.max, 1600)
        XCTAssertEqual(round.values["iso"], 100)
    }
}
