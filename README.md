# TylerCast

A macOS menu bar app that mirrors the Mac's main display, with its sound, to a
DLNA TV (built for and tried on one older smart TV). It began as Phase 2
of a Python script, `mirror.py` (ffmpeg + BlackHole), called "Phase 1" below,
and needs neither ffmpeg nor BlackHole. It uses Apple frameworks only and has no
third-party dependencies.

| Piece | How |
|---|---|
| Discovery | SSDP M-SEARCH for `MediaRenderer:1`, device description, AVTransport control URL |
| Capture | ScreenCaptureKit, main display. SCK delivers 1920x1080 NV12 with the picture already scaled (on the GPU), centred, side-barred and inset by the margin |
| Encode | VideoToolbox H.264 Main 4.1, 1920x1080, a steady 25fps (the last picture is repeated when the screen is still), keyframe every 2s, no B-frames, real time, 6 Mbit/s with bursts capped per 0.5s |
| Audio | The Mac's sound as **AAC** (plays on the test TV, though it lists only AC-3). `--audio silent-ac3` sends a silent AC-3 track instead |
| Mux | Its own MPEG-TS muxer. 188-byte packets, PAT/PMT/SDT every 100ms and before every keyframe, PCR every 20ms, null-padded to a constant 20 Mbit/s |
| Serve | Network.framework HTTP/1.0 on :8090 (the TV-facing interface only), a random `/<uuid>.ts` path per session, HEAD and GET. Only the TV's IP may connect. Every connection joins the one running stream |
| Control | SOAP Stop → SetAVTransportURI (with DIDL-Lite) → Play. Stop again on quit. The TV's state is polled every 5s |

## Build and test

```bash
cd tylercast
swift build                # debug build: .build/debug/TylerCast
swift test -Xswiftc -plugin-path -Xswiftc /Library/Developer/CommandLineTools/usr/lib/swift/host/plugins/testing
                           # 45 tests: TS structure, PAT/PMT (byte-exact vs ffmpeg), PCR, SOAP, DIDL, HTTP, AAC (decoded with ffmpeg if installed)
                           # (the plugin path is needed with Command Line Tools 27, which don't find
                           # swift-testing's macros by themselves)
./make-app.sh              # release build in an app bundle: build/TylerCast.app
```

Only the Command Line Tools are needed, not Xcode, but use a current version
(Command Line Tools for Xcode 26 or later, from Software Update). macOS picks
an app's look from the SDK it was built with: built with the macOS 15 SDK,
the panel gets the old flat grey window; built with the 26+ SDK it gets the
same Liquid Glass panel as Control Center's Wi-Fi and Sound. There is no
setting that turns the new look on for an older build. SwiftPM writes the
deployment target (14.0) into the binary as its SDK version, so `make-app.sh`
rewrites it to the real SDK version with `vtool` before signing. The panel
avoids `@State`: in the 26+ SDK it is a macro whose plugin ships only with Xcode. (`TylerCast
--dump-windows` logs the open panel's window and views, to check this.)

Tests use swift-testing,
because XCTest ships only with Xcode. There's no Xcode project: `make-app.sh`
copies the binary into a bundle with an `Info.plist` (menu bar only, no Dock
icon) and signs it ad hoc. The bundle exists so macOS can attach the Screen
Recording permission to the app itself rather than to Terminal.

## Run

### Debug run from Terminal

From Terminal, the permission belongs to Terminal, which already has it from
Phase 1. Every SSDP, SOAP and HTTP exchange is logged.

```bash
.build/debug/TylerCast discover                        # TVs, their services, the sink list
.build/debug/TylerCast record /tmp/t.ts --seconds 10   # no TV: capture → file; check with ffprobe
.build/debug/TylerCast serve --test-pattern            # no TV: open the URL it prints in VLC/ffplay
.build/debug/TylerCast play --test-pattern -v          # colour bars on the TV: tests the DLNA path alone
.build/debug/TylerCast play --margin 4                 # the screen on the TV; Ctrl-C sends Stop
```

`play --avt http://192.0.2.175:55000/dmr/control_2` skips SSDP.
`TylerCast help` lists every option. The levers from Phase 1 are still here:
`--mime`, `--no-pn`, `--tlag`, `--no-metadata`, `--bitrate`, `--muxrate`,
plus `--delay` and `--no-audio`.

### Menu bar app

Run `./make-app.sh`, then install it:

```bash
ditto build/TylerCast.app /Applications/TylerCast.app && open /Applications/TylerCast.app
```

Click the TylerCast icon (a screen with a T) for its panel, laid out like
the Wi-Fi panel (plain rows, dividers between sections):

1. **Warnings**, only while a permission is missing, each with a button
   that fixes it.
2. **TVs**: one row per TV found on the network this Mac is on now. Click a
   TV to mirror to it (its icon turns blue, with the time on the right);
   click it again to stop. A problem shows as a short orange line under the
   TV's name (the log has the detail) and clears once the panel closes.
   With no TVs found, a placeholder row says "Looking for TVs…" or "No TVs on
   this network".
   TylerCast searches (SSDP) at launch and each time the panel opens, at
   most every 30s; a TV appears as soon as it answers (~0.1s). TVs from
   another network are never listed, since they couldn't fetch the stream.
3. **Margin**: − and + in 1% steps, applied live on the TV.
4. **Sound on TV** (the Mac's sound; off sends a silent track) and **Mute
   Mac** (sound from the TV only; greyed out when sound is off). Both apply
   when mirroring starts, so they're locked while it runs, and are remembered.
5. **Open at Login**.
6. **Show Log…** and **Quit TylerCast** (⌘Q). Quitting while mirroring sends
   the TV a Stop first. Hold Option and Show Log… becomes **Send Test
   Pattern Instead of Screen**, as Option reveals extra items in the Wi-Fi panel.

While mirroring, the display can't idle-sleep (TylerCast holds a
`PreventUserIdleDisplaySleep` power assertion, released when it stops;
`pmset -g assertions` lists it). If the Mac or its display sleeps anyway
(lid closed, Apple menu → Sleep), mirroring stops cleanly: the TV gets
Stop, the panel goes back to Off, and nothing restarts on wake.

The icon itself shows the state: an outline when off, dimmed while
connecting, filled while mirroring, and a "!" after an error. The log
is `~/Library/Logs/TylerCast.log`.

## Permissions

**Screen Recording**, for the app bundle:

1. Start mirroring once. macOS asks for permission; click **Open System Settings**.
   If it doesn't ask, go to **System Settings → Privacy & Security → Screen &
   System Audio Recording** yourself.
2. Turn on **TylerCast**. If it isn't listed, click **+**, pick
   `TylerCast.app` (in `build/`, or `/Applications` if you copied it)
   and turn it on.
3. Quit TylerCast from its menu and open it again. macOS applies the grant
   only after a restart.
4. If Settings shows TylerCast switched on but it still says permission is
   missing, the grant belongs to an earlier build. This happens after every
   `./make-app.sh` with ad hoc signing. Clear it, reopen TylerCast and
   allow it again:

   ```bash
   tccutil reset ScreenCapture local.tylercast.TylerCast
   ```

   To make grants survive rebuilds, create a signing certificate once.
   `make-app.sh` uses it automatically.
   **Keychain Access → Certificate Assistant → Create a Certificate…**, then
   name it `TylerCast Signing`, Identity Type **Self Signed Root**,
   Certificate Type **Code Signing**, and click **Create**. Rebuild, then
   reset and allow once more; after that, rebuilds keep the permission.

When run from Terminal, Terminal needs the same permission. It already has it.

**Local Network**: macOS asks the first time TylerCast searches for the TV.
Click **Allow**, or no TV will be found. To change it later, go to
**System Settings → Privacy & Security → Local Network**.

The TV side is unchanged from Phase 1: DLNA Remote and TV Remote on, an
ordinary input showing, not standby.

## Audio

**Now:** the Mac's sound, AAC (`--audio aac`, the default). Tried on the
test TV on 2026-11-15: it played speech from the Mac, though its sink list
offers H.264 TS only with AC-3. The rest of this section is how we got there,
and what to do if another TV refuses AAC.

While mirroring, the Mac's speakers go quiet and the sound comes from the TV
only. A Core Audio process tap (`SystemAudioTap`, macOS 14.2+) captures
everything the Mac plays except TylerCast, with `muteBehavior =
.mutedWhenTapped`, read through a private aggregate device, as in
[AudioCap](https://github.com/insidegui/AudioCap). Stopping (or quitting, or
a crash, since the tap belongs to the process) gives the speakers back.
macOS asks once for **System Audio Recording** (`NSAudioCaptureUsageDescription`);
without it the tap delivers silence, which the log points out after 10s.
The tap sends nothing while the Mac is quiet, so the encoder pads silence
(0.1s behind real time) to keep the TV's audio track continuous.
`--mac-sound` keeps the Mac audible and uses ScreenCaptureKit's copy
instead (captured after the Mac's volume, so a muted Mac sends silence).
The test pattern captures no sound and keeps the silent AC-3 track.

### Background: why AC-3 was the first plan

AudioToolbox can't encode AC-3. `AudioConverterNew` to `kAudioFormatAC3` or
`kAudioFormatEnhancedAC3` returns `'fmt?'`. The system's encoders are
`aac`, the AAC variants, `alac`, `flac`, `opus`, `ilbc`, `ima4`, `lpcm`,
`alaw` and `ulaw` (checked on macOS 27.2). Meanwhile the TV's sink list offers
H.264 in plain 188-byte TS only with AC-3.

The stream carries a **silent AC-3 track**: one pre-encoded 32ms AC-3 frame
of silence, repeated. Every AC-3 frame is self-contained, so this is a valid
stream, and it matches `AVC_TS_HD_50_AC3_ISO` exactly as Phase 1 did. The
Mac keeps its own sound. `--no-audio` sends video only, as an experiment:
the TV may refuse a stream that lacks the AC-3 its profile promises.

To get real sound, ScreenCaptureKit already provides system audio
(`capturesAudio`, 48 kHz, with no BlackHole and the Mac still audible). The
hook is in `ScreenCapture.start`. What's missing is an encoder the TV accepts:

1. **Write an AC-3 encoder in Swift** (recommended). The TV wants plain
   stereo 48 kHz AC-3. A minimal encoder is roughly 1000 lines:
   512-point MDCT, exponent coding, the standard's parametric bit allocation,
   and mantissa quantisation. It's self-contained, has no dependencies, and
   ffmpeg can decode its output to check it. It slots in where
   `SilentAudioSource` is now.
2. **Bundle an AC-3 encoder** such as libavcodec's `ac3` (LGPL) or the old
   aften. It's proven, but it brings a C dependency and licence obligations
   into a package that otherwise has none.
3. **AAC, which macOS encodes natively** (built: `--audio aac`). The
   Mac's sound comes from ScreenCaptureKit (48 kHz stereo, TylerCast's own
   sound left out; it's captured after the Mac's volume, so a muted Mac
   sends silence). `AACEncoder` makes AAC-LC 160k in ADTS, muxed as stream
   type 0x0F, PES 0xC0, as ffmpeg does, with `DLNA.ORG_PN=AVC_TS_MP_HD_AAC_MULT5_ISO`.
   Tests decode it with ffmpeg and check a 1 kHz tone comes back at the
   right pitch and level. **Works on the test TV.** The TV lists `AVC_TS_JP_AAC_T`
   and `AVC_TS_MP_HD_AAC_LTP_T`, both `video/vnd.dlna.mpeg-tts`, meaning
   192-byte packets, each with a 4-byte timestamp. The muxer would need a
   192-byte mode, and those profiles (JP = Japanese broadcast; AAC-LTP)
   don't strictly fit what we'd send. Phase 1's `--audio aac` with an
   `_ISO` profile wasn't in the sink list. It's an experiment, not a sure
   thing.

## Timing, and the "dts < pcr" problem

ffmpeg's muxer set PCR and DTS independently and reported keyframes as late.
Here the stream clock *is* the byte position: packet *n* carries
PCR = *n*·1504/muxrate, and the pacer sends it at exactly that wall-clock
moment. A frame captured at time *t* gets DTS = *t* + `--delay` (0.15s by
default). Its packets jump ahead of the null padding as soon as it's
encoded, so a frame is late only if encoding plus sending takes longer than
the delay. The muxer measures this for every frame and logs it every 10s:

```
TS 10s: 248 video frames (5 key), 313 audio, late 0, min slack 209ms (keyframes 209ms), max queue 1153 pkts, padding 79%
```

On busy screen content keyframes reach about 300 KB: 0.25s at 10 Mbit/s,
which made some late at a 0.2s delay, but 0.12s at 20 Mbit/s. If `late` is
ever non-zero, the line says so: raise `--delay` (costs latency), raise
`--muxrate`, or lower `--bitrate`.

## Delay on the TV (measured 2026-11-15)

Measured by photographing a millisecond clock page on the Mac and on the TV
together:

| Run | TV behind Mac |
|---|---|
| TylerCast, 10 Mbit/s, delay 0.2s, joining mid-GOP | 1.19s |
| TylerCast, 10 Mbit/s, delay 0.2s, each connection starts on a keyframe | 1.10s |
| `mirror.py play --margin 4` (Phase 1, same day, same page) | 1.03s |
| TylerCast, 20 Mbit/s, delay 0.2s | 0.95s |
| TylerCast, 20 Mbit/s, delay 0.1s, `--tlag` | not photographed; at best ~0.85s |

So Phase 1 and TylerCast are level. The 0.7s recorded earlier for Phase 1
wasn't reproduced on this page. Of the ~1s, TylerCast controls only
`--delay` plus about 50-100ms of capture and encoding. The rest is the TV's
own playback buffer, which grows only a little with the byte rate
(about 0.1s less at 20 Mbit/s). It's most noticeable in the TV's pointer,
which trails your hand. The Mac isn't slowed: TylerCast uses about 8% of a
CPU core even as a debug build.

Not tried yet: `--tlag` on its own, with a photo; 720p50 (the 50Hz profile
also covers 1280x720 at 50fps, which would make the pointer move more
smoothly but probably wouldn't shorten the TV's buffer).

## Tried on the TV

The test pattern and the screen both play on the test TV, with the profile
and headers above. The TV accepted the stream on the first try, smooth with
no late frames. The menu bar app's own buttons haven't been clicked through
yet: the TV runs above were made with `TylerCast play`.
