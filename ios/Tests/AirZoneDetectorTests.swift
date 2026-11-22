import XCTest

final class AirZoneDetectorTests: XCTestCase {
    func testWideZonesDetectLocalHandAtEitherEndAndRecalibrateWidthChanges() throws {
        let detector = AirZoneDetector()
        var settings = AirConfiguration()
        settings.enabled = true
        settings.width = 0.8
        detector.configure(settings)
        let width = 1000, height = 600
        var image = [UInt8](repeating: 100, count: width * height)
        func process(_ now: Double) -> Int? {
            image.withUnsafeBufferPointer {
                detector.processLuma($0.baseAddress!, width: width, height: height, stride: width, now: now)?.mask
            }
        }
        XCTAssertEqual(process(0), 0)
        let rectangle = settings.rectangles(width: width, height: height)[2]
        for (index, left) in [Int(rectangle.minX), Int(rectangle.maxX) - 25].enumerated() {
            for y in Int(rectangle.minY)..<Int(rectangle.maxY) {
                for x in left..<(left + 25) { image[y * width + x] = 180 }
            }
            XCTAssertEqual(process(Double(index) + 0.01), 4)
            image = [UInt8](repeating: 100, count: image.count)
            XCTAssertEqual(process(Double(index) + 0.02), 0)
        }
        settings.width = 0.2
        detector.configure(settings)
        XCTAssertEqual(process(3), 0)
        let oldSettings = try JSONDecoder().decode(AirConfiguration.self, from: Data("{\"enabled\":true}".utf8))
        XCTAssertEqual(oldSettings.width, 0)
        let decoded = try JSONDecoder().decode(AirConfiguration.self, from: JSONEncoder().encode(settings))
        XCTAssertEqual(decoded.width, 0.2)
    }

    func testWideZoneGeometryAndValidation() {
        var settings = AirConfiguration()
        settings.width = 0.2
        for x in [0.0, 0.5, 1.0] {
            settings.x = x
            for rectangle in settings.rectangles(width: 1280, height: 720) {
                XCTAssertEqual(rectangle.width, 256, accuracy: 0.000001)
                XCTAssertEqual(rectangle.height, 28.8, accuracy: 0.000001)
                XCTAssertEqual(rectangle.minX, CGFloat(min(max(1280 * x - 128, 0), 1024)), accuracy: 0.000001)
            }
        }
        settings.width = 1
        XCTAssertTrue(settings.isValid)
        XCTAssertEqual(settings.rectangles(width: 1280, height: 720)[0].width, 1280)
        for invalid in [-0.1, 1.1, Double.nan, Double.infinity] {
            settings.width = invalid
            XCTAssertFalse(settings.isValid)
        }
    }

    func testAirCaptureRanksFrameRateBeforeResolution() {
        let large = CaptureConfiguration(width: 1280, height: 720, fps: 60)
        let fast = CaptureConfiguration(width: 640, height: 360, fps: 120)
        XCTAssertTrue(large.ranksBelow(fast, preferFrameRate: true))
        XCTAssertFalse(large.ranksBelow(fast, preferFrameRate: false))
        XCTAssertTrue(fast.ranksBelow(.init(width: 1280, height: 720, fps: 120), preferFrameRate: true))
    }

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
