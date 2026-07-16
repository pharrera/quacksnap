import Foundation

// Wire messages, camelCase JSON — must stay byte-compatible with
// windows/QuackSnap.Protocol/Messages.cs.

public enum FrameType: UInt8 {
    case hello = 1
    case offer = 2
    case accept = 3
    case chunk = 4
    case done = 5
    case ack = 6
    case error = 7
    case ping = 8
    case pong = 9
}

public struct HelloMessage: Codable, Sendable {
    public let v: Int
    public let deviceId: String
    public let deviceName: String

    public init(v: Int = 1, deviceId: String, deviceName: String) {
        self.v = v
        self.deviceId = deviceId
        self.deviceName = deviceName
    }
}

public struct OfferMessage: Codable, Sendable {
    public let fileId: String
    public let name: String
    public let mime: String
    public let size: Int64
    public let createdAtUnixMs: Int64
}

public struct AcceptMessage: Codable, Sendable {
    public let fileId: String
    public let offset: Int64

    public init(fileId: String, offset: Int64) {
        self.fileId = fileId
        self.offset = offset
    }
}

public struct DoneMessage: Codable, Sendable {
    public let fileId: String
}

public struct AckMessage: Codable, Sendable {
    public let fileId: String
    public let ok: Bool
    public let error: String?

    public init(fileId: String, ok: Bool, error: String? = nil) {
        self.fileId = fileId
        self.ok = ok
        self.error = error
    }
}

public struct ErrorMessage: Codable, Sendable {
    public let message: String
}

public struct PairRequest: Codable, Sendable {
    public let v: Int
    public let deviceId: String
    public let name: String
    public let listenPort: Int
    public let certFp: String
    public var mac: String
    /// Base64 DER of our certificate so the sender can seal relay envelopes to it.
    public let certDer: String?

    public init(v: Int = 1, deviceId: String, name: String, listenPort: Int, certFp: String, mac: String = "", certDer: String? = nil) {
        self.v = v
        self.deviceId = deviceId
        self.name = name
        self.listenPort = listenPort
        self.certFp = certFp
        self.mac = mac
        self.certDer = certDer
    }
}

public struct PairResponse: Codable, Sendable {
    public let ok: Bool
    public let deviceId: String?
    public let name: String?
    public let certFp: String?
    public let mac: String?
    public let error: String?
    public let certDer: String?
    public let relayUrl: String?
}

public enum WireJSON {
    public static func encode<T: Encodable>(_ value: T) throws -> Data {
        try JSONEncoder().encode(value)
    }

    public static func decode<T: Decodable>(_ type: T.Type, from data: Data) throws -> T {
        try JSONDecoder().decode(type, from: data)
    }
}
