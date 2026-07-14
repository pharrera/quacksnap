import Foundation
import Observation
import QuackSnapKit
import UIKit
import UserNotifications

struct Shot: Identifiable, Equatable {
    let id: URL
    let name: String
    let date: Date

    var url: URL { id }
}

@Observable
@MainActor
final class AppModel {
    private(set) var identity: DeviceIdentity?
    private(set) var peer: PairedSender?
    private(set) var shots: [Shot] = []
    private(set) var isListening = false
    var startupError: String?

    private var server: TransferServer?
    private static let peerKey = "quacksnap.pairedSender"

    let inbox: URL = {
        let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        return documents.appendingPathComponent("Inbox", isDirectory: true)
    }()

    init() {
        do {
            identity = try DeviceIdentity.loadOrCreate(deviceName: UIDevice.current.name)
        } catch {
            startupError = error.localizedDescription
        }
        if let data = UserDefaults.standard.data(forKey: Self.peerKey) {
            peer = try? WireJSON.decode(PairedSender.self, from: data)
        }
        refreshShots()
    }

    // MARK: - pairing

    func pair(with uri: String) async throws {
        guard let identity else {
            throw QuackSnapError.pairingFailed(startupError ?? "No device identity")
        }
        let payload = try PairingPayload.parse(uri)
        let sender = try await PairingClient.pair(payload: payload, identity: identity, listenPort: 47820)
        peer = sender
        UserDefaults.standard.set(try WireJSON.encode(sender), forKey: Self.peerKey)
        startReceiving()
        await requestNotificationPermission()
    }

    func unpair() {
        stopReceiving()
        peer = nil
        UserDefaults.standard.removeObject(forKey: Self.peerKey)
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
        if UIApplication.shared.applicationState != .active {
            postNotification(for: file)
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

    // MARK: - notifications

    private func requestNotificationPermission() async {
        _ = try? await UNUserNotificationCenter.current()
            .requestAuthorization(options: [.alert, .sound])
    }

    private func postNotification(for file: ReceivedFile) {
        let content = UNMutableNotificationContent()
        content.title = "Screenshot from \(file.from)"
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
