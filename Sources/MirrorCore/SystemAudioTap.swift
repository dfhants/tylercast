import AudioToolbox
import CoreAudio
import Foundation

/// Captures everything the Mac plays (except TylerCast itself) with a Core
/// Audio process tap, and mutes it on the Mac's speakers while it runs, so
/// the sound comes out of the TV only. Needs macOS 14.2+ and the "System
/// Audio Recording" permission (Info.plist NSAudioCaptureUsageDescription).
///
/// Shape after AudioCap (github.com/insidegui/AudioCap): tap → private
/// aggregate device (clocked by the output device) → IOProc.
@available(macOS 14.2, *)
public final class SystemAudioTap {
    /// Interleaved stereo Float32 at `sampleRate`, and the host time of the first sample.
    public var onAudio: (([Float], Double) -> Void)?
    public private(set) var sampleRate: Double = 48_000
    public private(set) var buffersIn = 0
    /// Buffers with any non-zero sample: all-zero for long means no permission
    /// (a tap without it delivers silence rather than failing).
    public private(set) var audibleBuffersIn = 0

    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggregateID = AudioObjectID(kAudioObjectUnknown)
    private var procID: AudioDeviceIOProcID?
    private var interleavedIn = false
    private var channelsIn = 2
    private let queue = DispatchQueue(label: "system-audio-tap", qos: .userInteractive)

    public init() {}

    deinit { stop() }

    public func start() throws {
        let desc = CATapDescription(stereoGlobalTapButExcludeProcesses: Self.ownProcessObject().map { [$0] } ?? [])
        desc.uuid = UUID()
        desc.name = "TylerCast"
        desc.isPrivate = true
        desc.muteBehavior = .mutedWhenTapped  // the Mac goes quiet; the TV plays it
        var tap = AudioObjectID(kAudioObjectUnknown)
        var err = AudioHardwareCreateProcessTap(desc, &tap)
        guard err == noErr else { throw CaptureError(description: "could not tap the Mac's sound (\(err))") }
        tapID = tap

        var asbd = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        var addr = AudioObjectPropertyAddress(mSelector: kAudioTapPropertyFormat, mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        err = AudioObjectGetPropertyData(tap, &addr, 0, nil, &size, &asbd)
        guard err == noErr, asbd.mFormatID == kAudioFormatLinearPCM, asbd.mFormatFlags & kAudioFormatFlagIsFloat != 0 else {
            stop()
            throw CaptureError(description: "unexpected tap format (\(err))")
        }
        sampleRate = asbd.mSampleRate
        channelsIn = Int(asbd.mChannelsPerFrame)
        interleavedIn = asbd.mFormatFlags & kAudioFormatFlagIsNonInterleaved == 0

        guard let outputUID = Self.defaultOutputUID() else { stop(); throw CaptureError(description: "no output device") }
        let agg: [String: Any] = [
            kAudioAggregateDeviceNameKey: "TylerCast Tap",
            kAudioAggregateDeviceUIDKey: "local.tylercast.tap.\(UUID().uuidString)",
            kAudioAggregateDeviceMainSubDeviceKey: outputUID,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: outputUID]],
            kAudioAggregateDeviceTapListKey: [[kAudioSubTapDriftCompensationKey: true,
                                               kAudioSubTapUIDKey: desc.uuid.uuidString]],
        ]
        var aggID = AudioObjectID(kAudioObjectUnknown)
        err = AudioHardwareCreateAggregateDevice(agg as CFDictionary, &aggID)
        guard err == noErr else { stop(); throw CaptureError(description: "could not make the tap device (\(err))") }
        aggregateID = aggID

        var proc: AudioDeviceIOProcID?
        err = AudioDeviceCreateIOProcIDWithBlock(&proc, aggID, queue) { [weak self] _, inData, inTime, _, _ in
            self?.receive(inData, time: inTime)
        }
        guard err == noErr, let proc else { stop(); throw CaptureError(description: "could not read the tap (\(err))") }
        procID = proc
        err = AudioDeviceStart(aggID, proc)
        guard err == noErr else { stop(); throw CaptureError(description: "could not start the tap (\(err))") }
        Log.info("Audio tap: the Mac's sound, \(Int(sampleRate)) Hz \(channelsIn)ch "
            + "\(interleavedIn ? "interleaved" : "planar"), muted on \(outputUID) while mirroring")
    }

    public func stop() {
        if aggregateID != kAudioObjectUnknown, let procID {
            AudioDeviceStop(aggregateID, procID)
            AudioDeviceDestroyIOProcID(aggregateID, procID)
        }
        procID = nil
        if aggregateID != kAudioObjectUnknown { AudioHardwareDestroyAggregateDevice(aggregateID) }
        aggregateID = AudioObjectID(kAudioObjectUnknown)
        if tapID != kAudioObjectUnknown {
            AudioHardwareDestroyProcessTap(tapID)
            Log.info("Audio tap: stopped; the Mac's speakers play again")
        }
        tapID = AudioObjectID(kAudioObjectUnknown)
    }

    private func receive(_ data: UnsafePointer<AudioBufferList>, time: UnsafePointer<AudioTimeStamp>) {
        let buffers = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: data))
        guard let first = buffers.first, let base = first.mData else { return }
        let frames: Int
        if interleavedIn {
            frames = Int(first.mDataByteSize) / (MemoryLayout<Float>.size * max(channelsIn, 1))
        } else {
            frames = Int(first.mDataByteSize) / MemoryLayout<Float>.size
        }
        guard frames > 0 else { return }
        var out = [Float](repeating: 0, count: frames * 2)
        var audible = false
        for f in 0..<frames {
            for ch in 0..<2 {
                let c = min(ch, channelsIn - 1)
                let v: Float
                if interleavedIn {
                    v = base.assumingMemoryBound(to: Float.self)[f * channelsIn + c]
                } else {
                    let b = c < buffers.count ? buffers[c].mData : nil
                    v = b?.assumingMemoryBound(to: Float.self)[f] ?? 0
                }
                out[f * 2 + ch] = v
                if v != 0 { audible = true }
            }
        }
        buffersIn += 1
        if audible { audibleBuffersIn += 1 }
        if buffersIn == 1 { Log.info("Audio tap: first buffer (\(frames) frames)") }
        if audibleBuffersIn == 0, frames > 0, buffersIn * frames == Int(sampleRate) * 10 {
            Log.warn("Audio tap: 10s of pure silence. Either nothing is playing, or TylerCast isn't allowed "
                + "System Audio Recording (System Settings → Privacy & Security → Screen & System Audio Recording)")
        }
        // Host time of the first sample, on CLOCK_UPTIME_RAW like Pacer.hostSeconds().
        let host = time.pointee.mFlags.contains(.hostTimeValid)
            ? Double(AudioConvertHostTimeToNanos(time.pointee.mHostTime)) / 1e9
            : Pacer.hostSeconds()
        onAudio?(out, host)
    }

    // MARK: Core Audio lookups

    private static func ownProcessObject() -> AudioObjectID? {
        var pid = getpid()
        var obj = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyTranslatePIDToProcessObject,
                                              mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        let err = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr,
                                             UInt32(MemoryLayout<pid_t>.size), &pid, &size, &obj)
        return err == noErr && obj != kAudioObjectUnknown ? obj : nil
    }

    private static func defaultOutputUID() -> String? {
        var dev = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                                              mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &dev) == noErr
        else { return nil }
        var uid: Unmanaged<CFString>?
        size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        addr.mSelector = kAudioDevicePropertyDeviceUID
        guard AudioObjectGetPropertyData(dev, &addr, 0, nil, &size, &uid) == noErr, let uid else { return nil }
        return uid.takeRetainedValue() as String
    }
}
