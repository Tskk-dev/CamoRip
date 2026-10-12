import Foundation
import Security

struct WirelessPeer: Codable {
    var v: Int
    var id: String
    var name: String
    var service: String
    var hosts: [String]
    var port: UInt16
    var fingerprint: String
    var token: String
    var expires: Int64
    var paired: Bool = false

    enum CodingKeys: String, CodingKey { case v, id, name, service, hosts, port, fingerprint, token, expires, paired }
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        v = try c.decode(Int.self, forKey: .v); id = try c.decode(String.self, forKey: .id)
        name = try c.decode(String.self, forKey: .name); service = try c.decode(String.self, forKey: .service)
        hosts = try c.decode([String].self, forKey: .hosts); port = try c.decode(UInt16.self, forKey: .port)
        fingerprint = try c.decode(String.self, forKey: .fingerprint); token = try c.decode(String.self, forKey: .token)
        expires = try c.decode(Int64.self, forKey: .expires); paired = try c.decodeIfPresent(Bool.self, forKey: .paired) ?? false
    }
    static func parse(_ code: String, now: Date = Date()) throws -> WirelessPeer {
        guard code.count <= 8192, let url = URLComponents(string: code), url.scheme == "camorip",
              url.host == "pair", let value = url.queryItems?.first(where: { $0.name == "data" })?.value else {
            throw failure("Scan the pairing QR code shown in CamoRip on your PC.")
        }
        var base64 = value.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        base64 += String(repeating: "=", count: (4 - base64.count % 4) % 4)
        guard let data = Data(base64Encoded: base64), var peer = try? JSONDecoder().decode(Self.self, from: data),
              peer.v == 1, peer.port == 7781, hex(peer.id, count: 32),
              peer.service == "CamoRip-" + peer.id, hex(peer.token, count: 64), hex(peer.fingerprint, count: 64),
              !peer.hosts.isEmpty, peer.hosts.count <= 16, peer.hosts.allSatisfy(localIPv4),
              peer.expires > Int64(now.timeIntervalSince1970),
              peer.expires <= Int64(now.timeIntervalSince1970) + 600 else {
            throw failure("This pairing QR code is invalid or expired. Refresh it on your PC.")
        }
        peer.paired = false // A QR can never install a persistent credential directly.
        return peer
    }
    static func hex(_ value: String, count: Int) -> Bool {
        value.count == count && value.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
    }
    static func localIPv4(_ host: String) -> Bool {
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4, parts.allSatisfy({ !$0.isEmpty && $0.allSatisfy(\.isNumber) }),
              parts.allSatisfy({ Int($0).map { (0...255).contains($0) } ?? false }),
              let first = Int(parts[0]), let second = Int(parts[1]) else { return false }
        return first == 10 || (first == 172 && (16...31).contains(second)) || (first == 192 && second == 168)
            || (first == 169 && second == 254)
    }
    static func failure(_ message: String) -> NSError {
        NSError(domain: "CamoRip.Wireless", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
    }
    private static let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
        kSecAttrService as String: "com.universalcam.phone.wireless", kSecAttrAccount as String: "paired-pc"]
    func save() throws {
        let data = try JSONEncoder().encode(self)
        let status = SecItemUpdate(Self.query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if status == errSecItemNotFound {
            var values = Self.query; values[kSecValueData as String] = data
            values[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            guard SecItemAdd(values as CFDictionary, nil) == errSecSuccess else { throw Self.failure("Could not save this PC in Keychain.") }
        } else if status != errSecSuccess { throw Self.failure("Could not update the paired PC in Keychain.") }
    }
    static func load() -> WirelessPeer? {
        var values = query; values[kSecReturnData as String] = true
        var result: CFTypeRef?
        guard SecItemCopyMatching(values as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data, let peer = try? JSONDecoder().decode(Self.self, from: data), peer.paired else { return nil }
        return peer
    }
    static func forget() { SecItemDelete(query as CFDictionary) }
}
