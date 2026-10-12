import XCTest

final class WirelessPeerTests: XCTestCase {
    private func code(_ changes: [String: Any] = [:]) throws -> String {
        let id = String(repeating: "a", count: 32)
        var fields: [String: Any] = ["v": 1, "id": id, "name": "Test PC", "service": "CamoRip-" + id,
            "hosts": ["192.168.1.10"], "port": 7781, "fingerprint": String(repeating: "b", count: 64),
            "token": String(repeating: "c", count: 64), "expires": 1300]
        fields.merge(changes) { _, new in new }
        let data = try JSONSerialization.data(withJSONObject: fields)
        return "camorip://pair?data=" + data.base64EncodedString().replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }
    private let now = Date(timeIntervalSince1970: 1000)
    func testValidQRCodeIsPendingPairing() throws {
        let peer = try WirelessPeer.parse(code(["paired": true]), now: now)
        XCTAssertFalse(peer.paired); XCTAssertEqual(peer.hosts, ["192.168.1.10"])
    }
    func testRejectsExpiredOrLongLivedQRCode() throws {
        for expires in [999, 1000, 1601] {
            XCTAssertThrowsError(try WirelessPeer.parse(code(["expires": expires]), now: now))
        }
    }
    func testRejectsUnsafeAddressesAndMalformedIdentity() throws {
        for hosts in [["8.8.8.8"], ["127.0.0.1"], ["192.168.1.999"], ["example.com"], []] {
            XCTAssertThrowsError(try WirelessPeer.parse(code(["hosts": hosts]), now: now))
        }
        let cases: [[String: Any]] = [["v": 2], ["port": 80], ["fingerprint": "bad"], ["token": "bad"], ["service": "other"]]
        for changes in cases {
            XCTAssertThrowsError(try WirelessPeer.parse(code(changes), now: now))
        }
    }
    func testRejectsUnrelatedQRAndRoundTripsPairedRecord() throws {
        XCTAssertThrowsError(try WirelessPeer.parse("https://example.com"))
        var peer = try WirelessPeer.parse(code(), now: now); peer.paired = true
        let saved = try JSONDecoder().decode(WirelessPeer.self, from: JSONEncoder().encode(peer))
        XCTAssertTrue(saved.paired); XCTAssertEqual(saved.fingerprint, peer.fingerprint)
    }
}
