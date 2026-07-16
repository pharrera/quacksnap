import Crypto
import Foundation
import Security

/// E2EE relay envelope — must stay field-compatible with
/// windows/QuackSnap.Protocol/Envelope.cs. Rides inside the push payload under
/// the "qsEnvelope" key; the notification extension opens it without the app.
public struct Envelope: Codable, Sendable {
    public let v: Int
    public let blobId: String
    public let relayUrl: String
    public let name: String
    public let mime: String
    public let size: Int64
    public let epk: String
    public let keyIv: String
    public let keyCt: String
    public let keyTag: String
    public let blobIv: String
    public let blobTag: String
    public let sig: String

    public var signedData: Data {
        Data("qsenv1|\(blobId)|\(relayUrl)|\(name)|\(mime)|\(size)|\(epk)|\(keyIv)|\(keyCt)|\(keyTag)|\(blobIv)|\(blobTag)".utf8)
    }
}

public enum EnvelopeCrypto {
    private static let hkdfInfo = Data("quacksnap-kek-v1".utf8)

    /// Verifies the sender's signature, unwraps the file key with our keychain
    /// identity (ECDH-ES + HKDF), and decrypts the blob.
    public static func open(
        _ envelope: Envelope,
        blobCiphertext: Data,
        identity: DeviceIdentity,
        senderCertDER: Data
    ) throws -> Data {
        try verifySignature(envelope, senderCertDER: senderCertDER)

        guard let epk = Base64Url.decode(envelope.epk),
              let keyIv = Base64Url.decode(envelope.keyIv),
              let keyCt = Base64Url.decode(envelope.keyCt),
              let keyTag = Base64Url.decode(envelope.keyTag),
              let blobIv = Base64Url.decode(envelope.blobIv),
              let blobTag = Base64Url.decode(envelope.blobTag) else {
            throw QuackSnapError.transferFailed("Envelope fields are not valid base64url")
        }

        // ECDH between our identity key and the sender's ephemeral key.
        var keyRef: SecKey?
        let status = SecIdentityCopyPrivateKey(identity.secIdentity, &keyRef)
        guard status == errSecSuccess, let privateKey = keyRef else {
            throw QuackSnapError.transferFailed("Identity private key unavailable (\(status))")
        }
        guard let ephemeralKey = SecKeyCreateWithData(epk as CFData, [
            kSecAttrKeyType: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeyClass: kSecAttrKeyClassPublic,
        ] as CFDictionary, nil) else {
            throw QuackSnapError.transferFailed("Bad ephemeral key in envelope")
        }
        var exchangeError: Unmanaged<CFError>?
        guard let shared = SecKeyCopyKeyExchangeResult(
            privateKey, .ecdhKeyExchangeStandard, ephemeralKey, [:] as CFDictionary, &exchangeError) as Data? else {
            throw QuackSnapError.transferFailed("ECDH failed: \(exchangeError?.takeRetainedValue().localizedDescription ?? "?")")
        }

        let kek = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: shared),
            salt: epk, info: hkdfInfo, outputByteCount: 32)

        let fileKey = try AES.GCM.open(
            AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: keyIv), ciphertext: keyCt, tag: keyTag),
            using: kek)
        return try AES.GCM.open(
            AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: blobIv), ciphertext: blobCiphertext, tag: blobTag),
            using: SymmetricKey(data: fileKey))
    }

    /// Only the paired sender may push files at us; its cert was pinned at pairing.
    private static func verifySignature(_ envelope: Envelope, senderCertDER: Data) throws {
        guard let cert = SecCertificateCreateWithData(nil, senderCertDER as CFData),
              let certKey = SecCertificateCopyKey(cert),
              let x963 = SecKeyCopyExternalRepresentation(certKey, nil) as Data? else {
            throw QuackSnapError.transferFailed("Cannot read sender certificate key")
        }
        guard let sigRaw = Base64Url.decode(envelope.sig) else {
            throw QuackSnapError.transferFailed("Bad signature encoding")
        }
        let publicKey = try P256.Signing.PublicKey(x963Representation: x963)
        let signature = try P256.Signing.ECDSASignature(rawRepresentation: sigRaw)
        guard publicKey.isValidSignature(signature, for: envelope.signedData) else {
            throw QuackSnapError.transferFailed("Envelope signature is invalid — dropped")
        }
    }
}
