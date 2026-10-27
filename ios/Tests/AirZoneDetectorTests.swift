import XCTest

final class AirZoneDetectorTests: XCTestCase {
    func testPreviewChangesPreserveBaselineUntilExplicitCalibration() {
        let detector = AirZoneDetector()
        var configuration = AirConfiguration()
        configuration.enabled = true
        detector.configure(configuration)
        var image = [UInt8](repeating: 100, count: 200 * 200)
        func process(_ now: Double) -> Int? {
            image.withUnsafeBufferPointer {
                detector.processLuma($0.baseAddress!, width: 200, height: 200, stride: 200, now: now)?.mask
            }
        }
        XCTAssertEqual(process(0), 0)
        image = [UInt8](repeating: 180, count: image.count)
        XCTAssertEqual(process(0.01), 63)
        configuration.preview = true
        XCTAssertEqual(detector.configure(configuration), 63)
        XCTAssertEqual(process(0.02), 63)
        configuration.recalibrate = true
        detector.configure(configuration)
        XCTAssertEqual(process(0.03), 0)
    }
    func testCalibrationDetectionReleaseAndHeartbeat() throws {
        let detector = AirZoneDetector()
        var settings = AirConfiguration()
        settings.enabled = true
        detector.configure(settings)
        let width = 200, height = 200, stride = 224
        var image = [UInt8](repeating: 100, count: stride * height)
        func process(_ now: Double) -> AirZoneDetector.Update? {
            image.withUnsafeBufferPointer { detector.processLuma($0.baseAddress!, width: width, height: height, stride: stride, now: now) }
        }
        XCTAssertEqual(process(0)?.mask, 0)
        XCTAssertNil(process(0.02))
        let rectangles = settings.rectangles(width: width, height: height)
        let rectangle = rectangles[2]
        for y in Int(rectangle.minY)..<Int(rectangle.maxY) {
            for x in Int(rectangle.minX)..<Int(rectangle.maxX) { image[y * stride + x] = 160 }
        }
        XCTAssertEqual(process(0.03)?.mask, 4)
        XCTAssertEqual(process(0.2)?.mask, 4)
        image = [UInt8](repeating: 100, count: image.count)
        XCTAssertEqual(process(0.21)?.mask, 0)
        detector.configure(AirConfiguration())
        XCTAssertNil(process(0.5))
    }

    func testSettingsAndZoneBounds() {
        XCTAssertTrue(AirConfiguration().isValid)
        var settings = AirConfiguration()
        settings.threshold = .nan
        XCTAssertFalse(settings.isValid)
        settings = AirConfiguration()
        settings.x = 0
        settings.y = 1
        for rectangle in settings.rectangles(width: 640, height: 480) {
            XCTAssertGreaterThanOrEqual(rectangle.minX, 0)
            XCTAssertGreaterThanOrEqual(rectangle.minY, 0)
            XCTAssertLessThanOrEqual(rectangle.maxX, 640)
            XCTAssertLessThanOrEqual(rectangle.maxY, 480)
        }
    }
}
