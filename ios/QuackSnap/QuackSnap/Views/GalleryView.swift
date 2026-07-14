import SwiftUI

struct GalleryView: View {
    @Environment(AppModel.self) private var model
    @State private var selected: Shot?

    private let columns = [GridItem(.adaptive(minimum: 110), spacing: 4)]

    var body: some View {
        Group {
            if model.shots.isEmpty {
                ContentUnavailableView(
                    "No screenshots yet",
                    systemImage: "photo.on.rectangle.angled",
                    description: Text("Take a screenshot on your computer and it will appear here."))
            } else {
                ScrollView {
                    LazyVGrid(columns: columns, spacing: 4) {
                        ForEach(model.shots) { shot in
                            ShotThumbnail(shot: shot)
                                .onTapGesture { selected = shot }
                                .contextMenu {
                                    ShareLink(item: shot.url) {
                                        Label("Share", systemImage: "square.and.arrow.up")
                                    }
                                    Button(role: .destructive) {
                                        model.delete(shot)
                                    } label: {
                                        Label("Delete", systemImage: "trash")
                                    }
                                }
                        }
                    }
                    .padding(4)
                }
            }
        }
        .refreshable { model.refreshShots() }
        .sheet(item: $selected) { shot in
            ShotDetailView(shot: shot)
        }
        .onAppear { model.refreshShots() }
    }
}

private struct ShotThumbnail: View {
    let shot: Shot

    var body: some View {
        GeometryReader { proxy in
            if let image = UIImage(contentsOfFile: shot.url.path) {
                Image(uiImage: image)
                    .resizable()
                    .scaledToFill()
                    .frame(width: proxy.size.width, height: proxy.size.width)
                    .clipped()
            } else {
                Color.secondary.opacity(0.15)
            }
        }
        .aspectRatio(1, contentMode: .fit)
        .clipShape(RoundedRectangle(cornerRadius: 6))
    }
}

private struct ShotDetailView: View {
    let shot: Shot
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Group {
                if let image = UIImage(contentsOfFile: shot.url.path) {
                    ScrollView([.horizontal, .vertical]) {
                        Image(uiImage: image)
                            .resizable()
                            .scaledToFit()
                            .containerRelativeFrame([.horizontal, .vertical])
                    }
                } else {
                    ContentUnavailableView("Could not load image", systemImage: "exclamationmark.triangle")
                }
            }
            .navigationTitle(shot.name)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button("Done") { dismiss() }
                }
                ToolbarItem(placement: .topBarTrailing) {
                    ShareLink(item: shot.url)
                }
                ToolbarItem(placement: .bottomBar) {
                    Button(role: .destructive) {
                        model.delete(shot)
                        dismiss()
                    } label: {
                        Label("Delete", systemImage: "trash")
                    }
                }
            }
        }
    }
}
