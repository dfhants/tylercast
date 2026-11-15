import Foundation

/// AudioToolbox on macOS has no AC-3 encoder (AudioConverterNew to kAudioFormatAC3
/// returns 'fmt?'; the encoder list is aac, alac, flac, opus, … only), yet the TV
/// lists H.264 TS only with AC-3 audio (AVC_TS_HD_50_AC3_ISO). Until there is an
/// encoder, the stream carries this: one pre-encoded AC-3 frame of digital
/// silence (48 kHz stereo, 192 kbit/s, 1536 samples = 32 ms, 768 bytes; made
/// once with `ffmpeg -f lavfi -i anullsrc=r=48000:cl=stereo -c:a ac3 -b:a 192k`),
/// repeated. Each AC-3 frame is self-contained (sync word + CRCs), so repeating
/// one is a valid stream, and the transport stream matches the profile the TV
/// accepted in Phase 1.
public enum SilentAC3 {
    public static let samplesPerFrame = 1536
    public static let sampleRate = 48_000
    public static let frameDuration = Double(samplesPerFrame) / Double(sampleRate)
    public static let frame: [UInt8] = Array(Data(base64Encoded: "C3dQUhRAQ+EG9GNwgICCEBAQQVx8+fPnz58+fPnz58+fPnz58+fPnz58+fPv+dXz58+fPnz58+fPnz58+fPnz58+fPnz58+fPnz58+fPnz58+fPj/nV8+fPnz58+fPnz58+fPnz58+fPnz58+fPnz58+fPnz58+fPnz4y/xRIkSAAAAAAHjbbbbx48eO7u7gAAAAAAAAAAAAAAAAAAAAAB3d3d48eNttvnzWta1rWta1rWta1oAAAAB422228ePHju7u4AAAAAAAAAAAAAAAAAAAAAAd3d3ePHjbbb581rWta1rWta1rWtaYAAAAAAAHjbbbbx48eO7u7gAAAAAAAAAAAAAAAAAAAAAB3d3d48eNttvnzWta1rWta1rWta1oAAAAB422228ePHju7u4AAAAAAAAAAAAAAAAAAAAAAd3d3ePHjbbb581rWta1rWta1rWtaYAAAAAAAHjbbbbx48eO7u7gAAAAAAAAAAAAAAAAAAAAAB3d3d48eNttvnzWta1rWta1rWta1oAAAAB422228ePHju7u4AAAAAAAAAAAAAAAAAAAAAAd3d3ePHjbbb581rWta1rWta1rWtaYAAAAAAAHjbbbbx48eO7u7gAAAAAAAAAAAAAAAAAAAAAB3d3d48eNttvnzWta1rWta1rWta1oAAAAB422228ePHju7u4AAAAAAAAAAAAAAAAAAAAAAd3d3ePHjbbb581rWta1rWta1rWtaYAAAAAAAHjbbbbx48eO7u7gAAAAAAAAAAAAAAAAAAAAAB3d3d48eNttvnzWta1rWta1rWta1oAAAAB422228ePHju7u4AAAAAAAAAAAAAAAAAAAAAAd3d3ePHjbbb581rWta1rWta1rWtaYAAAAAAAHjbbbbx48eO7u7gAAAAAAAAAAAAAAAAAAAAAB3d3d48eNttvnzWta1rWta1rWta1oAAAAB422228ePHju7u4AAAAAAAAAAAAAAAAAAAAAAd3d3ePHjbbb581rWta1rWta1rWtaPR7")!)
}

/// Feeds silent AC-3 frames into the pacer's muxer in real time.
public final class SilentAudioSource {
    private var next: Double?
    public init() {}

    /// Call from Pacer.onTick (muxer lock held).
    public func tick(host now: Double, pacer: Pacer) {
        var t = next ?? now
        while t <= now {
            pacer.muxer.enqueueAudio(frame: SilentAC3.frame, pts90: pacer.pts90(forHost: t))
            t += SilentAC3.frameDuration
        }
        next = t
    }
}
