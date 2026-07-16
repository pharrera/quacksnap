import AppKit

// QuackSnap mark — screenshot corner-brackets around a spark ("snip + spark").
// Authored in a top-left 100×100 box, mapped to the canvas.
let px = 1024.0
let image = NSImage(size: NSSize(width: px, height: px))
image.lockFocus()
let ctx = NSGraphicsContext.current!.cgContext

func col(_ hex: UInt) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 0xff) / 255, green: CGFloat((hex >> 8) & 0xff) / 255,
            blue: CGFloat(hex & 0xff) / 255, alpha: 1)
}
let s = px / 100.0
func p(_ x: Double, _ y: Double) -> CGPoint { CGPoint(x: x * s, y: (100 - y) * s) } // flip to bottom-left

// Background gradient (full bleed; iOS masks corners).
let grad = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                      colors: [col(0xFFC53D).cgColor, col(0xFF6B3D).cgColor] as CFArray, locations: [0, 1])!
ctx.drawLinearGradient(grad, start: CGPoint(x: 0, y: px), end: CGPoint(x: px, y: 0), options: [])

// Soft shadow for depth on the white glyph.
ctx.setShadow(offset: CGSize(width: 0, height: -14), blur: 34, color: col(0x7A3A12).withAlphaComponent(0.28).cgColor)

// Corner brackets (round caps + joins give rounded corners).
col(0xFFFFFF).setStroke()
ctx.setLineWidth(6.5 * s)
ctx.setLineCap(.round)
ctx.setLineJoin(.round)
let br = CGMutablePath()
br.move(to: p(36, 24)); br.addLine(to: p(24, 24)); br.addLine(to: p(24, 36))
br.move(to: p(64, 24)); br.addLine(to: p(76, 24)); br.addLine(to: p(76, 36))
br.move(to: p(36, 76)); br.addLine(to: p(24, 76)); br.addLine(to: p(24, 64))
br.move(to: p(64, 76)); br.addLine(to: p(76, 76)); br.addLine(to: p(76, 64))
ctx.addPath(br); ctx.strokePath()

// Spark (4-point sparkle).
col(0xFFFFFF).setFill()
let sp = CGMutablePath()
sp.move(to: p(50, 32))
sp.addLine(to: p(55, 45)); sp.addLine(to: p(68, 50)); sp.addLine(to: p(55, 55))
sp.addLine(to: p(50, 68)); sp.addLine(to: p(45, 55)); sp.addLine(to: p(32, 50)); sp.addLine(to: p(45, 45))
sp.closeSubpath()
ctx.addPath(sp); ctx.fillPath()

image.unlockFocus()
let rep = NSBitmapImageRep(data: image.tiffRepresentation!)!
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
print("wrote", CommandLine.arguments[1])
