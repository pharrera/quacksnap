import SwiftUI

/// QuackSnap brand system. Accent colors are constant across light/dark so the
/// brand reads the same; surfaces use system materials so dark mode just works.
enum Theme {
    static let amber = Color(hex: 0xFFC53D)
    static let coral = Color(hex: 0xFF6B3D)
    static let brand = Color(hex: 0xFF8A3D)
    static let brandInk = Color(hex: 0x7A3A12)

    static let brandGradient = LinearGradient(
        colors: [amber, coral], startPoint: .topLeading, endPoint: .bottomTrailing)

    static let softGradient = LinearGradient(
        colors: [amber.opacity(0.16), coral.opacity(0.10)],
        startPoint: .topLeading, endPoint: .bottomTrailing)
}

extension Color {
    init(hex: UInt, alpha: Double = 1) {
        self.init(
            .sRGB,
            red: Double((hex >> 16) & 0xff) / 255,
            green: Double((hex >> 8) & 0xff) / 255,
            blue: Double(hex & 0xff) / 255,
            opacity: alpha)
    }
}

/// The app mark: screenshot corner-brackets around a spark, on the brand gradient
/// tile. Geometry is authored in a 100×100 space and shared with the app icon and
/// the website.
struct BrandMark: View {
    var size: CGFloat = 96

    var body: some View {
        RoundedRectangle(cornerRadius: size * 0.28, style: .continuous)
            .fill(Theme.brandGradient)
            .frame(width: size, height: size)
            .overlay { BrandGlyph(size: size) }
            .shadow(color: Theme.coral.opacity(0.35), radius: size * 0.16, y: size * 0.08)
    }
}

/// The snip + spark glyph, drawn to fill its bounds (no tile).
struct BrandGlyph: View {
    var size: CGFloat = 96

    var body: some View {
        ZStack {
            BracketsShape()
                .stroke(.white, style: StrokeStyle(lineWidth: size * 0.065, lineCap: .round, lineJoin: .round))
            SparkShape().fill(.white)
        }
        .frame(width: size, height: size)
    }
}

private struct BracketsShape: Shape {
    func path(in rect: CGRect) -> Path {
        let s = rect.width / 100
        func pt(_ x: Double, _ y: Double) -> CGPoint { CGPoint(x: x * s, y: y * s) }
        var p = Path()
        p.move(to: pt(36, 24)); p.addLine(to: pt(24, 24)); p.addLine(to: pt(24, 36))
        p.move(to: pt(64, 24)); p.addLine(to: pt(76, 24)); p.addLine(to: pt(76, 36))
        p.move(to: pt(36, 76)); p.addLine(to: pt(24, 76)); p.addLine(to: pt(24, 64))
        p.move(to: pt(64, 76)); p.addLine(to: pt(76, 76)); p.addLine(to: pt(76, 64))
        return p
    }
}

private struct SparkShape: Shape {
    func path(in rect: CGRect) -> Path {
        let s = rect.width / 100
        func pt(_ x: Double, _ y: Double) -> CGPoint { CGPoint(x: x * s, y: y * s) }
        var p = Path()
        p.move(to: pt(50, 32))
        p.addLine(to: pt(55, 45)); p.addLine(to: pt(68, 50)); p.addLine(to: pt(55, 55))
        p.addLine(to: pt(50, 68)); p.addLine(to: pt(45, 55)); p.addLine(to: pt(32, 50)); p.addLine(to: pt(45, 45))
        p.closeSubpath()
        return p
    }
}

/// Filled gradient CTA. Dims when disabled, springs on press.
struct PrimaryButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        Content(configuration: configuration)
    }

    private struct Content: View {
        let configuration: Configuration
        @Environment(\.isEnabled) private var isEnabled

        var body: some View {
            configuration.label
                .font(.headline)
                .foregroundStyle(.white)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 15)
                .background(Theme.brandGradient, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
                .opacity(isEnabled ? (configuration.isPressed ? 0.85 : 1) : 0.4)
                .scaleEffect(configuration.isPressed ? 0.98 : 1)
                .animation(.easeOut(duration: 0.15), value: configuration.isPressed)
        }
    }
}

/// Soft tinted secondary button.
struct SoftButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(Theme.brand)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 13)
            .background(Theme.brand.opacity(0.12), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
            .opacity(configuration.isPressed ? 0.7 : 1)
    }
}
