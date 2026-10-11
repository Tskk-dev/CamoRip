import XCTest
import UIKit

final class PhoneEfficiencyTests: XCTestCase {
    func testAnnexBPreservesParameterSetsAndMultipleNALs() {
        let input = Data([0, 0, 0, 2, 0x65, 0x80, 0, 0, 0, 3, 0x41, 0x12, 0x34])
        let parameters = Data([0, 0, 0, 1, 0x67, 0x42])
        let actual = input.withUnsafeBytes { makeAnnexB($0, parameterSets: parameters) }
        XCTAssertEqual(actual, parameters + Data([0, 0, 0, 1, 0x65, 0x80, 0, 0, 0, 1, 0x41, 0x12, 0x34]))
    }

    func testAnnexBRejectsTruncationEmptyNALAndTrailingBytes() {
        for input in [Data(), Data([0, 0, 0]), Data([0, 0, 0, 0]),
                      Data([0, 0, 0, 2, 0x65]), Data([0, 0, 0, 1, 0x65, 0])] {
            XCTAssertNil(input.withUnsafeBytes { makeAnnexB($0, parameterSets: Data()) })
        }
    }

    @MainActor func testDimmingRestoresOriginalBrightnessOnTapAndStop() async {
        var brightness: CGFloat = 0.73
        let dimmer = StreamingScreenDimmer(readBrightness: { brightness },
            writeBrightness: { brightness = $0 }, appIsActive: { true })
        dimmer.update(enabled: true)
        dimmer.dimIfEnabled()
        XCTAssertEqual(brightness, 0)
        XCTAssertTrue(dimmer.isDimmed)
        dimmer.dimIfEnabled() // Must not overwrite saved brightness with zero.
        dimmer.userInteracted()
        XCTAssertEqual(brightness, 0.73)
        XCTAssertFalse(dimmer.isDimmed)
        dimmer.dimIfEnabled()
        dimmer.update(enabled: false)
        XCTAssertEqual(brightness, 0.73)
        XCTAssertFalse(dimmer.isDimmed)
    }

    @MainActor func testLeavingAppRestoresBrightness() async {
        var brightness: CGFloat = 0.42
        let dimmer = StreamingScreenDimmer(readBrightness: { brightness },
            writeBrightness: { brightness = $0 }, appIsActive: { true })
        dimmer.update(enabled: true)
        dimmer.dimIfEnabled()
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        XCTAssertEqual(brightness, 0.42)
        XCTAssertFalse(dimmer.isDimmed)
        dimmer.dimIfEnabled()
        XCTAssertEqual(brightness, 0.42)
    }

    @MainActor func testDisabledOrInactiveAppDoesNotDim() async {
        var brightness: CGFloat = 0.6
        let dimmer = StreamingScreenDimmer(readBrightness: { brightness },
            writeBrightness: { brightness = $0 }, appIsActive: { false })
        dimmer.dimIfEnabled()
        dimmer.update(enabled: true)
        dimmer.dimIfEnabled()
        XCTAssertEqual(brightness, 0.6)
        XCTAssertFalse(dimmer.isDimmed)
        dimmer.update(enabled: false)
    }
}
