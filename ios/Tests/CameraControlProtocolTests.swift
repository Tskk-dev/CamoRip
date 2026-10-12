import XCTest

final class CameraControlProtocolTests: XCTestCase {
    func testEncodingBitrateLimitsCoverQualityControls() {
        XCTAssertTrue(CaptureEncodingLimits.bitrateRange.contains(500_000))
        XCTAssertTrue(CaptureEncodingLimits.bitrateRange.contains(24_000_000))
        XCTAssertTrue(CaptureEncodingLimits.bitrateRange.contains(40_000_000))
        XCTAssertTrue(CaptureEncodingLimits.bitrateRange.contains(50_000_000))
        XCTAssertFalse(CaptureEncodingLimits.bitrateRange.contains(499_999))
        XCTAssertFalse(CaptureEncodingLimits.bitrateRange.contains(50_000_001))
    }
    func testCaptureModesInclude4K30AndFilterActualFrameRateRanges() throws {
        XCTAssertEqual(CaptureMode.choices.count, 5)
        let mode = CaptureMode(width: 3840, height: 2160, fps: 30)
        XCTAssertTrue(CaptureMode.choices.contains(mode))
        XCTAssertFalse(CaptureMode.choices.contains(.init(width: 3840, height: 2160, fps: 60)))
        XCTAssertTrue(mode.matches(width: 3840, height: 2160, minFPS: 1, maxFPS: 30))
        XCTAssertFalse(mode.matches(width: 1920, height: 1080, minFPS: 1, maxFPS: 60))
        XCTAssertFalse(mode.matches(width: 3840, height: 2160, minFPS: 1, maxFPS: 24))
        var state = CameraControlState(); state.capture_modes = [mode]
        let decoded = try JSONDecoder().decode(CameraControlState.self, from: JSONEncoder().encode(state))
        XCTAssertEqual(decoded.capture_modes, [mode])
    }
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

    func testCameraMismatchAndUnknownControlsAreRejected() {
        var state = CameraControlState(); state.camera_id = "rear"
        XCTAssertNotNil(state.validationError(for: .init(request_id: 1, camera_id: "front", values: [:])))
        XCTAssertNotNil(state.validationError(for: .init(request_id: 1, camera_id: "rear", values: ["contrast": 2])))
    }
    func testNonfiniteAndInvalidModesAreRejected() {
        var state = CameraControlState(); state.camera_id = "rear"
        XCTAssertNotNil(state.validationError(for: .init(request_id: 1, camera_id: "rear", values: ["auto_focus": 0.5])))
        XCTAssertNotNil(state.validationError(for: .init(request_id: 1, camera_id: "rear", values: ["focus": .nan])))
    }
    func testActiveFrameExposureBoundsAreEnforced() {
        var state = CameraControlState(); state.camera_id = "rear"
        state.ranges["shutter_ms"] = .init(min: 0.1, max: 1000/60)
        XCTAssertNotNil(state.validationError(for: .init(request_id: 1, camera_id: "rear", values: ["shutter_ms": 33])))
        XCTAssertNil(state.validationError(for: .init(request_id: 1, camera_id: "rear", values: ["shutter_ms": 8])))
    }
}
