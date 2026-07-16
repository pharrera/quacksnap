import QuackSnapKit
import UserNotifications

/// Runs when a relay push arrives while the app is suspended — the path that
/// makes background delivery real. Downloads the ciphertext, verifies the
/// sender's signature, decrypts with the shared keychain identity, drops the
/// file into the shared inbox, and attaches a preview to the notification.
final class NotificationService: UNNotificationServiceExtension {
    private var handler: ((UNNotificationContent) -> Void)?
    private var fallbackContent: UNMutableNotificationContent?

    override func didReceive(
        _ request: UNNotificationRequest,
        withContentHandler contentHandler: @escaping (UNNotificationContent) -> Void
    ) {
        handler = contentHandler
        let content = (request.content.mutableCopy() as? UNMutableNotificationContent) ?? UNMutableNotificationContent()
        fallbackContent = content

        guard let envelopeDict = request.content.userInfo["qsEnvelope"],
              let envelopeData = try? JSONSerialization.data(withJSONObject: envelopeDict),
              let envelope = try? WireJSON.decode(Envelope.self, from: envelopeData) else {
            contentHandler(content)
            return
        }

        Task {
            do {
                let file = try await receive(envelope)
                content.body = envelope.name
                if let attachment = Self.attachment(for: file, name: envelope.name) {
                    content.attachments = [attachment]
                }
            } catch {
                content.body = "\(envelope.name) (couldn't decrypt: \(error.localizedDescription))"
            }
            contentHandler(content)
        }
    }

    override func serviceExtensionTimeWillExpire() {
        // Out of time — show what we have rather than nothing.
        if let handler, let fallbackContent {
            handler(fallbackContent)
        }
    }

    private func receive(_ envelope: Envelope) async throws -> URL {
        guard let peer = SharedStore.loadPeer(), let senderCert = peer.certDer else {
            throw QuackSnapError.transferFailed("Not paired or sender certificate missing")
        }
        guard let identity = DeviceIdentity.loadExisting(deviceName: "iPhone", accessGroup: SharedStore.appGroup) else {
            throw QuackSnapError.transferFailed("Identity not in shared keychain")
        }
        guard let url = URL(string: "\(envelope.relayUrl)/v1/blob/\(envelope.blobId)") else {
            throw QuackSnapError.transferFailed("Bad relay URL")
        }

        let (ciphertext, response) = try await URLSession.shared.data(from: url)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw QuackSnapError.transferFailed("Blob not on relay (expired?)")
        }

        let plaintext = try EnvelopeCrypto.open(
            envelope, blobCiphertext: ciphertext, identity: identity, senderCertDER: senderCert)

        let destination = SharedStore.uniqueDestination(for: envelope.name)
        try plaintext.write(to: destination)
        return destination
    }

    private static func attachment(for file: URL, name: String) -> UNNotificationAttachment? {
        // The system moves attachments, so attach a copy and keep the inbox file.
        let ext = (name as NSString).pathExtension
        let copy = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .appendingPathExtension(ext.isEmpty ? "png" : ext)
        guard (try? FileManager.default.copyItem(at: file, to: copy)) != nil else { return nil }
        return try? UNNotificationAttachment(identifier: "file", url: copy)
    }
}
