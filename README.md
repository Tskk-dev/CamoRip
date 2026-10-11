# USB iPhone Camera

Use an iPhone 6s Plus as a Windows 11 webcam through a Lightning USB cable. The iPhone app captures camera video with AVFoundation and encodes H.264 with VideoToolbox. The Windows companion reads it through Apple's local USB device service, decodes it with Windows Media Foundation, and exposes **USB iPhone Camera** to camera apps using the Windows 11 virtual camera API.

Video stays on the iPhone, USB cable, and Windows PC. The phone listens on loopback only; the Windows client selects USB-connected devices. No external video server or account is used by this app. The meeting application you choose controls what happens to its webcam input.

## Requirements

- Windows 11 **x64**, build 22000 or newer. The intended PC is build 26300.9457.
- Apple Devices or iTunes installed, with its Apple Mobile Device service running.
- iPhone 6s Plus running iOS 15.x, a data-capable Lightning cable, and camera permission.
- An existing jailbreak-compatible signing/install route, or a free Apple ID signing tool. The CI IPA is unsigned and is not automatically installable on every jailbreak.

The main target is rear camera 1920×1080 at 60 fps. The app reports the actual capture format and frame rate; the front camera can use a lower resolution and frame rate. Windows produces a fixed 1080p60 webcam output and repeats frames when the selected iPhone camera runs slower.

## Install and use

1. Open this repository's **Actions → Build webcam apps**, select a successful run, and download `usb-iphone-camera-windows-x64` and `usb-iphone-camera-ios-unsigned`.
2. Run the Windows setup executable. Administrator access registers the Media Foundation camera source; run the installed companion normally afterward.
3. Sign/install the unsigned IPA with your confirmed iPhone install route. Keep the app open and allow Camera access.
4. Connect the cable, unlock the iPhone, and accept **Trust This Computer**. Open both companions.
5. Use Start/Stop and Switch camera on either companion. Select **USB iPhone Camera** in your meeting application's camera settings.

See [installation details](docs/INSTALLATION.md), [architecture](docs/ARCHITECTURE.md), [upstream audit](docs/UPSTREAM_AUDIT.md), and the [device validation checklist](docs/VALIDATION.md).

## Slow video diagnostics

Leave streaming for 10 seconds and compare the iPhone stage counts in
`Files > On My iPhone > UniversalCam > UniversalCam.log` with the Windows
`Pipeline fps` line. The Windows log is at
`%LOCALAPPDATA%\UniversalCam\Logs\latest.log`; use **Open diagnostic logs** in
the companion window to open its folder. `captured` versus `encoded` identifies
phone capture/encoder loss, `encoded` versus `usbSent` identifies USB send
backpressure, and Windows `USB` versus `decoded` versus `virtual camera` narrows
down the PC-side stage. Include both logs when reporting a stall.

Windows also logs `stage avg/max(count)` in milliseconds for decoder Feed,
camera-channel writes, and preview conversion. At 60 fps, a stage consistently
taking more than 16.7 ms cannot keep up. Camera and preview have independent
workers with one latest decoded frame each; coalesced preview frames are expected
because the preview runs at about 15 fps. `virtual camera` counts successful
channel writes, not frames consumed by a meeting app.

The compressed queue is limited to 32 frames, 16 MiB and 250 ms of queued age.
An overflow discards the broken H.264 dependency chain once and waits for a fresh
keyframe; `recoveries`, `waitingIDR`, queue age, last-frame ages and CPU/memory
help identify the cause. Metrics run independently of the UI. Restarting the
companion preserves the preceding run as `previous.log`; unhandled managed
exceptions and normal shutdowns are logged too. PC pipeline changes do not
require reinstalling the phone app.

The decoder honors the Media Foundation display aperture: a padded 1920x1088
H.264 surface is copied as its valid 1920x1080 image, including the correct UV
plane offset. This keeps normal 1080p publishing on the NV12 passthrough path
instead of running a full-frame scalar resize.

## Build

Windows needs Visual Studio 2022 C++ tools, a Windows 11 SDK, CMake, .NET SDK 8 or newer, and Inno Setup 6 for installer packaging. From a Developer PowerShell:

```powershell
./scripts/build-windows.ps1
& 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe' installer/USB-iPhone-Camera.iss
```

The macOS job uses XcodeGen and Xcode to build an arm64 app with an iOS 15 deployment target. It packages `Payload/UniversalCam.app` into an unsigned IPA with `CODE_SIGNING_ALLOWED=NO`. A paid Apple Developer membership is not required for these builds. GitHub runners do not test your physical iPhone.

The Windows virtual camera source is a user-mode COM DLL. Its session lifetime is tied to the running Windows companion. Closing the companion removes the camera session; reconnecting USB restores the phone connection while the companion stays open.

## Validation status

Check the Actions run for build/test results for the exact commit you download. Compilation and automated protocol/decoder tests are separate from physical acceptance. Installation on the actual jailbreak, USB loopback forwarding, sustained rear 1080p60, reconnect behavior, and recognition in a chosen meeting app require the checks in `docs/VALIDATION.md` before a working device release can be claimed.

Microphone audio, Wi-Fi streaming, image effects, Windows 10, ARM Windows, and custom DirectShow/kernel drivers are outside v1.

## License

MIT. This project reuses reviewed portions of [UniversalCam](https://github.com/Shivinder-s/universal-cam); preserve its copyright notice in [LICENSE](LICENSE). Dependency and platform details are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
