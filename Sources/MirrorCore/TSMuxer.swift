import Foundation

/// MPEG-TS (188-byte packets) at a constant rate, laid out as ffmpeg's mpegts
/// muxer did for Phase 1 (same PIDs, stream types and descriptors), because
/// that is what the TV accepted.
///
/// Timing model: the stream clock is the byte position. Packet `i` carries
/// PCR = base + i·1504/muxRate seconds, and the pacer sends packet `i` at that
/// wall-clock moment, so PCR tracks real time exactly. A frame captured at
/// stream time `t` gets DTS = t + delay. Its packets go out as soon as it is
/// encoded (ahead of null padding), so it is late only if encode time plus
/// transmission time exceeds `delay`. The muxer measures that for every frame
/// (`Stats`), which is the "dts < pcr" ffmpeg used to report.
public final class TSMuxer {
    public static let packetSize = 188
    public static let nullPID: UInt16 = 0x1FFF

    public struct Config {
        public var muxRate: Int = 10_000_000
        public var includeAudio = true
        public var audioCodec: AudioCodec = .ac3
        public var pmtPID: UInt16 = 0x1000
        public var videoPID: UInt16 = 0x100
        public var audioPID: UInt16 = 0x101
        public var programNumber: UInt16 = 1
        /// PAT/PMT/SDT repeat (also sent right before every keyframe).
        public var tablesInterval: Double = 0.1
        /// Standalone PCR packets on the video PID (spec max 0.1s).
        public var pcrInterval: Double = 0.02
        public var serviceName = "TylerCast"
        public init() {}
    }

    public struct Stats {
        public var videoFrames = 0
        public var audioFrames = 0
        public var keyframes = 0
        /// Frames whose last packet left after PCR had passed their DTS.
        public var lateFrames = 0
        /// Smallest DTS − PCR at a frame's last packet, in milliseconds.
        public var minSlackMs: Double = .infinity
        public var minKeyframeSlackMs: Double = .infinity
        public var maxQueuedPackets = 0
        public var nullPackets = 0
        public var packets = 0
    }

    public let config: Config
    /// 1s of offset so timestamps never start at 0.
    public static let base90: UInt64 = 90_000

    public private(set) var packetsEmitted: UInt64 = 0
    private var cc: [UInt16: UInt8] = [:]
    private var queue: [Queued] = []
    private var queueHead = 0
    private var nextTablesAt: UInt64 = 0
    private var nextPCRAt: UInt64 = 0
    private var pendingTables: [[UInt8]] = []
    private var stats = Stats()

    private enum Queued {
        case tables
        case packet([UInt8], pid: UInt16, dts90: UInt64?, isLast: Bool, keyframe: Bool)
    }

    public init(config: Config = Config()) {
        self.config = config
    }

    // MARK: clock

    /// PCR (27 MHz) of packet index `i`.
    public func pcr27(atPacket i: UInt64) -> UInt64 {
        Self.base90 * 300 + i * 1504 * 27_000_000 / UInt64(config.muxRate)
    }
    /// Stream time (90 kHz) at packet index `i`.
    public func time90(atPacket i: UInt64) -> UInt64 { pcr27(atPacket: i) / 300 }
    public var now90: UInt64 { time90(atPacket: packetsEmitted) }
    private func packets(forSeconds s: Double) -> UInt64 { UInt64(max(1, s * Double(config.muxRate) / 1504)) }

    public var queuedPackets: Int { queue.count - queueHead }

    public func takeStats() -> Stats {
        let s = stats
        stats = Stats()
        return s
    }

    // MARK: input

    /// One H.264 access unit in Annex B form (start codes; AUD, and SPS/PPS on
    /// keyframes, already included). `pts90` is in stream time; PTS = DTS
    /// because there are no B-frames.
    public func enqueueVideo(annexB: [UInt8], pts90: UInt64, keyframe: Bool) {
        if keyframe { queue.append(.tables) }
        let pes = Self.pesPacket(streamID: 0xE0, pts90: pts90, payload: annexB, bounded: false)
        packetize(pes, pid: config.videoPID, dts90: pts90, randomAccess: keyframe)
    }

    /// One audio frame: an AC-3 sync frame, or an ADTS AAC frame.
    public func enqueueAudio(frame: [UInt8], pts90: UInt64) {
        guard config.includeAudio else { return }
        let streamID: UInt8 = config.audioCodec == .aac ? 0xC0 : 0xBD
        let pes = Self.pesPacket(streamID: streamID, pts90: pts90, payload: frame, bounded: true)
        packetize(pes, pid: config.audioPID, dts90: pts90, randomAccess: false)
    }

    private func packetize(_ pes: [UInt8], pid: UInt16, dts90: UInt64, randomAccess: Bool) {
        var off = 0
        var first = true
        while off < pes.count {
            // Room for payload: 184, less an adaptation field if this packet needs one.
            let afMin = (first && randomAccess) ? 2 : 0
            let remaining = pes.count - off
            let n = min(remaining, 184 - afMin)
            var p = [UInt8]()
            p.reserveCapacity(188)
            let afLen = 184 - n  // total adaptation-field bytes incl. its length byte
            p.append(0x47)
            p.append(UInt8((first ? 0x40 : 0) | Int((pid >> 8) & 0x1F)))
            p.append(UInt8(pid & 0xFF))
            p.append(afLen > 0 ? 0x30 : 0x10)  // CC filled in at emit time
            if afLen > 0 {
                p.append(UInt8(afLen - 1))
                if afLen > 1 {
                    p.append((first && randomAccess) ? 0x40 : 0x00)
                    p.append(contentsOf: repeatElement(0xFF, count: afLen - 2))
                }
            }
            p.append(contentsOf: pes[off..<(off + n)])
            off += n
            queue.append(.packet(p, pid: pid, dts90: dts90, isLast: off >= pes.count, keyframe: randomAccess))
            first = false
        }
    }

    // MARK: output

    public func produce(count: Int) -> Data { produceChunk(count: count).data }

    /// `keyOffset`: byte offset in `data` where a keyframe's PAT/PMT/SDT start,
    /// if one does: the point a newly connected TV can begin decoding at once.
    public func produceChunk(count: Int) -> (data: Data, keyOffset: Int?) {
        var d = Data(capacity: count * 188)
        let start = packetsEmitted
        lastKeyStart = nil
        for _ in 0..<count { d.append(contentsOf: nextPacket()) }
        return (d, lastKeyStart.map { Int($0 - start) * 188 })
    }
    private var lastKeyStart: UInt64?

    public func nextPacket() -> [UInt8] {
        stats.packets += 1
        stats.maxQueuedPackets = max(stats.maxQueuedPackets, queuedPackets)
        defer { packetsEmitted += 1 }

        if !pendingTables.isEmpty { return stamp(pendingTables.removeFirst()) }
        if packetsEmitted >= nextTablesAt {
            scheduleTables()
            return stamp(pendingTables.removeFirst())
        }
        if packetsEmitted >= nextPCRAt {
            nextPCRAt = packetsEmitted + packets(forSeconds: config.pcrInterval)
            return pcrPacket()
        }
        if queueHead < queue.count {
            let item = queue[queueHead]
            queueHead += 1
            if queueHead > 4096 { queue.removeFirst(queueHead); queueHead = 0 }
            switch item {
            case .tables:
                // A keyframe follows: tables, then a PCR, then the frame, so a
                // TV joining here has everything it needs in the first 4 packets.
                if lastKeyStart == nil { lastKeyStart = packetsEmitted }
                scheduleTables()
                nextPCRAt = packetsEmitted + 3
                return stamp(pendingTables.removeFirst())
            case let .packet(p, pid, dts90, isLast, keyframe):
                if isLast, let dts = dts90 { account(dts: dts, pid: pid, keyframe: keyframe) }
                return stamp(p)
            }
        }
        stats.nullPackets += 1
        return Self.nullPacket
    }

    private func account(dts: UInt64, pid: UInt16, keyframe: Bool) {
        // PCR of this packet's last byte ≈ the next packet's.
        let slackMs = (Double(dts) - Double(time90(atPacket: packetsEmitted + 1))) / 90
        if pid == config.videoPID {
            stats.videoFrames += 1
            if keyframe { stats.keyframes += 1; stats.minKeyframeSlackMs = min(stats.minKeyframeSlackMs, slackMs) }
        } else {
            stats.audioFrames += 1
        }
        stats.minSlackMs = min(stats.minSlackMs, slackMs)
        if slackMs < 0 { stats.lateFrames += 1 }
    }

    private func scheduleTables() {
        pendingTables = [Self.psiPacket(pid: 0, section: pat()),
                         Self.psiPacket(pid: config.pmtPID, section: pmt()),
                         Self.psiPacket(pid: 0x11, section: sdt())]
        nextTablesAt = packetsEmitted + packets(forSeconds: config.tablesInterval)
    }

    /// Sets the continuity counter (payload-bearing packets increment per PID).
    private func stamp(_ packet: [UInt8]) -> [UInt8] {
        var p = packet
        let pid = (UInt16(p[1] & 0x1F) << 8) | UInt16(p[2])
        let c = cc[pid].map { ($0 + 1) & 0x0F } ?? 0
        cc[pid] = c
        p[3] = (p[3] & 0xF0) | c
        return p
    }

    /// Adaptation-field-only packet carrying the PCR. No payload, so the
    /// continuity counter repeats the PID's last value.
    private func pcrPacket() -> [UInt8] {
        let pid = config.videoPID
        let c = cc[pid] ?? 0x0F
        var p: [UInt8] = [0x47, UInt8((pid >> 8) & 0x1F), UInt8(pid & 0xFF), 0x20 | c, 183, 0x10]
        p.append(contentsOf: Self.pcrBytes(pcr27(atPacket: packetsEmitted)))
        p.append(contentsOf: repeatElement(0xFF, count: 188 - p.count))
        return p
    }

    public static func pcrBytes(_ pcr27: UInt64) -> [UInt8] {
        let base = (pcr27 / 300) & 0x1_FFFF_FFFF
        let ext = pcr27 % 300
        return [UInt8((base >> 25) & 0xFF), UInt8((base >> 17) & 0xFF), UInt8((base >> 9) & 0xFF),
                UInt8((base >> 1) & 0xFF), UInt8(((base & 1) << 7) | 0x7E | ((ext >> 8) & 1)), UInt8(ext & 0xFF)]
    }

    public static let nullPacket: [UInt8] = [0x47, 0x1F, 0xFF, 0x10] + [UInt8](repeating: 0xFF, count: 184)

    // MARK: PES / PSI

    /// PES with PTS only (PTS = DTS). Video is unbounded (length 0) as ffmpeg writes it.
    public static func pesPacket(streamID: UInt8, pts90: UInt64, payload: [UInt8], bounded: Bool) -> [UInt8] {
        let headerData = 5
        let len = bounded ? min(3 + headerData + payload.count, 0xFFFF) : 0
        var p: [UInt8] = [0, 0, 1, streamID, UInt8(len >> 8), UInt8(len & 0xFF), 0x80, 0x80, UInt8(headerData)]
        p.append(contentsOf: timestamp(prefix: 0x2, pts90))
        p.append(contentsOf: payload)
        return p
    }

    public static func timestamp(prefix: UInt8, _ t: UInt64) -> [UInt8] {
        let v = t & 0x1_FFFF_FFFF
        return [(prefix << 4) | UInt8((v >> 29) & 0x0E) | 1,
                UInt8((v >> 22) & 0xFF), UInt8((v >> 14) & 0xFE) | 1,
                UInt8((v >> 7) & 0xFF), UInt8((v << 1) & 0xFE) | 1]
    }

    public static func decodeTimestamp(_ b: ArraySlice<UInt8>) -> UInt64 {
        let b = Array(b)
        return (UInt64(b[0] & 0x0E) << 29) | (UInt64(b[1]) << 22) | (UInt64(b[2] & 0xFE) << 14)
            | (UInt64(b[3]) << 7) | (UInt64(b[4]) >> 1)
    }

    /// A long-form section: table_id, length, the given body (from the
    /// extension field on), then CRC32.
    static func section(tableID: UInt8, idExt: UInt16, body: [UInt8], privateBit: Bool = false) -> [UInt8] {
        let length = 5 + body.count + 4
        var s: [UInt8] = [tableID, UInt8(0xB0 | (privateBit ? 0x40 : 0) | ((length >> 8) & 0x0F)), UInt8(length & 0xFF),
                          UInt8(idExt >> 8), UInt8(idExt & 0xFF), 0xC1, 0x00, 0x00]
        if privateBit { s[1] = UInt8(0xF0 | ((length >> 8) & 0x0F)) }
        s.append(contentsOf: body)
        let crc = crc32mpeg(s)
        s.append(contentsOf: [UInt8(crc >> 24), UInt8((crc >> 16) & 0xFF), UInt8((crc >> 8) & 0xFF), UInt8(crc & 0xFF)])
        return s
    }

    func pat() -> [UInt8] {
        Self.section(tableID: 0x00, idExt: 1, body: [UInt8(config.programNumber >> 8), UInt8(config.programNumber & 0xFF),
                                                      UInt8(0xE0 | (config.pmtPID >> 8)), UInt8(config.pmtPID & 0xFF)])
    }

    func pmt() -> [UInt8] {
        let v = config.videoPID, a = config.audioPID
        var body: [UInt8] = [UInt8(0xE0 | (v >> 8)), UInt8(v & 0xFF), 0xF0, 0x00,  // PCR PID, no program info
                             0x1B, UInt8(0xE0 | (v >> 8)), UInt8(v & 0xFF), 0xF0, 0x00]  // H.264
        if config.includeAudio {
            switch config.audioCodec {
            case .ac3:
                // AC-3, ATSC stream type 0x81 with an "AC-3" registration descriptor (as ffmpeg).
                body += [0x81, UInt8(0xE0 | (a >> 8)), UInt8(a & 0xFF), 0xF0, 0x06, 0x05, 0x04, 0x41, 0x43, 0x2D, 0x33]
            case .aac:
                // AAC in ADTS, stream type 0x0F, no descriptors (as ffmpeg).
                body += [0x0F, UInt8(0xE0 | (a >> 8)), UInt8(a & 0xFF), 0xF0, 0x00]
            }
        }
        return Self.section(tableID: 0x02, idExt: config.programNumber, body: body)
    }

    func sdt() -> [UInt8] {
        let provider = Array("TylerCast".utf8), name = Array(config.serviceName.utf8)
        let desc: [UInt8] = [0x48, UInt8(3 + provider.count + name.count), 0x01, UInt8(provider.count)] + provider
            + [UInt8(name.count)] + name
        var body: [UInt8] = [0xFF, 0x01, 0xFF]  // original_network_id 0xFF01, reserved
        body += [UInt8(config.programNumber >> 8), UInt8(config.programNumber & 0xFF), 0xFC,
                 UInt8(0x80 | ((desc.count >> 8) & 0x0F)), UInt8(desc.count & 0xFF)] + desc  // running, not scrambled
        return Self.section(tableID: 0x42, idExt: 1, body: body, privateBit: true)
    }

    static func psiPacket(pid: UInt16, section: [UInt8]) -> [UInt8] {
        var p: [UInt8] = [0x47, UInt8(0x40 | ((pid >> 8) & 0x1F)), UInt8(pid & 0xFF), 0x10, 0x00]
        p.append(contentsOf: section)
        p.append(contentsOf: repeatElement(0xFF, count: 188 - p.count))
        return p
    }
}

/// CRC-32/MPEG-2 (poly 0x04C11DB7, init 0xFFFFFFFF, no reflection, no final xor).
public func crc32mpeg<S: Sequence>(_ bytes: S) -> UInt32 where S.Element == UInt8 {
    var crc: UInt32 = 0xFFFF_FFFF
    for b in bytes {
        crc ^= UInt32(b) << 24
        for _ in 0..<8 { crc = (crc & 0x8000_0000) != 0 ? (crc << 1) ^ 0x04C1_1DB7 : crc << 1 }
    }
    return crc
}
