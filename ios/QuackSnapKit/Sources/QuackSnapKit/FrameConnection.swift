import Foundation
import Network

public enum QuackSnapError: Error, LocalizedError {
    case connectionClosed
    case invalidFrame(String)
    case pairingFailed(String)
    case transferFailed(String)

    public var errorDescription: String? {
        switch self {
        case .connectionClosed: return "The connection was closed"
        case .invalidFrame(let detail): return "Protocol error: \(detail)"
        case .pairingFailed(let detail): return "Pairing failed: \(detail)"
        case .transferFailed(let detail): return "Transfer failed: \(detail)"
        }
    }
}

/// Async framing over an NWConnection: 4-byte LE length, 1-byte type, payload.
/// Must match windows/QuackSnap.Protocol/Frames.cs.
public final class FrameConnection: @unchecked Sendable {
    public static let chunkSize = 256 * 1024
    private static let maxFrameSize = chunkSize + 64 * 1024

    public let connection: NWConnection

    public init(connection: NWConnection) {
        self.connection = connection
    }

    public func start() async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            var resumed = false
            connection.stateUpdateHandler = { state in
                guard !resumed else { return }
                switch state {
                case .ready:
                    resumed = true
                    cont.resume()
                case .failed(let error):
                    resumed = true
                    cont.resume(throwing: error)
                case .cancelled:
                    resumed = true
                    cont.resume(throwing: QuackSnapError.connectionClosed)
                default:
                    break
                }
            }
            connection.start(queue: .global(qos: .userInitiated))
        }
    }

    public func send(_ type: FrameType, payload: Data) async throws {
        var frame = Data(capacity: 5 + payload.count)
        var length = UInt32(payload.count + 1).littleEndian
        withUnsafeBytes(of: &length) { frame.append(contentsOf: $0) }
        frame.append(type.rawValue)
        frame.append(payload)
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            connection.send(content: frame, completion: .contentProcessed { error in
                if let error { cont.resume(throwing: error) } else { cont.resume() }
            })
        }
    }

    public func send<T: Encodable>(_ type: FrameType, message: T) async throws {
        try await send(type, payload: WireJSON.encode(message))
    }

    public func receive() async throws -> (type: FrameType, payload: Data) {
        let header = try await receiveExact(4)
        let length = header.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: 0, as: UInt32.self) }.littleEndian
        guard length >= 1, length <= UInt32(Self.maxFrameSize) else {
            throw QuackSnapError.invalidFrame("bad frame length \(length)")
        }
        let body = try await receiveExact(Int(length))
        guard let type = FrameType(rawValue: body[body.startIndex]) else {
            throw QuackSnapError.invalidFrame("unknown frame type \(body[body.startIndex])")
        }
        return (type, body.dropFirst())
    }

    private func receiveExact(_ count: Int) async throws -> Data {
        var buffer = Data()
        while buffer.count < count {
            let remaining = count - buffer.count
            let piece: Data = try await withCheckedThrowingContinuation { cont in
                connection.receive(minimumIncompleteLength: 1, maximumLength: remaining) { data, _, isComplete, error in
                    if let error {
                        cont.resume(throwing: error)
                    } else if let data, !data.isEmpty {
                        cont.resume(returning: data)
                    } else if isComplete {
                        cont.resume(throwing: QuackSnapError.connectionClosed)
                    } else {
                        cont.resume(throwing: QuackSnapError.invalidFrame("empty read"))
                    }
                }
            }
            buffer.append(piece)
        }
        return buffer
    }

    public func cancel() {
        connection.cancel()
    }
}

/// Binary chunk frame: 32-byte SHA-256 file id, 8-byte LE offset, data.
public enum ChunkFrame {
    public static let headerSize = 40

    public static func parse(_ payload: Data) throws -> (fileId: Data, offset: Int64, data: Data) {
        guard payload.count >= headerSize else {
            throw QuackSnapError.invalidFrame("chunk frame too short")
        }
        let start = payload.startIndex
        let fileId = payload[start..<start + 32]
        let offset = payload[(start + 32)..<(start + 40)].withUnsafeBytes {
            $0.loadUnaligned(fromByteOffset: 0, as: Int64.self)
        }.littleEndian
        return (Data(fileId), offset, Data(payload[(start + 40)...]))
    }

    public static func build(fileId: Data, offset: Int64, data: Data) -> Data {
        var frame = Data(capacity: headerSize + data.count)
        frame.append(fileId)
        var le = offset.littleEndian
        withUnsafeBytes(of: &le) { frame.append(contentsOf: $0) }
        frame.append(data)
        return frame
    }
}
