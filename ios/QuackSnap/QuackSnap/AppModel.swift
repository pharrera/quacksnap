import Foundation
import Observation
import Photos
import QuackSnapKit
import SwiftUI
import UIKit
import UserNotifications

struct Shot: Identifiable, Equatable {
    static let imageExtensions: Set<String> = ["png", "jpg", "jpeg", "gif", "webp", "bmp", "heic", "heif", "tiff"]
    static let textExtensions: Set<String> = ["txt", "md", "csv", "json", "xml", "log", "rtf"]

    let id: URL
    let name: String
    let date: Date

    var url: URL { id }
    var isImage: Bool { Self.imageExtensions.contains(url.pathExtension.lowercased()) }
}

/// Transient toast shown when a file lands while the app is open.
struct ReceivedBanner: Identifiable, Equatable {
    let id = UUID()
    let name: String
    let from: String
    let url: URL
    var isImage: Bool { Shot.imageExtensions.contains(url.pathExtension.lowercased()) }
}

@Observable
@MainActor
final class AppModel {
    private(set) var identity: DeviceIdentity?
    private(set) var peer: PairedSender?
    private(set) var shots: [Shot] = []
    private(set) var isListening = false
    private(set) var banner: ReceivedBanner?
    private(set) var sendState: SendState = .idle
    var startupError: String?

    private var bannerDismiss: Task<Void, Never>?
    private var sendDismiss: Task<Void, Never>?

    // M4 settings — shared defaults so the notification extension honors them too.
    var copyToClipboard: Bool {
        didSet { SharedStore.defaults.set(copyToClipboard, forKey: "quacksnap.copyToClipboard") }
    }
    var saveToPhotos: Bool {
        didSet { SharedStore.defaults.set(saveToPhotos, forKey: "quacksnap.saveToPhotos") }
    }

    private var server: TransferServer?

    /// Shared with the notification extension via the app group.
    let inbox = SharedStore.inboxDirectory

    init() {
        copyToClipboard = SharedStore.defaults.object(forKey: "quacksnap.copyToClipboard") as? Bool ?? true
        saveToPhotos = SharedStore.defaults.bool(forKey: "quacksnap.saveToPhotos")
        do {
            identity = try DeviceIdentity.loadOrCreate(
                deviceName: UIDevice.current.name, accessGroup: SharedStore.appGroup)
        } catch {
            startupError = error.localizedDescription
        }
        peer = SharedStore.loadPeer()
        refreshShots()

        #if DEBUG
        // UI-testing/screenshot seam: `SIMCTL_CHILD_QUACKSNAP_DEMO=1` seeds an
        // in-memory paired device so the gallery is reachable without a live PC.
        // Never runs in release builds and never persists anything.
        if peer == nil, ProcessInfo.processInfo.environment["QUACKSNAP_DEMO"] == "1" {
            peer = PairedSender(deviceId: "demo", name: "Peter's PC", certFp: "demo")
        }
        #endif
    }

    // MARK: - pairing

    func pair(with uri: String) async throws {
        guard let identity else {
            throw QuackSnapError.pairingFailed(startupError ?? "No device identity")
        }
        let payload = try PairingPayload.parse(uri)
        let sender = try await PairingClient.pair(payload: payload, identity: identity, listenPort: 47820)
        await finishPairing(with: sender)
    }

    /// LocalSend-style: type the 6-digit code, discovery finds the computer.
    func pair(withCode code: String) async throws {
        guard let identity else {
            throw QuackSnapError.pairingFailed(startupError ?? "No device identity")
        }
        let sender = try await PairingClient.pair(code: code, identity: identity, listenPort: 47820)
        await finishPairing(with: sender)
    }

    private func finishPairing(with sender: PairedSender) async {
        peer = sender
        SharedStore.savePeer(sender)
        UINotificationFeedbackGenerator().notificationOccurred(.success)
        startReceiving()
        await requestNotificationPermission()
        registerWithRelay(sender)
    }

    func unpair() {
        stopReceiving()
        peer = nil
        SharedStore.savePeer(nil)
    }

    // MARK: - sending (iPhone → computer)

    enum SendState: Equatable {
        case idle
        case sending(name: String, fraction: Double)
        case done(count: Int)
        case failed(String)
    }

    /// Sends picked files to the paired computer, discovering it on the network.
    func send(_ urls: [URL]) {
        guard !urls.isEmpty, let peer, let identity else { return }
        sendDismiss?.cancel()
        withAnimation(.snappy) { sendState = .sending(name: "", fraction: 0) }
        Task {
            do {
                try await SendClient.send(urls, to: peer, identity: identity, progress: { p in
                    Task { @MainActor [weak self] in
                        self?.sendState = .sending(
                            name: p.name, fraction: p.total > 0 ? Double(p.sent) / Double(p.total) : 0)
                    }
                })
                UINotificationFeedbackGenerator().notificationOccurred(.success)
                withAnimation(.snappy) { sendState = .done(count: urls.count) }
            } catch {
                withAnimation(.snappy) { sendState = .failed(error.localizedDescription) }
            }
            sendDismiss = Task { [weak self] in
                try? await Task.sleep(for: .seconds(3))
                guard !Task.isCancelled else { return }
                await MainActor.run { withAnimation(.snappy) { self?.sendState = .idle } }
            }
        }
    }

    func clearSendState() {
        sendDismiss?.cancel()
        withAnimation(.snappy) { sendState = .idle }
    }

    /// Tells the relay where to push for this device. With the dev relay this is a
    /// no-op; in production the token arrives via didRegisterForRemoteNotifications
    /// and should be re-posted here.
    private func registerWithRelay(_ sender: PairedSender) {
        guard let relayUrl = sender.relayUrl, let url = URL(string: "\(relayUrl)/v1/register"),
              let identity else { return }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: [
            "deviceId": identity.deviceId,
            "token": "simulator-dev",
        ])
        URLSession.shared.dataTask(with: request).resume()
    }

    // MARK: - receiving

    func startReceiving() {
        guard server == nil, let identity, let peer else { return }
        let server = TransferServer(
            identity: identity,
            peer: peer,
            inboxDirectory: inbox,
            onReceived: { [weak self] file in
                Task { @MainActor [weak self] in
                    self?.handleReceived(file)
                }
            },
            onStateChange: { [weak self] state in
                Task { @MainActor [weak self] in
                    self?.isListening = state == "ready"
                }
            })
        do {
            try server.start()
            self.server = server
        } catch {
            startupError = error.localizedDescription
        }
    }

    func stopReceiving() {
        server?.stop()
        server = nil
        isListening = false
    }

    private func handleReceived(_ file: ReceivedFile) {
        refreshShots()
        if copyToClipboard {
            copyToPasteboard(file)
        }
        if saveToPhotos {
            saveImageToPhotos(file.url)
        }
        if UIApplication.shared.applicationState == .active {
            showBanner(for: file)
        } else {
            postNotification(for: file)
        }
    }

    /// Images land as an image, text lands as a string — so "copy on Windows →
    /// paste on iPhone" works for both screenshots and highlighted text.
    private func copyToPasteboard(_ file: ReceivedFile) {
        let ext = file.url.pathExtension.lowercased()
        if Shot.imageExtensions.contains(ext), let image = UIImage(contentsOfFile: file.url.path) {
            UIPasteboard.general.image = image
        } else if Shot.textExtensions.contains(ext),
                  let text = try? String(contentsOf: file.url, encoding: .utf8) {
            UIPasteboard.general.string = text
        }
    }

    private func showBanner(for file: ReceivedFile) {
        UIImpactFeedbackGenerator(style: .soft).impactOccurred()
        withAnimation(.snappy) { banner = ReceivedBanner(name: file.name, from: file.from, url: file.url) }
        bannerDismiss?.cancel()
        bannerDismiss = Task { [weak self] in
            try? await Task.sleep(for: .seconds(3))
            guard !Task.isCancelled else { return }
            await MainActor.run { self?.dismissBanner() }
        }
    }

    func dismissBanner() {
        withAnimation(.snappy) { banner = nil }
    }

    private func saveImageToPhotos(_ url: URL) {
        guard Shot.imageExtensions.contains(url.pathExtension.lowercased()) else { return }
        PHPhotoLibrary.requestAuthorization(for: .addOnly) { status in
            guard status == .authorized || status == .limited else { return }
            PHPhotoLibrary.shared().performChanges {
                PHAssetChangeRequest.creationRequestForAssetFromImage(atFileURL: url)
            }
        }
    }

    // MARK: - gallery

    func refreshShots() {
        let contents = (try? FileManager.default.contentsOfDirectory(
            at: inbox,
            includingPropertiesForKeys: [.creationDateKey],
            options: .skipsHiddenFiles)) ?? []
        shots = contents
            .filter { $0.pathExtension.lowercased() != "part" }
            .map { url in
                let date = (try? url.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? .distantPast
                return Shot(id: url, name: url.lastPathComponent, date: date)
            }
            .sorted { $0.date > $1.date }
    }

    func delete(_ shot: Shot) {
        try? FileManager.default.removeItem(at: shot.url)
        refreshShots()
    }

    func delete(_ shots: [Shot]) {
        for shot in shots {
            try? FileManager.default.removeItem(at: shot.url)
        }
        refreshShots()
    }

    // MARK: - notifications

    private func requestNotificationPermission() async {
        _ = try? await UNUserNotificationCenter.current()
            .requestAuthorization(options: [.alert, .sound])
    }

    private func postNotification(for file: ReceivedFile) {
        let content = UNMutableNotificationContent()
        content.title = "From \(file.from)"
        content.body = file.name

        // Attachments are moved by the system, so hand it a copy.
        let copy = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .appendingPathExtension(file.url.pathExtension.isEmpty ? "png" : file.url.pathExtension)
        if (try? FileManager.default.copyItem(at: file.url, to: copy)) != nil,
           let attachment = try? UNNotificationAttachment(identifier: "shot", url: copy) {
            content.attachments = [attachment]
        }

        UNUserNotificationCenter.current().add(
            UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
    }
}
