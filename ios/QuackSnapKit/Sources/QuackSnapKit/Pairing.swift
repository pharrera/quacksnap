import Crypto
import Foundation

public enum Base64Url {
    public static func encode(_ data: Data) -> String {
        Data(data).base64EncodedString()
            .replacingOccurrences(of: "=", with: "")
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
    }

    public static func decode(_ text: String) -> Data? {
        var s = text
            .replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        while s.count % 4 != 0 { s += "=" }
        return Data(base64Encoded: s)
    }
}

/// Parsed quacksnap://pair?... payload from the sender's QR code / copyable string.
public struct PairingPayload: Sendable {
    public let hosts: [String]
    public let port: Int
    public let secret: Data
    public let certFp: String
    public let name: String
    public let deviceId: String

    public static func parse(_ uri: String) throws -> PairingPayload {
        guard let components = URLComponents(string: uri.trimmingCharacters(in: .whitespacesAndNewlines)),
              components.scheme?.lowercased() == "quacksnap" else {
            throw QuackSnapError.pairingFailed("Not a quacksnap:// pairing code")
        }
        var query: [String: String] = [:]
        for item in components.queryItems ?? [] { query[item.name] = item.value }
        guard let h = query["h"], let p = query["p"], let port = Int(p),
              let s = query["s"], let secret = Base64Url.decode(s),
              let fp = query["fp"], let n = query["n"], let id = query["id"] else {
            throw QuackSnapError.pairingFailed("Pairing code is missing required fields")
        }
        let hosts = h.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        guard !hosts.isEmpty else { throw QuackSnapError.pairingFailed("No hosts in pairing code") }
        return PairingPayload(hosts: hosts, port: port, secret: secret, certFp: fp, name: n, deviceId: id)
    }
}

/// HMAC proofs — strings must match windows/QuackSnap.Protocol/Pairing.cs exactly.
public enum PairingMac {
    public static func forRequest(secret: Data, _ req: PairRequest) -> String {
        compute(secret: secret, "req|\(req.deviceId)|\(req.name)|\(req.listenPort)|\(req.certFp)")
    }

    public static func forResponse(secret: Data, deviceId: String, name: String, certFp: String) -> String {
        compute(secret: secret, "resp|\(deviceId)|\(name)|\(certFp)")
    }

    private static func compute(secret: Data, _ text: String) -> String {
        let mac = HMAC<SHA256>.authenticationCode(for: Data(text.utf8), using: SymmetricKey(data: secret))
        return Base64Url.encode(Data(mac))
    }
}

/// The sender we paired with; persisted by the app and pinned at the TLS layer.
public struct PairedSender: Codable, Equatable, Sendable {
    public let deviceId: String
    public let name: String
    public let certFp: String
    public let pairedAt: Date

    public init(deviceId: String, name: String, certFp: String, pairedAt: Date = Date()) {
        self.deviceId = deviceId
        self.name = name
        self.certFp = certFp
        self.pairedAt = pairedAt
    }
}
