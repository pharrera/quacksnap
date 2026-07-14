import Crypto
import Foundation
import Security
import SwiftASN1
import X509

/// This device's long-term identity: a self-signed ECDSA P-256 certificate kept in
/// the keychain as a SecIdentity (required by Network.framework's TLS), plus the
/// SHA-256 fingerprint the sender pins at pairing time.
public struct DeviceIdentity: @unchecked Sendable {
    public let deviceId: String
    public let deviceName: String
    public let secIdentity: SecIdentity
    public let certificateDER: Data

    public var fingerprint: String {
        Base64Url.encode(Data(SHA256.hash(data: certificateDER)))
    }

    private static let certLabel = "QuackSnap Identity"
    private static let deviceIdKey = "quacksnap.deviceId"

    public static func loadOrCreate(deviceName: String) throws -> DeviceIdentity {
        let deviceId: String
        if let existing = UserDefaults.standard.string(forKey: deviceIdKey) {
            deviceId = existing
        } else {
            deviceId = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
            UserDefaults.standard.set(deviceId, forKey: deviceIdKey)
        }

        if let identity = try? findExisting() {
            return DeviceIdentity(deviceId: deviceId, deviceName: deviceName,
                                  secIdentity: identity.identity, certificateDER: identity.der)
        }

        let created = try createAndStore(deviceName: deviceName)
        return DeviceIdentity(deviceId: deviceId, deviceName: deviceName,
                              secIdentity: created.identity, certificateDER: created.der)
    }

    // MARK: - keychain plumbing

    private static func findExisting() throws -> (identity: SecIdentity, der: Data)? {
        var result: CFTypeRef?
        let status = SecItemCopyMatching([
            kSecClass: kSecClassIdentity,
            kSecAttrLabel: certLabel,
            kSecReturnRef: true,
        ] as CFDictionary, &result)
        guard status == errSecSuccess, let ref = result else { return nil }
        let identity = ref as! SecIdentity

        var certRef: SecCertificate?
        guard SecIdentityCopyCertificate(identity, &certRef) == errSecSuccess, let cert = certRef else {
            return nil
        }
        return (identity, SecCertificateCopyData(cert) as Data)
    }

    private static func createAndStore(deviceName: String) throws -> (identity: SecIdentity, der: Data) {
        // 1. Key + self-signed certificate (swift-certificates).
        let privateKey = P256.Signing.PrivateKey()
        let certKey = Certificate.PrivateKey(privateKey)
        let subject = try DistinguishedName {
            CommonName("QuackSnap \(deviceName)")
        }
        let now = Date()
        let certificate = try Certificate(
            version: .v3,
            serialNumber: Certificate.SerialNumber(),
            publicKey: certKey.publicKey,
            notValidBefore: now.addingTimeInterval(-86_400),
            notValidAfter: now.addingTimeInterval(86_400 * 365 * 20),
            issuer: subject,
            subject: subject,
            signatureAlgorithm: .ecdsaWithSHA256,
            extensions: Certificate.Extensions {
                Critical(BasicConstraints.notCertificateAuthority)
            },
            issuerPrivateKey: certKey)

        var serializer = DER.Serializer()
        try serializer.serialize(certificate)
        let der = Data(serializer.serializedBytes)

        // 2. Import both halves into the keychain so a SecIdentity exists.
        guard let secKey = SecKeyCreateWithData(privateKey.x963Representation as CFData, [
            kSecAttrKeyType: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeyClass: kSecAttrKeyClassPrivate,
        ] as CFDictionary, nil) else {
            throw QuackSnapError.transferFailed("Could not create SecKey from generated key")
        }
        guard let secCert = SecCertificateCreateWithData(nil, der as CFData) else {
            throw QuackSnapError.transferFailed("Could not parse generated certificate")
        }

        var status = SecItemAdd([
            kSecClass: kSecClassKey,
            kSecValueRef: secKey,
            kSecAttrLabel: certLabel,
        ] as CFDictionary, nil)
        guard status == errSecSuccess || status == errSecDuplicateItem else {
            throw QuackSnapError.transferFailed("Keychain key import failed (\(status))")
        }

        status = SecItemAdd([
            kSecClass: kSecClassCertificate,
            kSecValueRef: secCert,
            kSecAttrLabel: certLabel,
        ] as CFDictionary, nil)
        guard status == errSecSuccess || status == errSecDuplicateItem else {
            throw QuackSnapError.transferFailed("Keychain certificate import failed (\(status))")
        }

        // 3. The keychain pairs them up by public key; fetch the combined identity.
        guard let found = try findExisting() else {
            throw QuackSnapError.transferFailed("Keychain did not return the new identity")
        }
        return found
    }

    /// Loads an identity from PKCS#12 bytes instead of generating one. Used by dev
    /// harnesses (macOS CLIs can't SecItemAdd raw keys without entitlements) and
    /// doubles as a cross-stack check that .NET-minted certs work in Swift TLS.
    public static func fromPKCS12(_ data: Data, password: String, deviceName: String) throws -> DeviceIdentity {
        var rawItems: CFArray?
        let status = SecPKCS12Import(data as CFData, [
            kSecImportExportPassphrase: password,
        ] as CFDictionary, &rawItems)
        guard status == errSecSuccess,
              let items = rawItems as? [[String: Any]],
              let first = items.first,
              let identityAny = first[kSecImportItemIdentity as String] else {
            throw QuackSnapError.transferFailed("PKCS#12 import failed (\(status))")
        }
        let identity = identityAny as! SecIdentity

        var certRef: SecCertificate?
        guard SecIdentityCopyCertificate(identity, &certRef) == errSecSuccess, let cert = certRef else {
            throw QuackSnapError.transferFailed("PKCS#12 had no certificate")
        }

        let deviceId: String
        if let existing = UserDefaults.standard.string(forKey: deviceIdKey) {
            deviceId = existing
        } else {
            deviceId = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
            UserDefaults.standard.set(deviceId, forKey: deviceIdKey)
        }
        return DeviceIdentity(deviceId: deviceId, deviceName: deviceName,
                              secIdentity: identity, certificateDER: SecCertificateCopyData(cert) as Data)
    }

    /// Removes the stored identity (used when re-pairing from scratch).
    public static func reset() {
        SecItemDelete([kSecClass: kSecClassIdentity, kSecAttrLabel: certLabel] as CFDictionary)
        SecItemDelete([kSecClass: kSecClassCertificate, kSecAttrLabel: certLabel] as CFDictionary)
        SecItemDelete([kSecClass: kSecClassKey, kSecAttrLabel: certLabel] as CFDictionary)
    }
}
