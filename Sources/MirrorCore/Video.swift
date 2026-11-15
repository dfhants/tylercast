import CoreGraphics
import CoreMedia
import CoreVideo
import Foundation
import ScreenCaptureKit
import VideoToolbox

public enum Geometry {
    /// Where the screen goes inside the output frame: aspect kept, centred,
    /// inside a `marginPercent` black border on each edge, even-sized for 4:2:0.
    /// A 3:2 2940x1912 screen in 1920x1080 is 1660x1080 at x=130 (side bars),
    /// never 1920x1248, which is over H.264 level 4.1's frame size (-12902).
    public static func fit(source: CGSize, width: Int, height: Int, marginPercent: Double) -> CGRect {
        let m = max(0, min(marginPercent, 20)) / 100
        let boxW = Double(width) * (1 - 2 * m), boxH = Double(height) * (1 - 2 * m)
        guard source.width > 0, source.height > 0 else { return CGRect(x: 0, y: 0, width: width, height: height) }
        let s = min(boxW / source.width, boxH / source.height)
        let w = Int(source.width * s) / 2 * 2, h = Int(source.height * s) / 2 * 2
        return CGRect(x: (width - w) / 4 * 2, y: (height - h) / 4 * 2, width: w, height: h)
    }
}

public struct VideoSettings {
    public var width = 1920
    public var height = 1080
    public var fps = 25
    public var bitrate = 6_000_000
    /// Caps bursts (keyframes): at most bitrate·window bits in any `window` seconds.
    public var rateWindow = 0.5
    public var keyframeSeconds = 2.0
    public init() {}
}

public struct EncoderError: Error, CustomStringConvertible {
    public var description: String
}

/// VideoToolbox H.264 Main@4.1, real time, no B-frames, keyframe every 2s.
/// Emits Annex B access units: AUD, then SPS+PPS on keyframes, then the slices.
public final class VideoEncoder {
    public let settings: VideoSettings
    /// (access unit, presentation host time in seconds, keyframe)
    public var onAccessUnit: (([UInt8], Double, Bool) -> Void)?
    private var session: VTCompressionSession?
    public private(set) var framesOut = 0

    public init(settings: VideoSettings) throws {
        self.settings = settings
        var s: VTCompressionSession?
        let spec = [kVTVideoEncoderSpecification_EnableHardwareAcceleratedVideoEncoder: true] as CFDictionary
        var st = VTCompressionSessionCreate(allocator: nil, width: Int32(settings.width), height: Int32(settings.height),
                                            codecType: kCMVideoCodecType_H264, encoderSpecification: spec,
                                            imageBufferAttributes: nil, compressedDataAllocator: nil,
                                            outputCallback: nil, refcon: nil, compressionSessionOut: &s)
        guard st == noErr, let s else { throw EncoderError(description: "VTCompressionSessionCreate failed: \(st)") }
        let bytesPerWindow = Double(settings.bitrate) / 8 * settings.rateWindow
        let props: [CFString: Any] = [
            kVTCompressionPropertyKey_RealTime: true,
            kVTCompressionPropertyKey_ProfileLevel: kVTProfileLevel_H264_Main_4_1,
            kVTCompressionPropertyKey_H264EntropyMode: kVTH264EntropyMode_CABAC,
            kVTCompressionPropertyKey_AllowFrameReordering: false,
            kVTCompressionPropertyKey_ExpectedFrameRate: settings.fps,
            kVTCompressionPropertyKey_MaxKeyFrameInterval: Int(Double(settings.fps) * settings.keyframeSeconds),
            kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration: settings.keyframeSeconds,
            kVTCompressionPropertyKey_AverageBitRate: settings.bitrate,
            kVTCompressionPropertyKey_DataRateLimits: [bytesPerWindow, settings.rateWindow] as CFArray,
            kVTCompressionPropertyKey_ColorPrimaries: kCVImageBufferColorPrimaries_ITU_R_709_2,
            kVTCompressionPropertyKey_TransferFunction: kCVImageBufferTransferFunction_ITU_R_709_2,
            kVTCompressionPropertyKey_YCbCrMatrix: kCVImageBufferYCbCrMatrix_ITU_R_709_2,
        ]
        for (k, v) in props {
            st = VTSessionSetProperty(s, key: k, value: v as CFTypeRef)
            if st != noErr { Log.warn("VideoToolbox: setting \(k) failed: \(st)") }
        }
        st = VTCompressionSessionPrepareToEncodeFrames(s)
        guard st == noErr else { throw EncoderError(description: "VideoToolbox prepare failed: \(st)") }
        session = s
        Log.info("Encoder: H.264 Main 4.1 \(settings.width)x\(settings.height) \(settings.fps)fps "
            + "\(settings.bitrate / 1000)k, keyframe every \(settings.keyframeSeconds)s, no B-frames")
    }

    public func encode(_ pb: CVPixelBuffer, host: Double, forceKeyframe: Bool = false) {
        guard let session else { return }
        let pts = CMTime(value: CMTimeValue(host * 1_000_000), timescale: 1_000_000)
        let dur = CMTime(value: 1, timescale: CMTimeScale(settings.fps))
        let props = forceKeyframe ? [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary : nil
        let st = VTCompressionSessionEncodeFrame(session, imageBuffer: pb, presentationTimeStamp: pts, duration: dur,
                                                 frameProperties: props, infoFlagsOut: nil) { [weak self] status, _, sb in
            guard let self else { return }
            guard status == noErr, let sb else { Log.error("VideoToolbox encode error \(status)"); return }
            self.emit(sb)
        }
        if st != noErr { Log.error("VTCompressionSessionEncodeFrame: \(st)") }
    }

    public func invalidate() {
        if let s = session {
            VTCompressionSessionCompleteFrames(s, untilPresentationTimeStamp: .invalid)
            VTCompressionSessionInvalidate(s)
        }
        session = nil
    }

    private func emit(_ sb: CMSampleBuffer) {
        guard let block = CMSampleBufferGetDataBuffer(sb), let fmt = CMSampleBufferGetFormatDescription(sb) else { return }
        var keyframe = true
        if let atts = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: false) as? [[CFString: Any]],
           let notSync = atts.first?[kCMSampleAttachmentKey_NotSync] as? Bool {
            keyframe = !notSync
        }
        let len = CMBlockBufferGetDataLength(block)
        var avcc = [UInt8](repeating: 0, count: len)
        CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: len, destination: &avcc)

        var nalHeaderLen: Int32 = 4
        var paramCount = 0
        CMVideoFormatDescriptionGetH264ParameterSetAtIndex(fmt, parameterSetIndex: 0, parameterSetPointerOut: nil,
                                                           parameterSetSizeOut: nil, parameterSetCountOut: &paramCount,
                                                           nalUnitHeaderLengthOut: &nalHeaderLen)
        var au: [UInt8] = [0, 0, 0, 1, 0x09, 0xF0]  // access unit delimiter, as ffmpeg writes
        au.reserveCapacity(len + 128)
        if keyframe {
            for i in 0..<paramCount {
                var ptr: UnsafePointer<UInt8>?
                var size = 0
                if CMVideoFormatDescriptionGetH264ParameterSetAtIndex(fmt, parameterSetIndex: i, parameterSetPointerOut: &ptr,
                                                                      parameterSetSizeOut: &size, parameterSetCountOut: nil,
                                                                      nalUnitHeaderLengthOut: nil) == noErr, let ptr {
                    au += [0, 0, 0, 1]
                    au.append(contentsOf: UnsafeBufferPointer(start: ptr, count: size))
                }
            }
        }
        au.append(contentsOf: Self.annexB(fromAVCC: avcc, lengthSize: Int(nalHeaderLen)))
        framesOut += 1
        onAccessUnit?(au, CMSampleBufferGetPresentationTimeStamp(sb).seconds, keyframe)
    }

    /// Length-prefixed NAL units to start-code-prefixed ones (AUDs dropped: one is added).
    public static func annexB(fromAVCC data: [UInt8], lengthSize: Int = 4) -> [UInt8] {
        var out = [UInt8]()
        out.reserveCapacity(data.count + 16)
        var i = 0
        while i + lengthSize <= data.count {
            var n = 0
            for k in 0..<lengthSize { n = (n << 8) | Int(data[i + k]) }
            i += lengthSize
            guard n > 0, i + n <= data.count else { break }
            if data[i] & 0x1F != 9 {
                out += [0, 0, 0, 1]
                out.append(contentsOf: data[i..<(i + n)])
            }
            i += n
        }
        return out
    }
}

/// Feeds the encoder at exactly `fps`, repeating the latest picture when the
/// screen has not changed (ScreenCaptureKit only sends frames on change), so
/// the stream has the steady 25fps the TV's profile expects.
public final class FrameClock {
    private let fps: Int
    private let encoder: VideoEncoder
    private let queue = DispatchQueue(label: "frame-clock", qos: .userInteractive)
    private var timer: DispatchSourceTimer?
    private let lock = NSLock()
    private var latest: CVPixelBuffer?
    private var startHost = 0.0
    private var lastIndex = -1
    /// For sources that draw per tick (the test pattern); else the latest pushed frame is used.
    public var render: ((Int) -> CVPixelBuffer?)?

    public init(fps: Int, encoder: VideoEncoder) {
        self.fps = fps
        self.encoder = encoder
    }

    public func push(_ pb: CVPixelBuffer) {
        lock.lock(); latest = pb; lock.unlock()
    }

    private var keyframeRequested = false
    /// The next frame is encoded as a keyframe (a TV has just connected).
    public func requestKeyframe() {
        lock.lock(); keyframeRequested = true; lock.unlock()
    }

    public func start() {
        startHost = Pacer.hostSeconds()
        let t = DispatchSource.makeTimerSource(flags: .strict, queue: queue)
        t.schedule(deadline: .now(), repeating: .nanoseconds(1_000_000_000 / fps / 4), leeway: .milliseconds(1))
        t.setEventHandler { [weak self] in self?.tick() }
        timer = t
        t.resume()
    }

    public func stop() {
        timer?.cancel()
        timer = nil
    }

    private func tick() {
        let now = Pacer.hostSeconds()
        let index = Int((now - startHost) * Double(fps))
        guard index > lastIndex else { return }
        lastIndex = index
        let host = startHost + Double(index) / Double(fps)
        let pb: CVPixelBuffer?
        lock.lock()
        let force = keyframeRequested && (render != nil || latest != nil)
        if force { keyframeRequested = false }
        let last = latest
        lock.unlock()
        if let render { pb = render(index) } else { pb = last }
        if let pb { encoder.encode(pb, host: host, forceKeyframe: force) }
    }
}

public struct CaptureError: Error, CustomStringConvertible {
    public var description: String
}

/// ScreenCaptureKit capture of the main display, delivered by SCK already
/// scaled to the output size with the black bars and margin in place (GPU
/// scaling; the CPU never touches full-resolution frames), as NV12 for
/// VideoToolbox.
public final class ScreenCapture: NSObject, SCStreamOutput, SCStreamDelegate {
    public var onFrame: ((CVPixelBuffer) -> Void)?
    public var onError: ((Error) -> Void)?
    /// Also capture the Mac's sound (48 kHz stereo; TylerCast's own sound left out).
    public var capturesAudio = false
    /// Interleaved stereo Float32 samples and the host time of the first one.
    public var onAudio: (([Float], Double) -> Void)?
    private let audioQueue = DispatchQueue(label: "audio-capture", qos: .userInteractive)
    private var stream: SCStream?
    private var config = SCStreamConfiguration()
    private var displaySize = CGSize.zero
    private let queue = DispatchQueue(label: "screen-capture", qos: .userInteractive)
    public private(set) var framesIn = 0
    public private(set) var audioBuffersIn = 0

    public override init() {}

    public static var hasPermission: Bool { CGPreflightScreenCaptureAccess() }

    public func start(settings: VideoSettings, marginPercent: Double) async throws {
        let content: SCShareableContent
        do {
            content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        } catch {
            throw CaptureError(description: "Screen Recording permission is missing or was refused (\(error.localizedDescription))")
        }
        guard let display = content.displays.first(where: { $0.displayID == CGMainDisplayID() }) ?? content.displays.first else {
            throw CaptureError(description: "no display to capture")
        }
        displaySize = CGSize(width: display.width, height: display.height)
        let c = SCStreamConfiguration()
        c.width = settings.width
        c.height = settings.height
        c.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(settings.fps))
        c.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        c.colorSpaceName = CGColorSpace.itur_709
        c.colorMatrix = CGDisplayStream.yCbCrMatrix_ITU_R_709_2
        c.showsCursor = true
        c.queueDepth = 6
        c.backgroundColor = .black
        c.preservesAspectRatio = true
        c.destinationRect = Geometry.fit(source: displaySize, width: settings.width, height: settings.height,
                                         marginPercent: marginPercent)
        // The Mac keeps playing its own sound; SCK hands us a copy.
        c.capturesAudio = capturesAudio
        c.sampleRate = PCM.sampleRate
        c.channelCount = PCM.channels
        c.excludesCurrentProcessAudio = true
        config = c
        let s = SCStream(filter: SCContentFilter(display: display, excludingWindows: []), configuration: c, delegate: self)
        try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        if capturesAudio { try s.addStreamOutput(self, type: .audio, sampleHandlerQueue: audioQueue) }
        try await s.startCapture()
        stream = s
        Log.info("Capture: display \(display.displayID) \(display.width)x\(display.height)pt -> \(c.width)x\(c.height), "
            + "picture at \(c.destinationRect) (margin \(marginPercent)%)")
    }

    /// Changes the margin on the running stream.
    public func setMargin(_ marginPercent: Double) async {
        guard let stream else { return }
        config.destinationRect = Geometry.fit(source: displaySize, width: config.width, height: config.height,
                                              marginPercent: marginPercent)
        do {
            try await stream.updateConfiguration(config)
            Log.info("Capture: margin \(marginPercent)%, picture at \(config.destinationRect)")
        } catch {
            Log.error("Capture: could not change margin: \(error)")
        }
    }

    public func stop() async {
        try? await stream?.stopCapture()
        stream = nil
    }

    public func stream(_ stream: SCStream, didOutputSampleBuffer sb: CMSampleBuffer, of type: SCStreamOutputType) {
        if type == .audio {
            guard sb.isValid, let samples = PCM.interleaved(sb) else { return }
            if audioBuffersIn == 0 {
                let pts = CMSampleBufferGetPresentationTimeStamp(sb).seconds
                Log.info("Capture: first sound buffer (\(samples.count / 2) samples; timestamp vs host clock "
                    + "\(Int((pts - Pacer.hostSeconds()) * 1000))ms)")
            }
            audioBuffersIn += 1
            onAudio?(samples, CMSampleBufferGetPresentationTimeStamp(sb).seconds)
            return
        }
        guard type == .screen, sb.isValid, let pb = CMSampleBufferGetImageBuffer(sb) else { return }
        if let atts = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
           let raw = atts.first?[.status] as? Int, let status = SCFrameStatus(rawValue: raw), status != .complete {
            return  // idle/blank: nothing new; the frame clock repeats the last picture
        }
        framesIn += 1
        onFrame?(pb)
    }

    public func stream(_ stream: SCStream, didStopWithError error: Error) {
        Log.error("Capture stopped: \(error.localizedDescription)")
        onError?(error)
    }
}

/// Colour bars with a moving box: tests encode/mux/DLNA with no capture
/// (and no Screen Recording permission), as `mirror.py play --test-pattern`.
public final class TestPattern {
    private let width: Int, height: Int
    private var pool: CVPixelBufferPool?
    private var templateY: [UInt8], templateUV: [UInt8]

    public init(width: Int, height: Int) {
        self.width = width
        self.height = height
        // 75% bars, BT.709 video range (Y, Cb, Cr)
        let bars: [(UInt8, UInt8, UInt8)] = [(180, 128, 128), (168, 44, 136), (145, 147, 44), (134, 63, 52),
                                              (63, 193, 204), (51, 109, 212), (28, 212, 120), (16, 128, 128)]
        templateY = [UInt8](repeating: 16, count: width * height)
        templateUV = [UInt8](repeating: 128, count: width * height / 2)
        for y in 0..<height * 2 / 3 {
            for x in 0..<width { templateY[y * width + x] = bars[x * 8 / width].0 }
        }
        for y in 0..<height / 3 {
            for x in stride(from: 0, to: width, by: 2) {
                let b = bars[x * 8 / width]
                templateUV[y * width + x] = b.1
                templateUV[y * width + x + 1] = b.2
            }
        }
        let attrs: [CFString: Any] = [
            kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            kCVPixelBufferWidthKey: width, kCVPixelBufferHeightKey: height,
            kCVPixelBufferIOSurfacePropertiesKey: [:] as CFDictionary,
        ]
        CVPixelBufferPoolCreate(nil, nil, attrs as CFDictionary, &pool)
    }

    public func frame(_ index: Int) -> CVPixelBuffer? {
        guard let pool else { return nil }
        var out: CVPixelBuffer?
        CVPixelBufferPoolCreatePixelBuffer(nil, pool, &out)
        guard let pb = out else { return nil }
        CVPixelBufferLockBaseAddress(pb, [])
        defer { CVPixelBufferUnlockBaseAddress(pb, []) }
        let yRow = CVPixelBufferGetBytesPerRowOfPlane(pb, 0), uvRow = CVPixelBufferGetBytesPerRowOfPlane(pb, 1)
        let yp = CVPixelBufferGetBaseAddressOfPlane(pb, 0)!.assumingMemoryBound(to: UInt8.self)
        let uvp = CVPixelBufferGetBaseAddressOfPlane(pb, 1)!.assumingMemoryBound(to: UInt8.self)
        templateY.withUnsafeBufferPointer { t in
            for y in 0..<height { (yp + y * yRow).update(from: t.baseAddress! + y * width, count: width) }
        }
        templateUV.withUnsafeBufferPointer { t in
            for y in 0..<height / 2 { (uvp + y * uvRow).update(from: t.baseAddress! + y * width, count: width) }
        }
        // A white box crossing the bottom third once every 4 seconds (100 frames).
        let box = height / 6
        let x0 = (index % 100) * (width - box) / 100 / 2 * 2
        let y0 = height * 3 / 4 - box / 2
        for y in y0..<(y0 + box) { (yp + y * yRow + x0).update(repeating: 235, count: box) }
        return pb
    }
}
