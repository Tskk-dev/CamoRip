import AVFoundation

struct AirConfiguration: Codable {
    var enabled = false
    var preview = false
    var recalibrate = false
    var threshold = 12.0
    var x = 0.5
    var y = 0.5
    var span = 0.65

    init() {}
    private enum CodingKeys: String, CodingKey { case enabled, preview, recalibrate, threshold, x, y, span }
    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        enabled = try values.decodeIfPresent(Bool.self, forKey: .enabled) ?? false
        preview = try values.decodeIfPresent(Bool.self, forKey: .preview) ?? false
        recalibrate = try values.decodeIfPresent(Bool.self, forKey: .recalibrate) ?? false
        threshold = try values.decodeIfPresent(Double.self, forKey: .threshold) ?? 12
        x = try values.decodeIfPresent(Double.self, forKey: .x) ?? 0.5
        y = try values.decodeIfPresent(Double.self, forKey: .y) ?? 0.5
        span = try values.decodeIfPresent(Double.self, forKey: .span) ?? 0.65
    }

    var isValid: Bool {
        threshold.isFinite && (1...100).contains(threshold) &&
        x.isFinite && (0...1).contains(x) && y.isFinite && (0...1).contains(y) &&
        span.isFinite && (0.1...0.9).contains(span)
    }

    func rectangles(width: Int, height: Int) -> [CGRect] {
        let side = max(2, Double(min(width, height)) * 0.04)
        let margin = side / (2 * Double(height))
        let center = min(max(y, span / 2 + margin), 1 - span / 2 - margin)
        return (0..<6).map { zone in
            let centerY = Double(height) * (center + (Double(zone) / 5 - 0.5) * span)
            return CGRect(x: CGFloat(min(max(0, Double(width) * x - side / 2), Double(width) - side)),
                          y: CGFloat(min(max(0, centerY - side / 2), Double(height) - side)),
                          width: CGFloat(side), height: CGFloat(side))
        }
    }
}

final class AirZoneDetector {
    struct Update { let mask: Int; let generation: Int }
    private let lock = NSLock()
    private var configuration = AirConfiguration()
    private var baseline: [Double]?
    private var dimensions = CGSize.zero
    private var mask = 0
    private var lastSent = -Double.infinity
    private var revision = 0
    var generation: Int { lock.lock(); defer { lock.unlock() }; return revision }
    var enabled: Bool { lock.lock(); defer { lock.unlock() }; return configuration.enabled }

    @discardableResult func configure(_ configuration: AirConfiguration) -> Int {
        lock.lock(); defer { lock.unlock() }
        let previous = self.configuration
        let reset = configuration.recalibrate || previous.enabled != configuration.enabled ||
            previous.threshold != configuration.threshold || previous.x != configuration.x ||
            previous.y != configuration.y || previous.span != configuration.span
        self.configuration = configuration
        if reset { baseline = nil; mask = 0 }
        lastSent = -.infinity
        revision += 1
        return mask
    }

    func process(_ sample: CMSampleBuffer) -> Update? {
        guard let pixels = CMSampleBufferGetImageBuffer(sample),
              CVPixelBufferGetPlaneCount(pixels) >= 2 else { return nil }
        CVPixelBufferLockBaseAddress(pixels, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(pixels, .readOnly) }
        guard let address = CVPixelBufferGetBaseAddressOfPlane(pixels, 0) else { return nil }
        return processLuma(address.assumingMemoryBound(to: UInt8.self),
            width: CVPixelBufferGetWidthOfPlane(pixels, 0), height: CVPixelBufferGetHeightOfPlane(pixels, 0),
            stride: CVPixelBufferGetBytesPerRowOfPlane(pixels, 0), now: ProcessInfo.processInfo.systemUptime)
    }

    func processLuma(_ bytes: UnsafePointer<UInt8>, width: Int, height: Int, stride: Int, now: Double) -> Update? {
        lock.lock(); defer { lock.unlock() }
        guard configuration.enabled, width >= 2, height >= 2, stride >= width else { return nil }
        var means: [Double] = []
        for rectangle in configuration.rectangles(width: width, height: height) {
            var total = 0
            let top = Double(rectangle.minY)
            let left = Double(rectangle.minX)
            let stepY = Double(rectangle.height) / 8.0
            let stepX = Double(rectangle.width) / 8.0
            for row in 0..<8 {
                let y = min(height - 1, Int(top + (Double(row) + 0.5) * stepY))
                for column in 0..<8 {
                    let x = min(width - 1, Int(left + (Double(column) + 0.5) * stepX))
                    total += Int(bytes[y * stride + x])
                }
            }
            means.append(Double(total) / 64.0)
        }
        let size = CGSize(width: CGFloat(width), height: CGFloat(height))
        let previous = mask
        if baseline == nil || dimensions != size {
            baseline = means
            dimensions = size
            mask = 0
            lastSent = -.infinity
        } else if let baseline {
            for zone in 0..<6 {
                let bit = 1 << zone
                let threshold = configuration.threshold * (mask & bit == 0 ? 1 : 0.65)
                if abs(means[zone] - baseline[zone]) >= threshold { mask |= bit }
                else { mask &= ~bit }
            }
        }
        guard mask != previous || now - lastSent >= 0.1 else { return nil }
        lastSent = now
        return Update(mask: mask, generation: revision)
    }
}
