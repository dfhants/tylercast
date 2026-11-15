import Foundation
import Testing
@testable import MirrorCore

@Suite struct AudioTests {
    /// Interleaved stereo sine, `seconds` long.
    static func sine(_ hz: Double, seconds: Double, amplitude: Float = 0.5) -> [Float] {
        let n = Int(seconds * Double(PCM.sampleRate))
        var out = [Float](repeating: 0, count: n * 2)
        for i in 0..<n {
            let v = amplitude * Float(sin(2 * Double.pi * hz * Double(i) / Double(PCM.sampleRate)))
            out[i * 2] = v
            out[i * 2 + 1] = v
        }
        return out
    }

    @Test func adtsHeader() {
        let h = AACEncoder.adtsHeader(payloadBytes: 300)
        #expect(h.count == 7)
        #expect(h[0] == 0xFF && h[1] & 0xF0 == 0xF0)  // sync
        #expect((h[2] >> 6) == 1)  // AAC-LC
        #expect((h[2] >> 2) & 0x0F == 3)  // 48 kHz
        let channels = ((h[2] & 1) << 2) | (h[3] >> 6)
        #expect(channels == 2)
        let len = (Int(h[3] & 3) << 11) | (Int(h[4]) << 3) | Int(h[5] >> 5)
        #expect(len == 307)
    }

    @Test func pmtAnnouncesAACAsADTS() {
        var cfg = TSMuxer.Config()
        cfg.audioCodec = .aac
        let pmt = TSMuxer(config: cfg).pmt()
        // ... 0x0F, PID 0x101, no descriptors (as ffmpeg's mpegts muxer).
        #expect(pmt.windowsContain([0x0F, 0xE1, 0x01, 0xF0, 0x00]))
        #expect(!TSMuxer(config: TSMuxer.Config()).pmt().windowsContain([0x0F, 0xE1, 0x01]))
        #expect(DLNA.profile(for: .aac) == "AVC_TS_MP_HD_AAC_MULT5_ISO")
    }

    @Test func aacFramesAreTimedFromTheirFirstSample() throws {
        let enc = try AACEncoder()
        var frames: [([UInt8], Double)] = []
        enc.onFrame = { frames.append(($0, $1)) }
        let pcm = Self.sine(1000, seconds: 1)
        // In 10ms buffers, as capture delivers them.
        let step = 480 * 2
        for (i, off) in stride(from: 0, to: pcm.count, by: step).enumerated() {
            enc.push(Array(pcm[off..<min(off + step, pcm.count)]), host: 100 + Double(i) * 0.01)
        }
        #expect(frames.count >= 40)  // 46 frames of input, less encoder priming
        for f in frames { #expect(f.0[0] == 0xFF && f.0[1] == 0xF1) }
        let gaps = zip(frames, frames.dropFirst()).map { $1.1 - $0.1 }
        for g in gaps { #expect(abs(g - 1024.0 / 48000) < 1e-9) }
        #expect(frames.first.map { $0.1 } ?? 0 >= 100)
    }

    /// The tap sends nothing while the Mac is quiet: padded silence keeps the
    /// track continuous, and real sound joins it without a gap or overlap.
    @Test func silencePaddingKeepsTheTrackContinuous() throws {
        let enc = try AACEncoder()
        var times: [Double] = []
        enc.onFrame = { times.append($1) }
        enc.padSilence(upTo: 10.0)  // anchors at 9.9
        for i in 1...50 { enc.padSilence(upTo: 10.0 + Double(i) * 0.02) }  // 1s of quiet
        // Sound arrives 30ms off where the padding got to, then keeps coming.
        var host = 10.9 + 0.03
        for _ in 0..<100 {
            enc.push(Self.sine(440, seconds: 0.01), host: host)
            host += 0.01
        }
        #expect(times.count > 80)
        #expect(abs((times.first ?? 0) - 9.9) < 1e-9)
        for (a, b) in zip(times, times.dropFirst()) { #expect(abs(b - a - 1024.0 / 48000) < 1e-9) }
    }

    @Test func aacMuxedStreamDecodesToTheTone() throws {
        guard let ffmpeg = ["/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg"].first(where: FileManager.default.fileExists)
        else { return }  // ffmpeg is the reference decoder; skip without it
        var cfg = TSMuxer.Config()
        cfg.audioCodec = .aac
        cfg.muxRate = 2_000_000
        let mux = TSMuxer(config: cfg)
        let enc = try AACEncoder()
        enc.onFrame = { frame, host in mux.enqueueAudio(frame: frame, pts90: TSMuxer.base90 + UInt64(host * 90_000)) }
        enc.push(Self.sine(1000, seconds: 2), host: 0)
        var ts = Data()
        while mux.queuedPackets > 0 { ts.append(mux.produce(count: 1000)) }

        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("tylercast-aac-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let tsURL = dir.appendingPathComponent("a.ts"), raw = dir.appendingPathComponent("a.f32")
        try ts.write(to: tsURL)
        let p = Process()
        p.executableURL = URL(fileURLWithPath: ffmpeg)
        p.arguments = ["-v", "error", "-i", tsURL.path, "-map", "0:a", "-af", "pan=mono|c0=c0", "-f", "f32le", "-ar", "48000", raw.path]
        try p.run()
        p.waitUntilExit()
        #expect(p.terminationStatus == 0)
        let decoded = try Data(contentsOf: raw).withUnsafeBytes { Array($0.bindMemory(to: Float.self)) }
        #expect(decoded.count > 48_000)

        // Middle second of the left channel: level about the input's (0.5 peak → 0.354 RMS), pitch 1 kHz.
        let mid = Array(decoded[decoded.count / 4 ..< decoded.count / 4 + 48_000])
        let rms = sqrt(mid.reduce(0) { $0 + Double($1 * $1) } / Double(mid.count))
        #expect(abs(rms - 0.354) < 0.05)
        let crossings = zip(mid, mid.dropFirst()).filter { $0 < 0 && $1 >= 0 }.count
        #expect(abs(crossings - 1000) <= 5)
    }
}

extension Array where Element == UInt8 {
    func windowsContain(_ needle: [UInt8]) -> Bool {
        guard needle.count <= count else { return false }
        return (0...(count - needle.count)).contains { Array(self[$0..<$0 + needle.count]) == needle }
    }
}
