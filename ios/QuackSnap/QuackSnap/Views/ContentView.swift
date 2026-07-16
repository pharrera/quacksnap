import PhotosUI
import SwiftUI

struct ContentView: View {
    @Environment(AppModel.self) private var model
    @State private var showSettings = false
    @State private var showPhotos = false
    @State private var showFiles = false
    @State private var photoItems: [PhotosPickerItem] = []

    var body: some View {
        NavigationStack {
            ZStack(alignment: .top) {
                Group {
                    if model.peer == nil {
                        PairView()
                            .transition(.opacity)
                    } else {
                        GalleryView()
                            .transition(.opacity)
                    }
                }
                .animation(.smooth, value: model.peer == nil)

                if let banner = model.banner {
                    ReceivedToast(banner: banner)
                        .padding(.horizontal, 14)
                        .padding(.top, 6)
                        .transition(.move(edge: .top).combined(with: .opacity))
                        .onTapGesture { model.dismissBanner() }
                        .zIndex(2)
                }
            }
            .safeAreaInset(edge: .bottom) { sendStatusBar }
            .navigationTitle("QuackSnap")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    BrandMark(size: 28)
                        .accessibilityLabel("QuackSnap")
                }
                if model.peer != nil {
                    ToolbarItem(placement: .topBarTrailing) {
                        Menu {
                            Button { showPhotos = true } label: { Label("Send photos", systemImage: "photo") }
                            Button { showFiles = true } label: { Label("Send files", systemImage: "folder") }
                        } label: {
                            Image(systemName: "paperplane.fill")
                                .foregroundStyle(Theme.brand)
                        }
                        .accessibilityLabel("Send to computer")
                    }
                    ToolbarItem(placement: .topBarTrailing) {
                        Button {
                            showSettings = true
                        } label: {
                            StatusPill(listening: model.isListening)
                        }
                        .accessibilityLabel("Settings")
                        .accessibilityValue(model.isListening ? "Live" : "Asleep")
                    }
                }
            }
            .toolbarBackground(.visible, for: .navigationBar)
        }
        .tint(Theme.brand)
        .photosPicker(isPresented: $showPhotos, selection: $photoItems, matching: .any(of: [.images, .videos]))
        .onChange(of: photoItems) { _, items in
            guard !items.isEmpty else { return }
            Task {
                let urls = await materialize(items)
                photoItems = []
                model.send(urls)
            }
        }
        .fileImporter(isPresented: $showFiles, allowedContentTypes: [.item], allowsMultipleSelection: true) { result in
            if case let .success(urls) = result { model.send(urls) }
        }
        .sheet(isPresented: $showSettings) {
            SettingsView()
        }
        .alert("Something went wrong", isPresented: .constant(model.startupError != nil)) {
            Button("OK") { model.startupError = nil }
        } message: {
            Text(model.startupError ?? "")
        }
    }

    /// Photos come back as raw data; write each to a temp file so SendClient can stream it.
    private func materialize(_ items: [PhotosPickerItem]) async -> [URL] {
        var urls: [URL] = []
        for item in items {
            guard let data = try? await item.loadTransferable(type: Data.self) else { continue }
            let ext = item.supportedContentTypes.first?.preferredFilenameExtension ?? "jpg"
            let stamp = Int(Date().timeIntervalSince1970 * 1000)
            let url = FileManager.default.temporaryDirectory
                .appendingPathComponent("Photo \(stamp).\(ext)")
            if (try? data.write(to: url)) != nil { urls.append(url) }
        }
        return urls
    }

    @ViewBuilder private var sendStatusBar: some View {
        switch model.sendState {
        case .idle:
            EmptyView()
        case let .sending(name, fraction):
            SendStatus(icon: "paperplane.fill", tint: Theme.brand,
                       title: name.isEmpty ? "Connecting to your computer…" : "Sending \(name)",
                       fraction: fraction)
        case let .done(count):
            SendStatus(icon: "checkmark.circle.fill", tint: Color(hex: 0x34C759),
                       title: count == 1 ? "Sent to your computer" : "Sent \(count) files", fraction: nil)
                .onTapGesture { model.clearSendState() }
        case let .failed(message):
            SendStatus(icon: "exclamationmark.triangle.fill", tint: Theme.coral,
                       title: message, fraction: nil)
                .onTapGesture { model.clearSendState() }
        }
    }
}

private struct SendStatus: View {
    let icon: String
    let tint: Color
    let title: String
    let fraction: Double?

    var body: some View {
        VStack(spacing: 8) {
            HStack(spacing: 10) {
                Image(systemName: icon).foregroundStyle(tint)
                Text(title).font(.subheadline).lineLimit(1)
                Spacer(minLength: 0)
                if fraction != nil { ProgressView().controlSize(.small) }
            }
            if let fraction {
                ProgressView(value: fraction).tint(tint)
            }
        }
        .padding(12)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous)
            .strokeBorder(Color(.separator).opacity(0.4), lineWidth: 0.5))
        .shadow(color: .black.opacity(0.12), radius: 14, y: 5)
        .padding(.horizontal, 14)
        .padding(.bottom, 6)
        .transition(.move(edge: .bottom).combined(with: .opacity))
    }
}

/// Green when the LAN listener is up, muted when asleep (relay still delivers).
private struct StatusPill: View {
    let listening: Bool

    var body: some View {
        HStack(spacing: 5) {
            Circle()
                .fill(listening ? Color(hex: 0x34C759) : Color(.tertiaryLabel))
                .frame(width: 7, height: 7)
            Text(listening ? "Live" : "Asleep")
                .font(.caption.weight(.semibold))
                .foregroundStyle(listening ? Color(hex: 0x248A3D) : Color(.secondaryLabel))
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(
            Capsule().fill(listening ? Color(hex: 0x34C759).opacity(0.14) : Color(.tertiarySystemFill)))
    }
}

/// In-app toast when a file lands while you're looking at the app.
private struct ReceivedToast: View {
    let banner: ReceivedBanner

    var body: some View {
        HStack(spacing: 12) {
            thumb
            VStack(alignment: .leading, spacing: 1) {
                Text(banner.name)
                    .font(.subheadline.weight(.semibold))
                    .lineLimit(1)
                Text("from \(banner.from)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
            Image(systemName: "checkmark.circle.fill")
                .foregroundStyle(Color(hex: 0x34C759))
        }
        .padding(10)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .strokeBorder(Color(.separator).opacity(0.4), lineWidth: 0.5))
        .shadow(color: .black.opacity(0.14), radius: 16, y: 6)
    }

    @ViewBuilder private var thumb: some View {
        if banner.isImage, let image = UIImage(contentsOfFile: banner.url.path) {
            Image(uiImage: image)
                .resizable()
                .scaledToFill()
                .frame(width: 40, height: 40)
                .clipShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
        } else {
            RoundedRectangle(cornerRadius: 9, style: .continuous)
                .fill(Theme.softGradient)
                .frame(width: 40, height: 40)
                .overlay(Image(systemName: "doc.fill").foregroundStyle(Theme.brand))
        }
    }
}
