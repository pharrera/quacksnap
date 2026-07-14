import Foundation
import QuackSnapKit

// Dev harness that exercises QuackSnapKit exactly the way the iOS app does,
// but runnable on macOS so the Swift receiver can be tested against the real
// .NET sender without a phone.
//
//   quacksnap-cli pair "quacksnap://pair?..."
//   quacksnap-cli listen

let stateDir = FileManager.default.homeDirectoryForCurrentUser
    .appendingPathComponent("Library/Application Support/quacksnap-cli")
try? FileManager.default.createDirectory(at: stateDir, withIntermediateDirectories: true)
let peerFile = stateDir.appendingPathComponent("peer.json")
let inbox = URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent("received-swift")

let arguments = CommandLine.arguments
guard arguments.count >= 2 else {
    print("Usage: quacksnap-cli pair \"<quacksnap://pair?...>\" | quacksnap-cli listen")
    exit(1)
}

// macOS CLIs can't add raw keys to the keychain without entitlements, so the
// harness pre-generates a PKCS#12 (via the .NET CertUtil) at identity.p12.
let deviceName = Host.current().localizedName ?? "Mac"
let p12Path = stateDir.appendingPathComponent("identity.p12")
let identity: DeviceIdentity
if let p12 = try? Data(contentsOf: p12Path) {
    identity = try DeviceIdentity.fromPKCS12(p12, password: "quacksnap-dev", deviceName: deviceName)
} else {
    identity = try DeviceIdentity.loadOrCreate(deviceName: deviceName)
}
print("Identity fp: \(identity.fingerprint)")

switch arguments[1] {
case "pair":
    guard arguments.count >= 3 else {
        print("Missing pairing URI")
        exit(1)
    }
    let payload = try PairingPayload.parse(arguments[2])
    print("Pairing with \(payload.name) at \(payload.hosts.joined(separator: ", ")):\(payload.port)…")
    let semaphore = DispatchSemaphore(value: 0)
    Task {
        do {
            let sender = try await PairingClient.pair(payload: payload, identity: identity, listenPort: 47820)
            try WireJSON.encode(sender).write(to: peerFile)
            print("PAIRED with \(sender.name) fp=\(sender.certFp)")
        } catch {
            print("PAIRING FAILED: \(error.localizedDescription)")
            exit(2)
        }
        semaphore.signal()
    }
    semaphore.wait()

case "listen":
    guard let peerData = try? Data(contentsOf: peerFile),
          let peer = try? WireJSON.decode(PairedSender.self, from: peerData) else {
        print("Not paired yet. Run: quacksnap-cli pair \"<quacksnap://...>\"")
        exit(1)
    }
    print("Paired sender: \(peer.name) (fp \(peer.certFp.prefix(12))…)")
    let server = TransferServer(
        identity: identity,
        peer: peer,
        inboxDirectory: inbox,
        onReceived: { file in
            print("  ✓ received \(file.name) (\(file.size / 1024) KB) from \(file.from) → \(file.url.path)")
        },
        onStateChange: { state in
            print("listener: \(state)")
        })
    try server.start()
    print("Listening on port 47820; saving to \(inbox.path)")
    dispatchMain()

default:
    print("Unknown command \(arguments[1])")
    exit(1)
}
