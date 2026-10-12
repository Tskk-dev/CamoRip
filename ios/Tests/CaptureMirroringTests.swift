import XCTest

final class CaptureMirroringTests: XCTestCase {
    private final class Connection: CaptureMirroringConnection {
        var isVideoMirroringSupported = true
        var changes: [String] = []
        var automaticallyAdjustsVideoMirroring = true {
            didSet { changes.append("automatic=\(automaticallyAdjustsVideoMirroring)") }
        }
        var isVideoMirrored = true {
            didSet {
                XCTAssertFalse(automaticallyAdjustsVideoMirroring,
                               "Manual mirroring while automatic adjustment is on can terminate the app")
                changes.append("mirrored=\(isVideoMirrored)")
            }
        }
    }

    func testAutomaticAdjustmentIsDisabledBeforeManualMirroring() {
        let connection = Connection()
        disableCaptureMirroring(on: connection)
        XCTAssertEqual(connection.changes, ["automatic=false", "mirrored=false"])
    }

    func testNewConnectionAfterEveryCameraSwitchIsConfigured() {
        for _ in 0..<10 {
            let replacement = Connection()
            disableCaptureMirroring(on: replacement)
            XCTAssertFalse(replacement.automaticallyAdjustsVideoMirroring)
            XCTAssertFalse(replacement.isVideoMirrored)
        }
    }

    func testUnsupportedConnectionIsNotMutated() {
        let connection = Connection()
        connection.isVideoMirroringSupported = false
        disableCaptureMirroring(on: connection)
        XCTAssertTrue(connection.changes.isEmpty)
    }
}
