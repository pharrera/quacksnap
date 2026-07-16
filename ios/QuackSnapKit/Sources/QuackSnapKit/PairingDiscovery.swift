import Foundation
import Network

/// Finds computers with an open pairing window via Bonjour. Browsing needs no
/// special entitlement on iOS (unlike raw multicast) — just the NSBonjourServices
/// Info.plist entry and Local Network permission.
public enum PairingDiscovery {
    public static let serviceType = "_quacksnap-pair._tcp"

    public struct Host: Sendable {
        public let endpoint: NWEndpoint
        public let name: String
    }

    /// Returns the first advertised pairing host, or throws after `timeout` seconds.
    /// The timeout is wired into the continuation directly (asyncAfter) because a
    /// task-group race would deadlock: groups await all children, and a pending
    /// browse continuation ignores cancellation.
    public static func discoverFirst(timeout: Double = 8) async throws -> Host {
        let browser = NWBrowser(
            for: .bonjour(type: serviceType, domain: nil),
            using: {
                let params = NWParameters()
                params.includePeerToPeer = true
                return params
            }())

        return try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Host, Error>) in
            let resumed = ResumeGuard()
            browser.browseResultsChangedHandler = { results, _ in
                guard let result = results.first else { return }
                var name = "Computer"
                if case let .service(serviceName, _, _, _) = result.endpoint {
                    name = serviceName
                }
                if resumed.claim() {
                    browser.cancel()
                    cont.resume(returning: Host(endpoint: result.endpoint, name: name))
                }
            }
            browser.stateUpdateHandler = { state in
                if case let .failed(error) = state, resumed.claim() {
                    browser.cancel()
                    cont.resume(throwing: error)
                }
            }
            browser.start(queue: .global(qos: .userInitiated))
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout) {
                if resumed.claim() {
                    browser.cancel()
                    cont.resume(throwing: QuackSnapError.pairingFailed(
                        "No computer with an open pairing window was found on this network"))
                }
            }
        }
    }
}

/// Continuations may be resumed exactly once; browser callbacks fire repeatedly.
final class ResumeGuard: @unchecked Sendable {
    private let lock = NSLock()
    private var claimed = false

    func claim() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if claimed { return false }
        claimed = true
        return true
    }
}
