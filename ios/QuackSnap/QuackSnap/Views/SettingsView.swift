import SwiftUI

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        @Bindable var model = model
        NavigationStack {
            Form {
                if let peer = model.peer {
                    Section {
                        HStack(spacing: 14) {
                            BrandMark(size: 48)
                            VStack(alignment: .leading, spacing: 3) {
                                Text(peer.name)
                                    .font(.headline)
                                Label(
                                    peer.relayUrl == nil ? "On this network" : "Anywhere, via relay",
                                    systemImage: peer.relayUrl == nil ? "wifi" : "cloud.fill")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                    .labelStyle(.titleAndIcon)
                            }
                            Spacer()
                        }
                        .padding(.vertical, 4)
                    }
                }

                Section("When a file arrives") {
                    settingRow("doc.on.clipboard", Color(hex: 0x378ADD),
                               "Copy images to clipboard", isOn: $model.copyToClipboard)
                    settingRow("photo.on.rectangle", Color(hex: 0x1D9E75),
                               "Save images to Photos", isOn: $model.saveToPhotos)
                }

                Section {
                    Button(role: .destructive) {
                        model.unpair()
                        dismiss()
                    } label: {
                        Label("Unpair computer", systemImage: "minus.circle")
                    }
                }

                Section {
                    HStack {
                        Spacer()
                        VStack(spacing: 8) {
                            BrandMark(size: 40)
                            Text("QuackSnap")
                                .font(.footnote.weight(.semibold))
                            Text("Instant transfer between Windows and iPhone")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                                .multilineTextAlignment(.center)
                        }
                        Spacer()
                    }
                    .padding(.vertical, 8)
                    .listRowBackground(Color.clear)
                }
            }
            .navigationTitle("Settings")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Done") { dismiss() }
                }
            }
        }
        .tint(Theme.brand)
    }

    private func settingRow(_ icon: String, _ color: Color, _ title: String, isOn: Binding<Bool>) -> some View {
        Toggle(isOn: isOn) {
            HStack(spacing: 12) {
                Image(systemName: icon)
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(.white)
                    .frame(width: 28, height: 28)
                    .background(color, in: RoundedRectangle(cornerRadius: 7, style: .continuous))
                Text(title)
            }
        }
        .tint(Theme.brand)
    }
}
