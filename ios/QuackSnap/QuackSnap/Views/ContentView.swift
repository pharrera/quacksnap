import SwiftUI

struct ContentView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        NavigationStack {
            Group {
                if model.peer == nil {
                    PairView()
                } else {
                    GalleryView()
                }
            }
            .navigationTitle("QuackSnap")
            .toolbar {
                if let peer = model.peer {
                    ToolbarItem(placement: .topBarTrailing) {
                        Menu {
                            Label(peer.name, systemImage: "desktopcomputer")
                            Divider()
                            Button(role: .destructive) {
                                model.unpair()
                            } label: {
                                Label("Unpair", systemImage: "link.badge.plus")
                            }
                        } label: {
                            Image(systemName: model.isListening ? "dot.radiowaves.left.and.right" : "moon.zzz")
                                .foregroundStyle(model.isListening ? .green : .secondary)
                        }
                    }
                }
            }
        }
        .alert("Something went wrong", isPresented: .constant(model.startupError != nil)) {
            Button("OK") { model.startupError = nil }
        } message: {
            Text(model.startupError ?? "")
        }
    }
}
