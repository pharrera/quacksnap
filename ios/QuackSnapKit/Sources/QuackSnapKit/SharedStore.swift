import Foundation

/// Storage shared between the app and the notification extension via the app
/// group: the paired sender record and the inbox folder both must be visible to
/// whichever process receives a file.
public enum SharedStore {
    public static let appGroup = "group.com.peterherrera.quacksnap"
    private static let peerKey = "quacksnap.pairedSender"

    public static var defaults: UserDefaults {
        UserDefaults(suiteName: appGroup) ?? .standard
    }

    /// App-group inbox when the entitlement is present, Documents/Inbox otherwise.
    public static var inboxDirectory: URL {
        let base = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: appGroup)
            ?? FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let inbox = base.appendingPathComponent("Inbox", isDirectory: true)
        try? FileManager.default.createDirectory(at: inbox, withIntermediateDirectories: true)
        return inbox
    }

    public static func loadPeer() -> PairedSender? {
        guard let data = defaults.data(forKey: peerKey) else { return nil }
        return try? WireJSON.decode(PairedSender.self, from: data)
    }

    public static func savePeer(_ peer: PairedSender?) {
        if let peer, let data = try? WireJSON.encode(peer) {
            defaults.set(data, forKey: peerKey)
        } else {
            defaults.removeObject(forKey: peerKey)
        }
    }

    public static func uniqueDestination(for name: String) -> URL {
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
