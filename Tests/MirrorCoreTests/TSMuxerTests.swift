import Foundation
import Testing
@testable import MirrorCore

private func hex(_ s: String) -> [UInt8] {
    var out = [UInt8]()
    var i = s.startIndex
    while i < s.endIndex {
        let j = s.index(i, offsetBy: 2)
        out.append(UInt8(s[i..<j], radix: 16)!)
        i = j
    }
    return out
}

private func pid(_ p: [UInt8]) -> UInt16 { (UInt16(p[1] & 0x1F) << 8) | UInt16(p[2]) }

/// PCR in 27 MHz from a packet's adaptation field, if it has one.
private func pcr(_ p: [UInt8]) -> UInt64? {
    guard p[3] & 0x20 != 0, p[4] >= 7, p[5] & 0x10 != 0 else { return nil }
    let b = p[6...11].map(UInt64.init)
    let base = (b[0] << 25) | (b[1] << 17) | (b[2] << 9) | (b[3] << 1) | (b[4] >> 7)
    return base * 300 + ((b[4] & 1) << 8 | b[5])
}

/// Payload offset (after any adaptation field).
private func payloadStart(_ p: [UInt8]) -> Int { p[3] & 0x20 != 0 ? 5 + Int(p[4]) : 4 }

private func packets(_ d: Data) -> [[UInt8]] {
    let a = [UInt8](d)
    return stride(from: 0, to: a.count, by: 188).map { Array(a[$0..<($0 + 188)]) }
}

@Suite struct TSMuxerTests {
    @Test func crcKnownValue() {
        #expect(crc32mpeg(Array("123456789".utf8)) == 0x0376_E6E7)
    }

    /// Byte-for-byte what ffmpeg's mpegts muxer wrote in Phase 1 (which the TV played).
    @Test func patAndPmtMatchFfmpeg() {
        let m = TSMuxer()
        #expect(m.pat() == hex("00b00d0001c100000001f0002ab104b2"))
        #expect(m.pmt() == hex("02b01d0001c10000e100f0001be100f00081e101f006050441432d33ce50fcbc"))
    }

    @Test func pmtWithoutAudioHasOnlyVideo() {
        var c = TSMuxer.Config()
        c.includeAudio = false
        let pmt = TSMuxer(config: c).pmt()
        #expect(pmt.count == 3 + 9 + 5 + 4)  // header, PCR PID + info len + one stream, CRC
        #expect(crc32mpeg(pmt) == 0)  // a section with its CRC appended checks to 0
    }

    @Test func sectionsCheckToZero() {
        let m = TSMuxer()
        for s in [m.pat(), m.pmt(), m.sdt()] { #expect(crc32mpeg(s) == 0) }
    }

    @Test func everyPacketIsWellFormed() {
        let m = TSMuxer()
        m.enqueueVideo(annexB: [UInt8](repeating: 0xAB, count: 50_000), pts90: 200_000, keyframe: true)
        m.enqueueAudio(frame: SilentAC3.frame, pts90: 200_000)
        let ps = packets(m.produce(count: 2000))
        #expect(ps.allSatisfy { $0.count == 188 && $0[0] == 0x47 })
        // adaptation field lengths stay inside the packet
        #expect(ps.allSatisfy { $0[3] & 0x20 == 0 || Int($0[4]) <= 183 })
    }

    @Test func tablesComeFirstAndRepeat() {
        let m = TSMuxer()  // 10 Mbit/s: 100ms = 665 packets
        let ps = packets(m.produce(count: 3000))
        #expect(pid(ps[0]) == 0 && pid(ps[1]) == 0x1000 && pid(ps[2]) == 0x11)
        let patAt = ps.indices.filter { pid(ps[$0]) == 0 }
        #expect(patAt.count >= 4)
        for (a, b) in zip(patAt, patAt.dropFirst()) { #expect(b - a <= 700) }
    }

    @Test func pcrFollowsByteRateAndRepeats() {
        let m = TSMuxer()
        let ps = packets(m.produce(count: 20_000))
        let pcrs = ps.indices.compactMap { i in pcr(ps[i]).map { (i, $0) } }
        #expect(pcrs.count > 50)
        for (i, v) in pcrs {
            #expect(pid(ps[i]) == 0x100)  // on the video PID, as the PMT says
            #expect(v == m.pcr27(atPacket: UInt64(i)))
        }
        for (a, b) in zip(pcrs, pcrs.dropFirst()) {
            let gap = Double(b.1 - a.1) / 27_000_000
            #expect(gap > 0 && gap <= 0.04)  // spec max 0.1s
        }
        // exactly muxRate: 1504 bits per packet at 10 Mbit/s
        let (i0, p0) = pcrs.first!, (i1, p1) = pcrs.last!
        let rate = Double((i1 - i0) * 1504) / (Double(p1 - p0) / 27_000_000)
        #expect(abs(rate - 10_000_000) < 1000)
    }

    @Test func nullPaddingWhenIdle() {
        let m = TSMuxer()
        let ps = packets(m.produce(count: 1000))
        let nulls = ps.filter { pid($0) == 0x1FFF }
        #expect(nulls.count > 900)
        #expect(nulls.allSatisfy { $0 == TSMuxer.nullPacket })
    }

    @Test func continuityCountersIncrementPerPID() {
        let m = TSMuxer()
        for k in 0..<10 {
            m.enqueueVideo(annexB: [UInt8](repeating: UInt8(k), count: 3000), pts90: 100_000 + UInt64(k) * 3600, keyframe: k == 0)
            m.enqueueAudio(frame: SilentAC3.frame, pts90: 100_000 + UInt64(k) * 2880)
        }
        let ps = packets(m.produce(count: 5000))
        var last: [UInt16: UInt8] = [:]
        for p in ps where pid(p) != 0x1FFF {
            let c = p[3] & 0x0F, hasPayload = p[3] & 0x10 != 0
            if let prev = last[pid(p)] {
                #expect(c == (hasPayload ? (prev + 1) & 0x0F : prev))
            }
            last[pid(p)] = c
        }
    }

    @Test func videoPESCarriesPTSAndKeyframeFlags() {
        let m = TSMuxer()
        let au: [UInt8] = [0, 0, 0, 1, 0x09, 0xF0, 0, 0, 0, 1, 0x65] + [UInt8](repeating: 7, count: 1000)
        m.enqueueVideo(annexB: au, pts90: 123_456, keyframe: true)
        let ps = packets(m.produce(count: 700))
        let starts = ps.indices.filter { pid(ps[$0]) == 0x100 && ps[$0][1] & 0x40 != 0 }
        #expect(starts.count == 1)
        let i = starts[0]
        // The keyframe is preceded by PAT, PMT, SDT and a PCR, so a TV joining here can decode at once.
        #expect(pid(ps[i - 4]) == 0 && pid(ps[i - 3]) == 0x1000 && pid(ps[i - 2]) == 0x11 && pcr(ps[i - 1]) != nil)
        let p = ps[i]
        #expect(p[3] & 0x20 != 0 && p[5] & 0x40 != 0)  // random_access_indicator
        let pes = Array(p[payloadStart(p)...])
        #expect(Array(pes[0..<4]) == [0, 0, 1, 0xE0])
        #expect(pes[4] == 0 && pes[5] == 0)  // unbounded video PES, as ffmpeg
        #expect(pes[7] == 0x80 && pes[8] == 5)  // PTS only
        #expect(TSMuxer.decodeTimestamp(pes[9..<14]) == 123_456)
        #expect(Array(pes[14..<20]) == [0, 0, 0, 1, 0x09, 0xF0])
        // Reassembled payload is the access unit.
        var payload = Array(pes[14...])
        for q in ps[(i + 1)...] where pid(q) == 0x100 && q[3] & 0x10 != 0 { payload += q[payloadStart(q)...] }
        #expect(payload == au)
    }

    @Test func audioPESIsBoundedPrivateStream1() {
        let m = TSMuxer()
        m.enqueueAudio(frame: SilentAC3.frame, pts90: 99_999)
        let ps = packets(m.produce(count: 700))
        let first = ps.first { pid($0) == 0x101 && $0[1] & 0x40 != 0 }!
        let pes = Array(first[payloadStart(first)...])
        #expect(Array(pes[0..<4]) == [0, 0, 1, 0xBD])
        #expect(Int(pes[4]) << 8 | Int(pes[5]) == 3 + 5 + 768)
        #expect(TSMuxer.decodeTimestamp(pes[9..<14]) == 99_999)
        #expect(pes[14] == 0x0B && pes[15] == 0x77)  // AC-3 sync word
    }

    @Test func timestampRoundTrip() {
        for t: UInt64 in [0, 1, 90_000, 0x1_FFFF_FFFF, 8_589_934_591 / 3] {
            #expect(TSMuxer.decodeTimestamp(TSMuxer.timestamp(prefix: 2, t)[...]) == t)
        }
    }

    @Test func lateFramesAreCounted() {
        let m = TSMuxer()
        // A 1 MB keyframe whose DTS is only 100ms ahead: at 10 Mbit/s it takes 0.8s to send.
        m.enqueueVideo(annexB: [UInt8](repeating: 1, count: 1_000_000), pts90: m.now90 + 9000, keyframe: true)
        _ = m.produce(count: 7000)
        let s = m.takeStats()
        #expect(s.videoFrames == 1 && s.lateFrames == 1 && s.minKeyframeSlackMs < -500)
        // The same frame with 1s of slack is on time.
        m.enqueueVideo(annexB: [UInt8](repeating: 1, count: 1_000_000), pts90: m.now90 + 90_000, keyframe: true)
        _ = m.produce(count: 7000)
        let s2 = m.takeStats()
        #expect(s2.lateFrames == 0 && s2.minSlackMs > 100)
    }

    @Test func keyOffsetMarksTablesPCRThenKeyframe() {
        let m = TSMuxer()
        _ = m.produceChunk(count: 1000)  // past the startup tables
        m.enqueueVideo(annexB: [UInt8](repeating: 9, count: 5000), pts90: m.now90 + 18_000, keyframe: false)
        m.enqueueVideo(annexB: [UInt8](repeating: 7, count: 5000), pts90: m.now90 + 21_600, keyframe: true)
        let (d, k) = m.produceChunk(count: 300)
        let off = try! #require(k)
        let ps = packets(d.subdata(in: off..<d.count))
        #expect(pid(ps[0]) == 0 && pid(ps[1]) == 0x1000 && pid(ps[2]) == 0x11)
        #expect(pcr(ps[3]) != nil)
        #expect(pid(ps[4]) == 0x100 && ps[4][1] & 0x40 != 0 && ps[4][5] & 0x40 != 0)  // PES start, random access
        // a chunk without a keyframe has no offset
        #expect(m.produceChunk(count: 300).keyOffset == nil)
    }

    @Test func silentAC3FrameIsOneSyncFrame() {
        #expect(SilentAC3.frame.count == 768)
        #expect(SilentAC3.frame[0] == 0x0B && SilentAC3.frame[1] == 0x77)
        #expect(SilentAC3.frame[4] == 0x14)  // fscod 48 kHz, frmsizecod 192 kbit/s
    }
}

@Suite struct VideoTests {
    @Test func macBookScreenFitsInsideLevel41() {
        let r = Geometry.fit(source: CGSize(width: 2940, height: 1912), width: 1920, height: 1080, marginPercent: 0)
        #expect(r == CGRect(x: 130, y: 0, width: 1660, height: 1080))
    }

    @Test func marginShrinksPictureEvenly() {
        let r = Geometry.fit(source: CGSize(width: 2940, height: 1912), width: 1920, height: 1080, marginPercent: 4)
        #expect(r.height <= 1080 * 0.92 && r.height > 1080 * 0.91)
        #expect(Int(r.width) % 2 == 0 && Int(r.height) % 2 == 0 && Int(r.minX) % 2 == 0 && Int(r.minY) % 2 == 0)
        #expect(abs(r.midX - 960) <= 1 && abs(r.midY - 540) <= 1)
    }

    @Test func sixteenNineFillsFrame() {
        let r = Geometry.fit(source: CGSize(width: 3840, height: 2160), width: 1920, height: 1080, marginPercent: 0)
        #expect(r == CGRect(x: 0, y: 0, width: 1920, height: 1080))
    }

    @Test func avccToAnnexB() {
        let avcc: [UInt8] = [0, 0, 0, 2, 0x09, 0xF0, 0, 0, 0, 3, 0x65, 1, 2, 0, 0, 0, 1, 0x06]
        #expect(VideoEncoder.annexB(fromAVCC: avcc) == [0, 0, 0, 1, 0x65, 1, 2, 0, 0, 0, 1, 0x06])
    }
}
