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
- [ ] Open Windows camera enumeration and a preview app. Verify **USB iPhone Camera** appears and produces frames.
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
