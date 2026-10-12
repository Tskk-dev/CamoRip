# Validation and acceptance

Record the exact commit, Actions run URL, Windows build, iOS version/jailbreak, installation tool, cable, capture format, and meeting app/version for each physical test. A CI build does not satisfy a device test.

## Automated checks

- Windows native H.264 decoder and Media Foundation source compile for x64.
- Managed companion builds/publishes and the installer is produced.
- Protocol tests cover fragmented/coalesced packets, length bounds, invalid control messages, resets, and USB service request/reply framing.
- A synthetic local Apple USB service tests USB filtering, Connect port byte order, handshake, raw tunnel, cancellation, and reconnect behavior without a phone.
- A known valid H.264 keyframe must produce a real decoded frame; a missing native DLL or null output must fail this check.
- macOS Xcode build compiles arm64 with iOS 15 deployment target and packages a structurally valid unsigned IPA.

Use the Actions result to establish which of these passed for a particular commit. GitHub-hosted macOS does not test this iPhone. Windows runners also do not establish that the virtual camera activates on the target Windows 11 PC.

## Physical acceptance checklist — pending until performed

- [ ] Install/sign the IPA on this iPhone using its actual jailbreak route; record the method. Exercise free Apple ID fallback if needed.
- [ ] Grant camera permission; separately deny it and verify a useful error and recovery.
- [ ] Connect and trust with a data cable; establish USB video with Wi-Fi disabled.
- [ ] Start/stop on the phone and PC. Confirm stopped video becomes neutral in camera clients and the phone can sleep after stopping.
- [ ] Switch front/rear repeatedly. Confirm actual reported capture dimensions, keyframe recovery, and stable output.
- [ ] Run the rear camera at native 1920×1080 at 60 fps for at least 15 minutes. Record output rate, latency, phone temperature/thermal changes, and any stalls.
- [ ] While streaming, unplug/reconnect ten times. Verify the old socket/capture/decoder state is discarded and the new session recovers without restarting apps.
- [ ] Open Windows camera enumeration and a preview app. Verify **CamoRip** appears and produces frames.
- [ ] Select the camera in at least one intended meeting app, e.g. Zoom or Teams; record the actual app and whether its preview/call is stable.
- [ ] Check missing Apple driver/service, untrusted/unpaired or locked phone, unavailable phone app endpoint, unsupported IPA installer, denied camera permission, and interrupted USB. Record useful messages rather than assuming the failure cause.
- [ ] Close both companions and uninstall Windows. Confirm no stale active camera session and no retained temporary captured frames.
- [ ] Confirm session storage ACLs allow the current user and required Windows camera service, and exclude unrelated local users.
- [ ] Observe network behavior on both devices. Confirm camera traffic uses the cable and loopback endpoints; no app audio stream, Wi-Fi camera endpoint, or external video server is active.

If a physical check fails, keep the release status incomplete and report the specific evidence. Do not infer successful 1080p60 or client compatibility from compilation.

## Reported performance failure: 2026-10-11

The user's Windows log from 21:02:02 through 21:04:08 (Windows build 26300,
capture configured at 1920x1080/60) shows USB receipt near 60 fps with decoded
and published rates usually around 6 fps, sometimes zero. Compressed-queue
overflows recur throughout the run. This establishes loss in the Windows
pipeline; the original metrics cannot separate decoding from its synchronous
camera/preview callback. Sustained 1080p60 acceptance remains failed/pending.

The follow-up Windows change separates those consumers, bounds compressed
backlog by size and age, waits for one keyframe after an overflow, and adds
per-stage timing and freshness diagnostics. Reproduce on the phone/PC and
compare `latest.log` after installing the new Windows artifact. No successful
physical retest has been recorded for this change.

A local synthetic 1920x1080 H.264 stream also reproduced a 1920x1088 padded
decoder output. That incorrectly selected the managed scalar-resize path on
every frame. The native decoder now packs only the valid display aperture;
regression fixtures cover 1920x1080 and 50x50 cropped images. This synthetic
finding identifies an unnecessary conversion, but does not measure the phone's
actual bitstream or establish sustained 60 fps.

## Pipeline v4 allocation and scheduling changes (2026-10-12)

The dedicated decoder now consumes compressed frames synchronously with a monitor
wakeup instead of blocking on an async iterator whose continuation uses the thread
pool. Camera publishing uses a dedicated synchronous latest-frame mailbox. Both
workers register with Windows MMCSS Capture and log whether registration succeeds:
https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service

The app enables bounded exact-size NV12 buffer reuse. Camera and preview consumers
retain separate references, release them after copying, and release superseded or
shutdown frames. The default decoder API still allocates independent frames for
callers that retain event arguments without explicit ownership. Preview output and
front-camera resize storage are also reused. There is no added prebuffer delay.
Logs now include managed allocation MiB/s and generation 0/1/2 collection counts.

A local repeated-IDR microbenchmark on AMD Radeon RX Vega 10, using the committed
Hardware1080p.h264 fixture, warmed up for 20 feeds and measured 180 more feeds in
each mode. Both modes reported D3D11 hardware confirmed and produced 180 frames.
With the .NET 10 runtime available on this PC, allocating mode measured 534.23 MiB
of managed allocation on the decode thread and 58 generation-2 collections; pooled
mode measured 0.07 MiB and zero generation-2 collections. Mean feed time was
7.27 ms versus 6.44 ms in that run. This isolates allocation cost: repeated IDRs,
sequential runs, and absence of USB/camera-client load make it unsuitable as proof
of sustained phone fps or a controlled end-to-end latency comparison.

Retest the new Windows installer with the existing iPhone app, then compare live
decode/publish rates, queue drops/recoveries, stage maximum times, allocations,
and GC counts. Physical sustained 1080p60 and meeting-client smoothness remain
pending for v4.

## CamoRip v0.2 desktop acceptance — pending physical review

### 0.2.1 startup and USB regression (2026-10-12)

The installed 0.2.0 build failed in `MainWindow.InitializeComponent` with a
`XamlParseException` before starting USB. Its publish/installer directory lacked
`CamoRip.pri`, `App.xbf` and `Views/MainWindow.xbf`, although normal build output
contained them. Reproducing the workflow's self-contained publish locally showed
the same omission. The project now explicitly publishes its generated PRI/XBF
files. The Windows build also launches the published executable with
`--startup-check` and requires a successful exit and resource-load marker before
building the installer. This check isolates settings, starts no USB connection,
and does not inspect visual design.

On this PC, the corrected self-contained publish passed the startup check with
exit code 0. Windows reported the Apple iPhone USB devices present and the Apple
usbmux service returned one USB device. Launching the corrected executable in
normal mode connected to the actual phone, negotiated 1920x1080 at 60 fps and
started video. Initial log samples confirmed D3D11 hardware decode on AMD Radeon
RX Vega 10, 1920x1080 output, approximately 60 fps publication and no queue drops.
Later rates varied; these initial samples do not establish sustained 60 fps or
meeting-app behavior. Camera adjustments, torch lifecycle, tray continuity and
upgrade registration still require their respective checks below.

The previous visible window was an isolated `--prototype` instance, which does
not start USB. Prototype mode now shows an explicit USB-disabled notice, and
startup logs identify the executable, version and mode. The user waived further
UI validation; visual/accessibility checks below are no longer acceptance gates.

- [ ] Upgrade from the existing installation, close the old companion and verify one CamoRip camera without a duplicate old device.
- [ ] Change exposure compensation, manual ISO/shutter, focus and white balance from Windows; verify acknowledged actual values and the resulting video in a meeting app.
- [ ] Switch cameras while dragging a setting; stale commands must not apply to the newly selected camera.
- [ ] Exercise torch toggle/brightness, unavailable front camera and thermal availability. Verify off on stop, cable pull, switch and application exit/background.
- [ ] Select 60/30/15/off preview, fit/fill, mirror, F11/Escape and different window sizes/DPIs. Mirroring must affect only the desktop preview.
- [ ] Minimize, close-to-tray and reopen the existing instance; webcam output must continue. Verify all tray actions and explicit Exit.
- [ ] Toggle logging off, note the log file size, stream for 10 seconds and verify no new writes. Restart and confirm the preference; retain access to old logs.
- [ ] Verify start minimized and optional Windows sign-in launch, keyboard navigation, screen reader labels, text scaling, high contrast and system animation/transparency settings.
- [ ] Compare sustained 1080p60 queue drops, decode/publish/preview timings and CPU/GPU load against pipeline v4. Synthetic GPU tests are not an end-to-end phone benchmark.

A local hardware-only preview smoke run (renderer from CI run 32) initialized the
D3D11 device successfully and submitted 180 synthetic 1080p NV12 images with zero
negative render results, averaging 1.82 ms per upload/draw/present call. This used
an unattached composition swap chain and does not establish actual WinUI display
rate or meeting-app performance. Native shader tests use WARP solely on hosted CI;
the shipped renderer requests hardware and reports failure rather than silently
calling software rendering hardware acceleration.

## 0.3.0 resolution selector and Windows torch investigation

- Local managed tests: 107 passed, 0 failed (including 4K catalog, wire round trip,
  saved capture preference, invalid control ranges, live reconfiguration restart,
  ignoring a superseded configuration acknowledgement, and preview cadence on 60/75/120 Hz clocks).
- Local WinUI self-contained publish succeeded. CI checks all five native source
  modes, selected mode byte lengths/timing, and channel cache resizing.
- Windows Event Log contains two 0.2.1 crashes in ucrtbase.dll with 0xc0000409,
  fast-fail parameter 5. These records lack a call stack; root cause is unconfirmed.
  Torch Header stays stable, active slider drags are protected against incoming
  range/value changes, ranges/values are validated and clamped, and control
  send/ack details now respect the existing optional diagnostic logging setting.
- A functional startup check exercises torch values/acknowledgements and mode
  bindings without USB. This is not evidence that the reported native crash is
  fixed during physical dragging/streaming. A local ProcDump session is capturing
  the installed application's next native failure for diagnosis.
- New IPA installation, sustained 4K30 USB, physical mode changes, torch crash
  reproduction after changes and meeting-app format reopening remain unverified.
- User waived visual UI validation; no visual inspection is required.

Native checks at revision cd9eb828c8d4b30d37b73b2691e5fd8279ba3d34 passed on
GitHub run 41: shader checks, all five source modes (provider smoke), and frame
channel mode/cache checks. The iOS arm64 build, unsigned packaging and 11
simulator tests passed. Its Windows job was superseded during managed build by
the preview cadence commit; it is not a complete installer result.


## iOS 0.3.1 live capture mode reconfiguration

- The user's 0.3.0 session at 09:31–09:35 on 2026-10-12 repeatedly lost
  USB while switching a running stream; switching after stopping succeeded.
  The user reports that the iPhone app closes during live mode changes.
  No iOS crash report is available, so the exact exception is unconfirmed.
- Reconfiguration now gates incoming capture samples atomically, drains and
  invalidates the old encoder before changing camera format, stops AVCaptureSession
  outside its configuration transaction, resets previous frame duration locks,
  and acknowledges the mode only after the new compression session is ready.
  Camera switching uses the same encoder barrier. Late buffers with old dimensions
  are discarded instead of recreating an encoder for the previous format.
- iOS version is 0.3.1, build 5. The Windows 0.3.0 protocol remains compatible.
- Physical acceptance remains pending: repeatedly switch 4K30 / 1080p60 /
  720p60 during streaming on the 6s Plus; switch front/rear; stop while a mode
  is changing; unplug/reconnect; check no phone closure, stale acknowledgement,
  wrong output dimensions or persistent frozen feed. Collect the iOS Analytics
  crash report if it closes again. These code changes do not establish that
  the reported crash is fixed on the device.
- The Windows torch crash also recurred at 09:35:02 on 0.3.0, in ucrtbase.dll
  (0xc0000409, parameter 5). It remains unresolved and is separate from this fix.

## 0.3.2 quality controls and latency investigation

- Windows adds Efficient/Balanced/High/Custom quality with an explicit Apply
  button and a 1–50 Mbps custom target. Save only after acknowledgement.
  Balanced preserves current defaults; High doubles them (1080p60: 24 Mbps;
  4K30: 40 Mbps). bitrate_control_v1 gates the controls; older phones reconnect
  with balanced defaults rather than unsupported high targets.
- The phone validates 0.5–50 Mbps, checks the bitrate property setter, requests
  an optional one-frame compressor delay bound with fallback, and logs the bound
  and queried hardware-encoder status. The target is an average bitrate;
  Windows separately logs actual compressed payload Mbps.
- LATENCY logs match presentation timestamps to complete-packet arrival time,
  recording arrival-to-decode, arrival-to-camera-publication and arrival-to-preview
  submission. Tracking storage is bounded. Matched ping/pong uses a local monotonic
  clock for control RTT. These measurements exclude capture/phone encoding,
  delivery before packet arrival, downstream app buffering and display scanout.
  Control RTT and the existing stage activity ages are not full video latency.
- The available Windows 0.3.0 log (2026-10-12 09:51–09:53) shows approximately
  60 fps at 720p60, hardware decoding, final decode averages about 1.7–2.2 ms,
  no recorded queue drops/publication failures and preview around 51–58 fps.
  No sustained PC decoder backlog is recorded. The user clarified a perceived
  delay of a few milliseconds; exact camera-to-screen latency is unmeasured.
- Local targeted checks: 32 passed. Self-contained WinUI publish succeeded.
  CI compiles native components/iOS and runs the complete suites. Physical
  quality, bitrate, thermals, lower encoder delay and cable performance remain
  unverified. Install both 0.3.2 companions, compare presets, inspect LATENCY
  logs and phone encoder status, and film a source timer/LED alongside the
  displayed feed to measure full delay. Keep mode/exposure/app identical for
  any cable/port comparison. No visual UI validation is required by the user.


## 0.3.3 iPhone 16 build and monochrome identity

- A separate iOS 18 minimum arm64 IPA uses exactly the same source, H.264 modes,
  USB transport, remote camera controls and app identity as the iOS 15 package.
  Both build profiles remain in CI; the modern profile selects an iPhone 16
  simulator and the legacy profile selects an available compatible iPhone.
- The shared icon is a black circle on white, generated from
  `assets/branding/camorip.svg` by `scripts/generate-icons.py` (Pillow).
  iOS uses an opaque 1024-pixel icon; Windows embeds 16-256-pixel ICO entries
  for its executable, window, tray and installer shortcuts.
- Physical iPhone 16 installation/signing, USB-C pairing/reconnect, front/rear
  mode negotiation, sustained capture, adjustments, torch and thermals remain
  pending. Simulator checks do not exercise AVFoundation camera hardware or USB.
  Existing unresolved Windows torch-crash reports remain relevant.

## iOS 0.3.4 camera-switch mirroring exception

- A tester reports that the iOS app closes on camera switch on iOS 26. No
  iOS crash report has been provided; the actual exception/stack is unconfirmed.
- Inspection found that the replacement output connection was assigned
  `isVideoMirrored` while `automaticallyAdjustsVideoMirroring` retained its
  default true value. Apple's documentation states that manual assignment in
  that state raises an exception. Swift do/catch does not catch Objective-C
  exceptions. Disable automatic adjustment first on each new connection,
  retaining the mirroring-support guard and unmirrored webcam output.
- Three regression tests exercise setter ordering, new connections after
  repeated switches, and unsupported connections, using a test double. They
  do not exercise AVFoundation hardware. Camera-switch phase logs identify
  input replacement, format commit and connection configuration.
- Build both the iOS 18+ and iOS 15+ packages as version 0.3.4, build 8.
  The existing Windows 0.3.3 companion remains compatible and unchanged.
- Physical validation remains pending: switch front/rear repeatedly on iOS 26
  while stopped and streaming, from phone and PC, with preview on/off and
  after selecting 4K30. Also repeat on iPhone 6s Plus. If closure persists,
  obtain the CamoRip/UniversalCam .ips report from iPhone Settings > Privacy
  & Security > Analytics & Improvements > Analytics Data, and the phone's
  Documents/UniversalCam.log. A successful build is not proof of a crash fix.
- API reference:
  https://developer.apple.com/documentation/avkit/choosing-a-camera-by-the-direction-it-faces?language=objc
