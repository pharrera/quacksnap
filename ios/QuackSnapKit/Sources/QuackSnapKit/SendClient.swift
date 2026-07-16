import Crypto
import Foundation
import Network
import Security

/// Sends files from this device to a paired computer (iPhone → Windows). Mirrors
/// TransferServer but client-side: discover the PC, open a mutually authenticated
/// TLS connection (present our identity, pin the PC's certificate), then run the
/// Offer/Chunk/Done exchange for each file.
public enum SendClient {
    public struct Progress: Sendable {
        public let name: String
        public let sent: Int64
        public let total: Int64
    }

    /// Sends the files at `urls` to the paired sender. Reports per-file progress.
    public static func send(
        _ urls: [URL],
        to peer: PairedSender,
        identity: DeviceIdentity,
        progress: (@Sendable (Progress) -> Void)? = nil
    ) async throws {
        let endpoint = try await TransferDiscovery.find(deviceId: peer.deviceId)
        let connection = FrameConnection(connection: makeConnection(to: endpoint, peer: peer, identity: identity))
        defer { connection.cancel() }

        try await connection.start()

        // Handshake.
        try await connection.send(.hello, message: HelloMessage(
            deviceId: identity.deviceId, deviceName: identity.deviceName))
        let (helloType, _) = try await connection.receive()
        guard helloType == .hello else { throw QuackSnapError.invalidFrame("expected Hello, got \(helloType)") }

        for url in urls {
            try await sendOne(url, over: connection, progress: progress)
        }
    }

    private static func sendOne(
        _ url: URL,
        over connection: FrameConnection,
        progress: (@Sendable (Progress) -> Void)?
    ) async throws {
        let scoped = url.startAccessingSecurityScopedResource()
        defer { if scoped { url.stopAccessingSecurityScopedResource() } }

        let data = try Data(contentsOf: url)
        let fileId = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        let fileIdRaw = Data(fileId.chunked(2).compactMap { UInt8($0, radix: 16) })
        let name = url.lastPathComponent
        let mime = Self.mime(for: url.pathExtension.lowercased())

        try await connection.send(.offer, message: OfferMessage(
            fileId: fileId, name: name, mime: mime, size: Int64(data.count),
            createdAtUnixMs: Int64(Date().timeIntervalSince1970 * 1000)))
        let (acceptType, acceptPayload) = try await connection.receive()
        guard acceptType == .accept else { throw QuackSnapError.invalidFrame("expected Accept, got \(acceptType)") }
        let offset = Int(try WireJSON.decode(AcceptMessage.self, from: acceptPayload).offset)

        var pos = offset
        while pos < data.count {
            let end = min(pos + FrameConnection.chunkSize, data.count)
            let chunk = ChunkFrame.build(fileId: fileIdRaw, offset: Int64(pos), data: data.subdata(in: pos..<end))
            try await connection.send(.chunk, payload: chunk)
            pos = end
            progress?(Progress(name: name, sent: Int64(pos), total: Int64(data.count)))
        }

        try await connection.send(.done, message: DoneMessage(fileId: fileId))
        let (ackType, ackPayload) = try await connection.receive()
        guard ackType == .ack else { throw QuackSnapError.invalidFrame("expected Ack, got \(ackType)") }
        let ack = try WireJSON.decode(AckMessage.self, from: ackPayload)
        guard ack.ok else { throw QuackSnapError.transferFailed(ack.error ?? "receiver rejected the file") }
    }

    private static func makeConnection(to endpoint: NWEndpoint, peer: PairedSender, identity: DeviceIdentity) -> NWConnection {
        let tls = NWProtocolTLS.Options()
        if let secIdentity = sec_identity_create(identity.secIdentity) {
            sec_protocol_options_set_local_identity(tls.securityProtocolOptions, secIdentity)
        }
        sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)

        let pinnedFp = peer.certFp
        sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, secTrust, complete in
            let trust = sec_trust_copy_ref(secTrust).takeRetainedValue()
            guard let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
                  let leaf = chain.first else { complete(false); return }
            let der = SecCertificateCopyData(leaf) as Data
            complete(Base64Url.encode(Data(SHA256.hash(data: der))) == pinnedFp)
        }, .global(qos: .userInitiated))

        return NWConnection(to: endpoint, using: NWParameters(tls: tls, tcp: NWProtocolTCP.Options()))
    }

    private static func mime(for ext: String) -> String {
        switch ext {
        case "png": return "image/png"
        case "jpg", "jpeg": return "image/jpeg"
        case "gif": return "image/gif"
        case "heic": return "image/heic"
        case "pdf": return "application/pdf"
        case "txt": return "text/plain"
        case "md": return "text/markdown"
        case "json": return "application/json"
        case "mp4": return "video/mp4"
        case "mov": return "video/quicktime"
        case "zip": return "application/zip"
        default: return "application/octet-stream"
        }
    }
}

private extension String {
    /// Splits into fixed-size substrings (used to turn a hex string into bytes).
    func chunked(_ size: Int) -> [String] {
        var result: [String] = []
        var index = startIndex
        while index < endIndex {
            let next = self.index(index, offsetBy: size, limitedBy: endIndex) ?? endIndex
            result.append(String(self[index..<next]))
            index = next
        }
        return result
    }
}
