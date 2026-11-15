import AudioToolbox
import CoreMedia
import Foundation

/// The audio formats the muxer can carry.
public enum AudioCodec: String, Sendable {
    /// ATSC AC-3 (stream type 0x81, private stream 1): what the test TV lists for H.264 TS.
    case ac3
    /// AAC-LC in ADTS (stream type 0x0F, audio stream 0xC0).
    case aac
}

/// 48 kHz stereo PCM, interleaved Float32, from ScreenCaptureKit's audio
/// sample buffers (which are Float32, usually non-interleaved).
public enum PCM {
    public static let sampleRate = 48_000
    public static let channels = 2

    /// Interleaved stereo samples (L R L R …), or nil if the format isn't Float32.
    public static func interleaved(_ sb: CMSampleBuffer) -> [Float]? {
        guard let fd = CMSampleBufferGetFormatDescription(sb),
              let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(fd)?.pointee,
              asbd.mFormatID == kAudioFormatLinearPCM, asbd.mFormatFlags & kAudioFormatFlagIsFloat != 0,
              asbd.mBitsPerChannel == 32 else { return nil }
        let frames = CMSampleBufferGetNumSamples(sb)
        guard frames > 0 else { return [] }
        let srcChannels = Int(asbd.mChannelsPerFrame)
        let nonInterleaved = asbd.mFormatFlags & kAudioFormatFlagIsNonInterleaved != 0

        var sizeNeeded = 0
        CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(sb, bufferListSizeNeededOut: &sizeNeeded, bufferListOut: nil,
                                                                bufferListSize: 0, blockBufferAllocator: nil,
                                                                blockBufferMemoryAllocator: nil, flags: 0, blockBufferOut: nil)
        let raw = UnsafeMutableRawPointer.allocate(byteCount: sizeNeeded, alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }
        let abl = raw.bindMemory(to: AudioBufferList.self, capacity: 1)
        var block: CMBlockBuffer?
        guard CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sb, bufferListSizeNeededOut: nil, bufferListOut: abl, bufferListSize: sizeNeeded, blockBufferAllocator: nil,
            blockBufferMemoryAllocator: nil, flags: kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment,
            blockBufferOut: &block) == noErr else { return nil }
        let buffers = UnsafeMutableAudioBufferListPointer(abl)

        var out = [Float](repeating: 0, count: frames * channels)
        func sample(_ frame: Int, _ ch: Int) -> Float {
            let c = min(ch, srcChannels - 1)  // mono source: same sample both sides
            if nonInterleaved {
                guard c < buffers.count, let d = buffers[c].mData else { return 0 }
                return d.assumingMemoryBound(to: Float.self)[frame]
            }
            guard let d = buffers[0].mData else { return 0 }
            return d.assumingMemoryBound(to: Float.self)[frame * srcChannels + c]
        }
        for f in 0..<frames {
            out[f * 2] = sample(f, 0)
            out[f * 2 + 1] = sample(f, 1)
        }
        return out
    }
}

/// AAC-LC encoder (AudioToolbox) producing ADTS frames of 1024 samples,
/// each with the host time of its first sample.
public final class AACEncoder {
    public static let samplesPerFrame = 1024
    public let bitrate: Int
    /// (ADTS frame, host seconds of its first sample)
    public var onFrame: (([UInt8], Double) -> Void)?

    private var converter: AudioConverterRef?
    private var fifo: [Float] = []
    /// Host time of the first sample since the last (re)start, and counts
    /// since then: frame k starts at anchor + k·1024/48000, exactly.
    private var anchor: Double?
    private var samplesIn = 0
    private var framesOut = 0
    private var maxPacketSize = 0

    /// Rate of the PCM pushed in; the AAC is always 48 kHz (the converter resamples).
    public let inputSampleRate: Double

    public init(bitrate: Int = 160_000, inputSampleRate: Double = Double(PCM.sampleRate)) throws {
        self.bitrate = bitrate
        self.inputSampleRate = inputSampleRate
        var input = AudioStreamBasicDescription(
            mSampleRate: inputSampleRate, mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked, mBytesPerPacket: 8, mFramesPerPacket: 1,
            mBytesPerFrame: 8, mChannelsPerFrame: 2, mBitsPerChannel: 32, mReserved: 0)
        var output = AudioStreamBasicDescription(
            mSampleRate: Double(PCM.sampleRate), mFormatID: kAudioFormatMPEG4AAC,
            mFormatFlags: AudioFormatFlags(MPEG4ObjectID.AAC_LC.rawValue), mBytesPerPacket: 0,
            mFramesPerPacket: UInt32(Self.samplesPerFrame), mBytesPerFrame: 0, mChannelsPerFrame: 2,
            mBitsPerChannel: 0, mReserved: 0)
        var conv: AudioConverterRef?
        var status = AudioConverterNew(&input, &output, &conv)
        guard status == noErr, let conv else { throw CaptureError(description: "AAC encoder unavailable (\(status))") }
        var rate = UInt32(bitrate)
        status = AudioConverterSetProperty(conv, kAudioConverterEncodeBitRate, UInt32(MemoryLayout<UInt32>.size), &rate)
        if status != noErr { Log.warn("AAC: could not set bitrate \(bitrate) (\(status))") }
        var size = UInt32(MemoryLayout<UInt32>.size)
        var maxSize: UInt32 = 0
        AudioConverterGetProperty(conv, kAudioConverterPropertyMaximumOutputPacketSize, &size, &maxSize)
        maxPacketSize = Int(max(maxSize, 2048))
        converter = conv
    }

    deinit { if let converter { AudioConverterDispose(converter) } }

    private let lock = NSLock()

    /// Adds interleaved stereo samples whose first sample was captured at `host`.
    public func push(_ samples: [Float], host: Double) {
        lock.lock(); defer { lock.unlock() }
        pushLocked(samples, host: host)
    }

    /// Fills silence up to `host − lag` when no sound has arrived (the Core
    /// Audio tap delivers nothing while the Mac is quiet), so the TV always
    /// gets a continuous audio track. Call every few tens of milliseconds.
    public func padSilence(upTo host: Double, lag: Double = 0.1) {
        lock.lock(); defer { lock.unlock() }
        let target = host - lag
        guard let anchor else {
            self.anchor = target; samplesIn = 0; framesOut = 0
            return
        }
        let have = anchor + Double(samplesIn) / inputSampleRate
        let missing = Int((target - have) * inputSampleRate)
        if missing > 0 { pushLocked([Float](repeating: 0, count: missing * 2), host: have) }
    }

    private func pushLocked(_ samples: [Float], host: Double) {
        var samples = samples
        if let anchor {
            let expected = anchor + Double(samplesIn) / inputSampleRate
            let diff = host - expected
            if abs(diff) > 0.5 {
                // A real jump (capture restarted): start a new timeline here.
                Log.debug("AAC: timing jump of \(Int(diff * 1000))ms; re-anchoring")
                fifo.removeAll(keepingCapacity: true)
                self.anchor = nil
            } else if diff > 0.002 {
                // Arrived a little late (e.g. after padded silence): fill the hole.
                samples = [Float](repeating: 0, count: Int(diff * inputSampleRate) * 2) + samples
            } else if diff < -0.002 {
                // Overlaps what's already queued (padding ran ahead): drop the overlap.
                let drop = min(Int(-diff * inputSampleRate) * 2, samples.count)
                samples.removeFirst(drop)
                if samples.isEmpty { return }
            }
        }
        if anchor == nil { anchor = host; samplesIn = 0; framesOut = 0 }
        samplesIn += samples.count / 2
        fifo += samples
        while fifo.count >= inputChunk * 2 {
            encodeOne()
        }
    }

    /// Input samples per AAC frame (1024 at 48 kHz; e.g. 941 at 44.1 kHz).
    private var inputChunk: Int { Int((Double(Self.samplesPerFrame) * inputSampleRate / Double(PCM.sampleRate)).rounded()) }

    private struct Input { var samples: UnsafeMutablePointer<Float>; var count: Int; var consumed: Bool }

    private func encodeOne() {
        guard let converter, let anchor else { return }
        let n = inputChunk * 2
        let buf = UnsafeMutablePointer<Float>.allocate(capacity: n)
        defer { buf.deallocate() }
        fifo.withUnsafeBufferPointer { buf.initialize(from: $0.baseAddress!, count: n) }
        var input = Input(samples: buf, count: n, consumed: false)
        fifo.removeFirst(n)

        var out = [UInt8](repeating: 0, count: maxPacketSize)
        var packets: UInt32 = 1
        var desc = AudioStreamPacketDescription()
        let status: OSStatus = out.withUnsafeMutableBytes { outBytes in
            var abl = AudioBufferList(mNumberBuffers: 1, mBuffers: AudioBuffer(
                mNumberChannels: 2, mDataByteSize: UInt32(outBytes.count), mData: outBytes.baseAddress))
            return withUnsafeMutablePointer(to: &input) { ctx in
                AudioConverterFillComplexBuffer(converter, { _, ioPackets, ioData, _, user in
                    let inp = user!.assumingMemoryBound(to: Input.self)
                    if inp.pointee.consumed { ioPackets.pointee = 0; return 1 }  // no more for now
                    inp.pointee.consumed = true
                    ioPackets.pointee = UInt32(inp.pointee.count / 2)
                    ioData.pointee.mNumberBuffers = 1
                    ioData.pointee.mBuffers.mNumberChannels = 2
                    ioData.pointee.mBuffers.mDataByteSize = UInt32(inp.pointee.count * MemoryLayout<Float>.size)
                    ioData.pointee.mBuffers.mData = UnsafeMutableRawPointer(inp.pointee.samples)
                    return noErr
                }, ctx, &packets, &abl, &desc)
            }
        }
        // 1 = our "no more input" code: the converter returns what it has.
        guard status == noErr || status == 1 else { Log.error("AAC encode failed (\(status))"); return }
        guard packets > 0, desc.mDataByteSize > 0 else { return }  // encoder still priming
        let payload = Array(out[Int(desc.mStartOffset)..<Int(desc.mStartOffset) + Int(desc.mDataByteSize)])
        let start = anchor + Double(framesOut * Self.samplesPerFrame) / Double(PCM.sampleRate)
        framesOut += 1
        onFrame?(Self.adtsHeader(payloadBytes: payload.count) + payload, start)
    }

    /// 7-byte ADTS header (no CRC): MPEG-4, AAC-LC, 48 kHz, stereo.
    public static func adtsHeader(payloadBytes: Int) -> [UInt8] {
        let len = payloadBytes + 7
        let profile: UInt8 = 1  // AAC-LC (object type 2) − 1
        let freqIndex: UInt8 = 3  // 48000
        let channels: UInt8 = 2
        return [0xFF, 0xF1,  // sync, MPEG-4, layer 0, no CRC
                (profile << 6) | (freqIndex << 2) | (channels >> 2),
                ((channels & 3) << 6) | UInt8((len >> 11) & 0x03),
                UInt8((len >> 3) & 0xFF),
                UInt8((len & 0x07) << 5) | 0x1F,
                0xFC]  // buffer fullness 0x7FF (VBR), 1 raw block
    }
}
