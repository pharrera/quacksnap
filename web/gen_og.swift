import AppKit

// 1200×630 Open Graph share card: brand gradient, duck mark, wordmark, tagline.
let w = 1200.0, h = 630.0
let image = NSImage(size: NSSize(width: w, height: h))
image.lockFocus()
let ctx = NSGraphicsContext.current!.cgContext

func col(_ hex: UInt) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 0xff) / 255, green: CGFloat((hex >> 8) & 0xff) / 255,
            blue: CGFloat(hex & 0xff) / 255, alpha: 1)
}

// Warm off-white background.
col(0xFDF8F3).setFill()
ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))

// Duck mark tile at left.
let tile = CGRect(x: 96, y: h / 2 - 132, width: 264, height: 264)
let tilePath = CGPath(roundedRect: tile, cornerWidth: 74, cornerHeight: 74, transform: nil)
ctx.saveGState()
ctx.addPath(tilePath); ctx.clip()
let grad = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                      colors: [col(0xFFC53D).cgColor, col(0xFF6B3D).cgColor] as CFArray, locations: [0, 1])!
ctx.drawLinearGradient(grad, start: CGPoint(x: tile.minX, y: tile.maxY), end: CGPoint(x: tile.maxX, y: tile.minY), options: [])
ctx.restoreGState()

// Duck, mapped into the tile (100-box → tile). Flip y within the tile.
let s = tile.width / 100.0
func p(_ x: Double, _ y: Double) -> CGPoint { CGPoint(x: tile.minX + x * s, y: tile.maxY - y * s) }
// Corner brackets.
col(0xFFFFFF).setStroke()
ctx.setLineWidth(6.5 * s); ctx.setLineCap(.round); ctx.setLineJoin(.round)
let br = CGMutablePath()
br.move(to: p(36, 24)); br.addLine(to: p(24, 24)); br.addLine(to: p(24, 36))
br.move(to: p(64, 24)); br.addLine(to: p(76, 24)); br.addLine(to: p(76, 36))
br.move(to: p(36, 76)); br.addLine(to: p(24, 76)); br.addLine(to: p(24, 64))
br.move(to: p(64, 76)); br.addLine(to: p(76, 76)); br.addLine(to: p(76, 64))
ctx.addPath(br); ctx.strokePath()
// Spark.
col(0xFFFFFF).setFill()
let sp = CGMutablePath()
sp.move(to: p(50, 32)); sp.addLine(to: p(55, 45)); sp.addLine(to: p(68, 50)); sp.addLine(to: p(55, 55))
sp.addLine(to: p(50, 68)); sp.addLine(to: p(45, 55)); sp.addLine(to: p(32, 50)); sp.addLine(to: p(45, 45)); sp.closeSubpath()
ctx.addPath(sp); ctx.fillPath()

// Text block at right.
let textX = 420.0
func draw(_ str: String, font: NSFont, color: NSColor, x: Double, baseline: Double) {
    let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color]
    NSAttributedString(string: str, attributes: attrs).draw(at: NSPoint(x: x, y: h - baseline))
}
let bold = NSFont.systemFont(ofSize: 84, weight: .heavy)
draw("QuackSnap", font: bold, color: col(0x1C1712), x: textX, baseline: 250)
let sub = NSFont.systemFont(ofSize: 40, weight: .medium)
draw("Screenshots from Windows to iPhone,", font: sub, color: col(0x4A423B), x: textX, baseline: 322)
draw("the instant you snip.", font: NSFont.systemFont(ofSize: 40, weight: .bold), color: col(0xF5762A), x: textX, baseline: 378)
let tag = NSFont.systemFont(ofSize: 28, weight: .semibold)
draw("End-to-end encrypted  ·  No account  ·  Free", font: tag, color: col(0x8A8078), x: textX, baseline: 448)

image.unlockFocus()
let rep = NSBitmapImageRep(data: image.tiffRepresentation!)!
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
print("wrote", CommandLine.arguments[1])
