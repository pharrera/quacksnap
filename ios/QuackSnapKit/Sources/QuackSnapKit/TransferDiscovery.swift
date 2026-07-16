import Foundation
import Network

/// Finds a paired computer's receive service (`_quacksnap._tcp`) on the network,
/// matching the TXT `id` to the paired device so we connect to the right PC.
public enum TransferDiscovery {
    public static let serviceType = "_quacksnap._tcp"

    /// Resolves the endpoint advertised by the device with `deviceId`, or throws.
    public static func find(deviceId: String, timeout: Double = 6) async throws -> NWEndpoint {
        let browser = NWBrowser(for: .bonjour(type: serviceType, domain: nil), using: NWParameters())

        return try await withCheckedThrowingContinuation { (cont: CheckedContinuation<NWEndpoint, Error>) in
            let resumed = ResumeGuard()

            browser.browseResultsChangedHandler = { results, _ in
                for result in results {
                    // Prefer an exact device-id match from the TXT record; fall back
                    // to the only advertised service if none carries an id.
                    if case let .bonjour(txt) = result.metadata, txt["id"] == deviceId {
                        if resumed.claim() { browser.cancel(); cont.resume(returning: result.endpoint) }
                        return
                    }
                }
                if results.count == 1, let only = results.first, resumed.claim() {
                    browser.cancel()
                    cont.resume(returning: only.endpoint)
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
                    cont.resume(throwing: QuackSnapError.transferFailed(
                        "Your paired computer isn't reachable on this network"))
                }
            }
        }
    }
}
