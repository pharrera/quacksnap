import QuickLook
import SwiftUI

struct GalleryView: View {
    @Environment(AppModel.self) private var model
    @State private var selected: Shot?
    @State private var selecting = false
    @State private var selection: Set<URL> = []
    @State private var confirmDelete = false

    private let columns = [GridItem(.adaptive(minimum: 108), spacing: 12)]

    var body: some View {
        Group {
            if model.shots.isEmpty {
                emptyState
            } else {
                grid
            }
        }
        .refreshable { model.refreshShots() }
        .sheet(item: $selected) { shot in
            ShotDetailView(shot: shot)
        }
        .onAppear { model.refreshShots() }
        .toolbar { galleryToolbar }
        .animation(.snappy, value: selecting)
        .confirmationDialog(
            "Delete \(selection.count) \(selection.count == 1 ? "item" : "items")?",
            isPresented: $confirmDelete, titleVisibility: .visible
        ) {
            Button("Delete", role: .destructive) { deleteSelection() }
        }
    }

    // MARK: - grid with date sections

    private var grid: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 18, pinnedViews: .sectionHeaders) {
                ForEach(sections) { section in
                    Section {
                        LazyVGrid(columns: columns, spacing: 12) {
                            ForEach(section.shots) { shot in
                                tile(for: shot)
                            }
                        }
                        .padding(.horizontal, 16)
                    } header: {
                        Text(section.title)
                            .font(.headline)
                            .padding(.horizontal, 16)
                            .padding(.vertical, 8)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .background(.bar)
                    }
                }
            }
            .padding(.vertical, 8)
        }
    }

    private func tile(for shot: Shot) -> some View {
        ShotThumbnail(shot: shot, selecting: selecting, selected: selection.contains(shot.url))
            .onTapGesture {
                if selecting {
                    toggle(shot)
                } else {
                    selected = shot
                }
            }
            .contextMenu {
                if !selecting {
                    ShareLink(item: shot.url) { Label("Share", systemImage: "square.and.arrow.up") }
                    Button(role: .destructive) { model.delete(shot) } label: {
                        Label("Delete", systemImage: "trash")
                    }
                    Button { enterSelection(with: shot) } label: {
                        Label("Select", systemImage: "checkmark.circle")
                    }
                }
            }
    }

    private var sections: [ShotSection] {
        let calendar = Calendar.current
        let groups = Dictionary(grouping: model.shots) { calendar.startOfDay(for: $0.date) }
        return groups.keys.sorted(by: >).map { day in
            ShotSection(id: day, title: Self.title(for: day, calendar: calendar), shots: groups[day]!)
        }
    }

    private static func title(for day: Date, calendar: Calendar) -> String {
        if calendar.isDateInToday(day) { return "Today" }
        if calendar.isDateInYesterday(day) { return "Yesterday" }
        let formatter = DateFormatter()
        formatter.dateFormat = calendar.isDate(day, equalTo: .now, toGranularity: .year) ? "EEEE, MMM d" : "MMM d, yyyy"
        return formatter.string(from: day)
    }

    // MARK: - selection

    @ToolbarContentBuilder private var galleryToolbar: some ToolbarContent {
        if !model.shots.isEmpty {
            ToolbarItem(placement: .topBarTrailing) {
                Button(selecting ? "Done" : "Select") {
                    selecting ? exitSelection() : (selecting = true)
                }
                .fontWeight(.medium)
            }
        }
        if selecting {
            ToolbarItemGroup(placement: .bottomBar) {
                if let urls = selectedURLs {
                    ShareLink(items: urls) { Image(systemName: "square.and.arrow.up") }
                        .disabled(selection.isEmpty)
                } else {
                    Image(systemName: "square.and.arrow.up").foregroundStyle(.tertiary)
                }
                Spacer()
                Text(selection.isEmpty ? "Select items" : "\(selection.count) selected")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .fixedSize()
                Spacer()
                Button {
                    confirmDelete = true
                } label: {
                    Image(systemName: "trash")
                }
                .tint(Theme.coral)
                .disabled(selection.isEmpty)
            }
        }
    }

    private var selectedURLs: [URL]? {
        selection.isEmpty ? nil : model.shots.filter { selection.contains($0.url) }.map(\.url)
    }

    private func toggle(_ shot: Shot) {
        if selection.contains(shot.url) { selection.remove(shot.url) } else { selection.insert(shot.url) }
    }

    private func enterSelection(with shot: Shot) {
        selecting = true
        selection = [shot.url]
    }

    private func exitSelection() {
        selecting = false
        selection = []
    }

    private func deleteSelection() {
        let toDelete = model.shots.filter { selection.contains($0.url) }
        model.delete(toDelete)
        exitSelection()
    }

    // MARK: - empty state

    private var emptyState: some View {
        VStack(spacing: 18) {
            ZStack {
                Circle()
                    .fill(Theme.softGradient)
                    .frame(width: 128, height: 128)
                Image(systemName: "tray.and.arrow.down.fill")
                    .font(.system(size: 46))
                    .foregroundStyle(Theme.brandGradient)
            }
            VStack(spacing: 6) {
                Text("Ready for your files")
                    .font(.title3.bold())
                Text("Take a screenshot on your computer, or drop any file on the QuackSnap window — it lands right here.")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }
            .padding(.horizontal, 40)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

private struct ShotThumbnail: View {
    let shot: Shot
    var selecting = false
    var selected = false

    var body: some View {
        content
            .aspectRatio(1, contentMode: .fit)
            .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .strokeBorder(selected ? AnyShapeStyle(Theme.brand) : AnyShapeStyle(Color(.separator).opacity(0.3)),
                                  lineWidth: selected ? 3 : 1))
            .overlay(alignment: .bottomTrailing) { checkmark }
            .shadow(color: .black.opacity(0.08), radius: 6, y: 3)
            .scaleEffect(selected ? 0.94 : 1)
            .animation(.snappy(duration: 0.18), value: selected)
    }

    @ViewBuilder private var content: some View {
        if shot.isImage, let image = UIImage(contentsOfFile: shot.url.path) {
            Color.clear.overlay(
                Image(uiImage: image).resizable().scaledToFill())
                .clipped()
        } else {
            fileTile
        }
    }

    @ViewBuilder private var checkmark: some View {
        if selecting {
            Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                .font(.title3)
                .symbolRenderingMode(.palette)
                .foregroundStyle(.white, selected ? Theme.brand : Color.black.opacity(0.25))
                .background(Circle().fill(selected ? Theme.brand.opacity(0.001) : .clear))
                .padding(7)
                .shadow(color: .black.opacity(0.25), radius: 2)
        }
    }

    private var fileTile: some View {
        VStack(spacing: 10) {
            Image(systemName: fileKind.icon)
                .font(.system(size: 34))
                .foregroundStyle(fileKind.color)
            Text(shot.name)
                .font(.caption2.weight(.medium))
                .lineLimit(2)
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
                .padding(.horizontal, 8)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(fileKind.color.opacity(0.10))
    }

    private var fileKind: FileKind { FileKind(ext: shot.url.pathExtension.lowercased()) }
}

private struct ShotSection: Identifiable {
    let id: Date
    let title: String
    let shots: [Shot]
}

private struct FileKind {
    let icon: String
    let color: Color

    init(ext: String) {
        switch ext {
        case "pdf": self = FileKind(icon: "doc.richtext.fill", color: Color(hex: 0xE24B4A))
        case "txt", "md", "csv", "json", "xml": self = FileKind(icon: "doc.text.fill", color: Color(hex: 0x378ADD))
        case "zip": self = FileKind(icon: "doc.zipper", color: Color(hex: 0xBA7517))
        case "mp4", "mov": self = FileKind(icon: "film.fill", color: Color(hex: 0x7F77DD))
        case "mp3", "m4a", "wav": self = FileKind(icon: "waveform", color: Color(hex: 0x1D9E75))
        case "doc", "docx": self = FileKind(icon: "doc.fill", color: Color(hex: 0x185FA5))
        case "xls", "xlsx": self = FileKind(icon: "tablecells.fill", color: Color(hex: 0x1D9E75))
        case "ppt", "pptx": self = FileKind(icon: "rectangle.on.rectangle.fill", color: Color(hex: 0xD85A30))
        default: self = FileKind(icon: "doc.fill", color: Color(hex: 0x888780))
        }
    }

    private init(icon: String, color: Color) {
        self.icon = icon
        self.color = color
    }
}

/// QuickLook previews everything iOS knows how to render — images, PDFs, video,
/// text, Office documents — so one detail view covers every file type.
private struct ShotDetailView: View {
    let shot: Shot
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            QuickLookPreview(url: shot.url)
                .ignoresSafeArea(edges: .bottom)
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

private struct QuickLookPreview: UIViewControllerRepresentable {
    let url: URL

    func makeCoordinator() -> Coordinator { Coordinator(url: url) }

    func makeUIViewController(context: Context) -> QLPreviewController {
        let controller = QLPreviewController()
        controller.dataSource = context.coordinator
        return controller
    }

    func updateUIViewController(_ controller: QLPreviewController, context: Context) {}

    final class Coordinator: NSObject, QLPreviewControllerDataSource {
        let url: URL

        init(url: URL) { self.url = url }

        func numberOfPreviewItems(in controller: QLPreviewController) -> Int { 1 }

        func previewController(_ controller: QLPreviewController, previewItemAt index: Int) -> QLPreviewItem {
            url as NSURL
        }
    }
}
