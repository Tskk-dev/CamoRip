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
