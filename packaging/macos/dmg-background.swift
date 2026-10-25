import AppKit

// Render an original, text-based Finder background. No app screenshots or user data.
let output = CommandLine.arguments[1]
let width: CGFloat = 900
let height: CGFloat = 580
let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1800, pixelsHigh: 1160,
    bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
    colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
bitmap.size = NSSize(width: width, height: height)
let context = NSGraphicsContext(bitmapImageRep: bitmap)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = context
NSColor.white.setFill()
NSRect(x: 0, y: 0, width: width, height: height).fill()

func rect(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat) -> NSRect {
    NSRect(x: x, y: height - y - h, width: w, height: h)
}
func text(_ value: String, _ x: CGFloat, _ y: CGFloat, _ size: CGFloat,
          bold: Bool = false, mono: Bool = false, color: NSColor = .labelColor) {
    let font = mono ? NSFont.monospacedSystemFont(ofSize: size, weight: .regular)
                    : NSFont.systemFont(ofSize: size, weight: bold ? .semibold : .regular)
    let style = NSMutableParagraphStyle()
    style.lineBreakMode = .byWordWrapping
    (value as NSString).draw(in: rect(x, y, width - x - 65, size * 2.8),
        withAttributes: [.font: font, .foregroundColor: color, .paragraphStyle: style])
}
let muted = NSColor(calibratedRed: 0.30, green: 0.35, blue: 0.42, alpha: 1)
let dark = NSColor(calibratedRed: 0.10, green: 0.15, blue: 0.22, alpha: 1)
let blue = NSColor(calibratedRed: 0.14, green: 0.40, blue: 0.76, alpha: 1)
text("拖入 Applications 安装", 323, 59, 16, bold: true, color: muted)
let arrow = NSBezierPath()
arrow.move(to: NSPoint(x: 330, y: height - 105))
arrow.line(to: NSPoint(x: 540, y: height - 105))
arrow.move(to: NSPoint(x: 526, y: height - 94))
arrow.line(to: NSPoint(x: 540, y: height - 105))
arrow.line(to: NSPoint(x: 526, y: height - 116))
arrow.lineWidth = 2
blue.setStroke()
arrow.stroke()
NSColor(calibratedRed: 0.95, green: 0.97, blue: 1, alpha: 1).setFill()
NSBezierPath(roundedRect: rect(60, 205, 780, 335), xRadius: 16, yRadius: 16).fill()
text("macOS 拦截时的处理方法", 85, 225, 22, bold: true, color: dark)
text("本地测试包未公证；请先确认来源并校验 SHA-256。", 85, 264, 14, color: muted)
text("1. 先退出旧版，将 v2rayN 拖入 Applications，再尝试打开。", 85, 301, 15, color: dark)
text("2. 如提示“无法验证开发者”或“应用已损坏”，在终端执行：", 85, 334, 15, color: dark)
NSColor.white.setFill()
NSBezierPath(roundedRect: rect(85, 370, 730, 44), xRadius: 8, yRadius: 8).fill()
text("xattr -dr com.apple.quarantine \"/Applications/v2rayN.app\"", 100, 383, 15.5, mono: true, color: dark)
text("命令可从右上角的“安装说明.txt”复制；执行后重新打开应用。", 85, 432, 14, color: muted)
text("也可前往“系统设置 → 隐私与安全性 → 仍要打开”。", 85, 462, 14, color: muted)
text("若提示恶意软件或“会损坏电脑”，请停止并重新获取安装包。", 85, 502, 13, color: muted)
NSGraphicsContext.restoreGraphicsState()
try bitmap.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: output))
