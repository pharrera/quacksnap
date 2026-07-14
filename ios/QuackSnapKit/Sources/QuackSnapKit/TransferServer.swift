import Crypto
import Foundation
import Network
import Security

public struct ReceivedFile: Sendable {
    public let url: URL
    public let name: String
    public let size: Int64
    public let from: String
}

/// The receiving end: a TLS server that only ever talks to the paired sender.
/// Mutual TLS — we present our keychain identity, require a client certificate,
/// and accept exactly one: the fingerprint pinned at pairing time.
public final class TransferServer: @unchecked Sendable {
    public let port: UInt16

    private let identity: DeviceIdentity
    private let peer: PairedSender
    private let inboxDirectory: URL
    private let onReceived: @Sendable (ReceivedFile) -> Void
    private let onStateChange: (@Sendable (String) -> Void)?
    private var listener: NWListener?

    public init(
        identity: DeviceIdentity,
        peer: PairedSender,
        port: UInt16 = 47820,
        inboxDirectory: URL,
        onReceived: @escaping @Sendable (ReceivedFile) -> Void,
        onStateChange: (@Sendable (String) -> Void)? = nil
    ) {
        self.identity = identity
        self.peer = peer
        self.port = port
        self.inboxDirectory = inboxDirectory
        self.onReceived = onReceived
        self.onStateChange = onStateChange
    }

    public func start() throws {
        try FileManager.default.createDirectory(at: inboxDirectory, withIntermediateDirectories: true)

        let tls = NWProtocolTLS.Options()
        guard let secIdentity = sec_identity_create(identity.secIdentity) else {
            throw QuackSnapError.transferFailed("Could not wrap keychain identity for TLS")
        }
        sec_protocol_options_set_local_identity(tls.securityProtocolOptions, secIdentity)
        sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)
        sec_protocol_options_set_peer_authentication_required(tls.securityProtocolOptions, true)

        let pinnedFp = peer.certFp
        sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, secTrust, complete in
            let trust = sec_trust_copy_ref(secTrust).takeRetainedValue()
            guard let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
                  let leaf = chain.first else {
                complete(false)
                return
            }
            let der = SecCertificateCopyData(leaf) as Data
            let fp = Base64Url.encode(Data(SHA256.hash(data: der)))
            complete(fp == pinnedFp)
        }, .global(qos: .userInitiated))

        let params = NWParameters(tls: tls, tcp: NWProtocolTCP.Options())
        params.allowLocalEndpointReuse = true

        guard let nwPort = NWEndpoint.Port(rawValue: port) else {
            throw QuackSnapError.transferFailed("Bad listen port \(port)")
        }
        let listener = try NWListener(using: params, on: nwPort)
        listener.stateUpdateHandler = { [weak self] state in
            self?.onStateChange?("\(state)")
        }
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { return }
            Task { await self.handle(connection) }
        }
        listener.start(queue: .global(qos: .userInitiated))
        self.listener = listener
    }

    public func stop() {
        listener?.cancel()
        listener = nil
    }

    // MARK: - session handling

    private func handle(_ nwConnection: NWConnection) async {
        let connection = FrameConnection(connection: nwConnection)
        do {
            try await connection.start()

            let (helloType, helloPayload) = try await connection.receive()
            guard helloType == .hello else {
                throw QuackSnapError.invalidFrame("expected Hello, got \(helloType)")
            }
            let hello = try WireJSON.decode(HelloMessage.self, from: helloPayload)
            guard hello.deviceId == peer.deviceId else {
                throw QuackSnapError.invalidFrame("unexpected sender id")
            }
            try await connection.send(.hello, message: HelloMessage(
                deviceId: identity.deviceId, deviceName: identity.deviceName))

            try await receiveLoop(connection, senderName: hello.deviceName)
        } catch {
            connection.cancel()
        }
    }

    private func receiveLoop(_ connection: FrameConnection, senderName: String) async throws {
        var current: OfferMessage?
        var handle: FileHandle?
        var partURL: URL?

        defer { try? handle?.close() }

        while true {
            let (type, payload) = try await connection.receive()
            switch type {
            case .ping:
                try await connection.send(.pong, payload: Data())

            case .offer:
                let offer = try WireJSON.decode(OfferMessage.self, from: payload)
                let part = inboxDirectory.appendingPathComponent(offer.fileId + ".part")
                if !FileManager.default.fileExists(atPath: part.path) {
                    FileManager.default.createFile(atPath: part.path, contents: nil)
                }
                let file = try FileHandle(forWritingTo: part)
                let offset = try file.seekToEnd()
                current = offer
                handle = file
                partURL = part
                try await connection.send(.accept, message: AcceptMessage(fileId: offer.fileId, offset: Int64(offset)))

            case .chunk:
                guard let offer = current, let file = handle else {
                    throw QuackSnapError.invalidFrame("chunk without an offer")
                }
                let (fileId, offset, data) = try ChunkFrame.parse(payload)
                guard fileId.map({ String(format: "%02x", $0) }).joined() == offer.fileId else {
                    throw QuackSnapError.invalidFrame("chunk for unexpected file")
                }
                guard try file.offset() == UInt64(offset) else {
                    throw QuackSnapError.invalidFrame("out-of-order chunk")
                }
                try file.write(contentsOf: data)

            case .done:
                guard let offer = current, let file = handle, let part = partURL else {
                    throw QuackSnapError.invalidFrame("done without an offer")
                }
                try file.close()
                handle = nil

                let bytes = try Data(contentsOf: part)
                let actual = SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
                if actual != offer.fileId {
                    try? FileManager.default.removeItem(at: part)
                    try await connection.send(.ack, message: AckMessage(fileId: offer.fileId, ok: false, error: "Hash mismatch"))
                } else {
                    let destination = uniqueDestination(for: offer.name)
                    try FileManager.default.moveItem(at: part, to: destination)
                    try await connection.send(.ack, message: AckMessage(fileId: offer.fileId, ok: true))
                    onReceived(ReceivedFile(url: destination, name: offer.name, size: offer.size, from: senderName))
                }
                current = nil
                partURL = nil

            default:
                throw QuackSnapError.invalidFrame("unexpected frame \(type)")
            }
        }
    }

    private func uniqueDestination(for name: String) -> URL {
        let safe = name.components(separatedBy: CharacterSet(charactersIn: "/\\:?%*|\"<>")).joined(separator: "_")
        var candidate = inboxDirectory.appendingPathComponent(safe)
        var counter = 2
        while FileManager.default.fileExists(atPath: candidate.path) {
            let stem = (safe as NSString).deletingPathExtension
            let ext = (safe as NSString).pathExtension
            let numbered = ext.isEmpty ? "\(stem) (\(counter))" : "\(stem) (\(counter)).\(ext)"
            candidate = inboxDirectory.appendingPathComponent(numbered)
            counter += 1
        }
        return candidate
    }
}
