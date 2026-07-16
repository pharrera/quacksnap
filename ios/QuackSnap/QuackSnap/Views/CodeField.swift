import SwiftUI

/// Six-box pairing-code entry. A hidden text field captures keystrokes; the boxes
/// mirror the digits and highlight the active slot in brand color.
struct CodeField: View {
    @Binding var code: String
    var onComplete: () -> Void

    @FocusState private var focused: Bool

    private let count = 6

    var body: some View {
        ZStack {
            TextField("", text: $code)
                .keyboardType(.numberPad)
                .textContentType(.oneTimeCode)
                .focused($focused)
                .foregroundStyle(.clear)
                .tint(.clear)
                .onChange(of: code) { _, newValue in
                    let digits = String(newValue.filter(\.isNumber).prefix(count))
                    if digits != newValue { code = digits }
                    if digits.count == count { onComplete() }
                }

            HStack(spacing: 10) {
                ForEach(0..<count, id: \.self) { index in
                    box(at: index)
                }
            }
            .allowsHitTesting(false)
        }
        .contentShape(Rectangle())
        .onTapGesture { focused = true }
        .onAppear { focused = true }
    }

    private func box(at index: Int) -> some View {
        let characters = Array(code)
        let isFilled = index < characters.count
        let isActive = focused && index == characters.count

        return Text(isFilled ? String(characters[index]) : "")
            .font(.system(size: 30, weight: .bold, design: .rounded))
            .foregroundStyle(.primary)
            .frame(width: 46, height: 58)
            .background(
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .fill(Color(.secondarySystemBackground)))
            .overlay(
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .strokeBorder(
                        isActive ? AnyShapeStyle(Theme.brandGradient)
                                 : AnyShapeStyle(Color(.separator).opacity(isFilled ? 0.4 : 0.7)),
                        lineWidth: isActive ? 2 : 1))
            .animation(.easeOut(duration: 0.15), value: isActive)
            .animation(.easeOut(duration: 0.15), value: isFilled)
    }
}
