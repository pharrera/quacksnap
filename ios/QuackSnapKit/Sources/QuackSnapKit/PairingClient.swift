import Crypto
import Foundation
import Network

/// One-shot pairing: plain TCP to the sender's ephemeral pairing listener, mutual
/// HMAC proof of an out-of-band secret, exchange of certificate fingerprints.
/// Two entry points, same handshake:
///  - `pair(payload:...)` — QR / pasted URI (128-bit secret, endpoint in payload)
///  - `pair(code:...)`    — typed 6-digit code (endpoint found via Bonjour)
public enum PairingClient {
    public static func pair(
        payload: PairingPayload,
        identity: DeviceIdentity,
        listenPort: Int
    ) async throws -> PairedSender {
        var lastError: Error = QuackSnapError.pairingFailed("No hosts to try")

        for host in payload.hosts {
            guard let port = NWEndpoint.Port(rawValue: UInt16(payload.port)) else { continue }
            let endpoint = NWEndpoint.hostPort(host: NWEndpoint.Host(host), port: port)
            do {
                let sender = try await pair(endpoint: endpoint, secret: payload.secret,
                                            identity: identity, listenPort: listenPort)
                guard sender.certFp == payload.certFp else {
                    throw QuackSnapError.pairingFailed("Sender certificate does not match the QR code")
                }
                return sender
            } catch {
                lastError = error
            }
        }
        throw lastError
    }

    public static func pair(
        code: String,
        identity: DeviceIdentity,
        listenPort: Int
    ) async throws -> PairedSender {
        guard ShortPairingCode.isPlausible(code) else {
            throw QuackSnapError.pairingFailed("Enter the 6-digit code shown on your computer")
        }
        let host = try await PairingDiscovery.discoverFirst()
        return try await pair(endpoint: host.endpoint, secret: ShortPairingCode.secret(code),
                              identity: identity, listenPort: listenPort)
    }

    // MARK: - shared handshake

    private static func pair(
        endpoint: NWEndpoint,
        secret: Data,
        identity: DeviceIdentity,
        listenPort: Int
    ) async throws -> PairedSender {
        let connection = FrameConnection(connection: NWConnection(to: endpoint, using: .tcp))
        defer { connection.cancel() }

        // Cancelling the NWConnection fails its pending continuations; hooking that
        // to task cancellation is what lets the timeout race actually win.
        return try await withTimeout(seconds: 8) {
            try await withTaskCancellationHandler {
                try await handshake(connection, secret: secret, identity: identity, listenPort: listenPort)
            } onCancel: {
                connection.cancel()
            }
        }
    }

    private static func handshake(
        _ connection: FrameConnection,
        secret: Data,
        identity: DeviceIdentity,
        listenPort: Int
    ) async throws -> PairedSender {
        do {
            try await connection.start()

            var request = PairRequest(
                deviceId: identity.deviceId,
                name: identity.deviceName,
                listenPort: listenPort,
                certFp: identity.fingerprint,
                certDer: identity.certificateDER.base64EncodedString())
            request.mac = PairingMac.forRequest(secret: secret, request)
            try await connection.send(.hello, message: request)

            let (_, responsePayload) = try await connection.receive()
            let response = try WireJSON.decode(PairResponse.self, from: responsePayload)
            guard response.ok,
                  let deviceId = response.deviceId, let name = response.name,
                  let certFp = response.certFp, let mac = response.mac else {
                throw QuackSnapError.pairingFailed(response.error ?? "Sender rejected the pairing")
            }

            let expected = PairingMac.forResponse(secret: secret, deviceId: deviceId, name: name, certFp: certFp)
            guard expected == mac else {
                throw QuackSnapError.pairingFailed("Sender failed verification — wrong code or tampering")
            }

            // Keep the sender's full cert only if it hashes to the MAC-covered fingerprint.
            var certDer: Data?
            if let der = response.certDer.flatMap({ Data(base64Encoded: $0) }),
               Base64Url.encode(Data(SHA256.hash(data: der))) == certFp {
                certDer = der
            }
            return PairedSender(deviceId: deviceId, name: name, certFp: certFp,
                                certDer: certDer, relayUrl: response.relayUrl)
        }
    }
}

func withTimeout<T: Sendable>(seconds: Double, _ work: @escaping @Sendable () async throws -> T) async throws -> T {
    try await withThrowingTaskGroup(of: T.self) { group in
        group.addTask { try await work() }
        group.addTask {
            try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
            throw QuackSnapError.pairingFailed("Timed out")
        }
        let result = try await group.next()!
        group.cancelAll()
        return result
    }
}
