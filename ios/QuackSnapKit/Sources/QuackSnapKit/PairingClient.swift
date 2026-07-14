import Foundation
import Network

/// One-shot pairing: plain TCP to the sender's ephemeral pairing listener, mutual
/// HMAC proof of the QR secret, exchange of certificate fingerprints.
public enum PairingClient {
    public static func pair(
        payload: PairingPayload,
        identity: DeviceIdentity,
        listenPort: Int
    ) async throws -> PairedSender {
        var lastError: Error = QuackSnapError.pairingFailed("No hosts to try")

        for host in payload.hosts {
            do {
                return try await pairWithHost(host, payload: payload, identity: identity, listenPort: listenPort)
            } catch {
                lastError = error
            }
        }
        throw lastError
    }

    private static func pairWithHost(
        _ host: String,
        payload: PairingPayload,
        identity: DeviceIdentity,
        listenPort: Int
    ) async throws -> PairedSender {
        guard let port = NWEndpoint.Port(rawValue: UInt16(payload.port)) else {
            throw QuackSnapError.pairingFailed("Bad port \(payload.port)")
        }
        let connection = FrameConnection(connection: NWConnection(
            host: NWEndpoint.Host(host), port: port, using: .tcp))
        defer { connection.cancel() }

        return try await withTimeout(seconds: 6) {
            try await connection.start()

            var request = PairRequest(
                deviceId: identity.deviceId,
                name: identity.deviceName,
                listenPort: listenPort,
                certFp: identity.fingerprint)
            request.mac = PairingMac.forRequest(secret: payload.secret, request)
            try await connection.send(.hello, message: request)

            let (_, responsePayload) = try await connection.receive()
            let response = try WireJSON.decode(PairResponse.self, from: responsePayload)
            guard response.ok,
                  let deviceId = response.deviceId, let name = response.name,
                  let certFp = response.certFp, let mac = response.mac else {
                throw QuackSnapError.pairingFailed(response.error ?? "Sender rejected the pairing")
            }

            let expected = PairingMac.forResponse(secret: payload.secret, deviceId: deviceId, name: name, certFp: certFp)
            guard expected == mac else {
                throw QuackSnapError.pairingFailed("Sender failed verification — wrong code or tampering")
            }
            guard certFp == payload.certFp else {
                throw QuackSnapError.pairingFailed("Sender certificate does not match the QR code")
            }
            return PairedSender(deviceId: deviceId, name: name, certFp: certFp)
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
