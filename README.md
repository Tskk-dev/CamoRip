# CamoRip — iPhone Webcam for Windows 11

Turn an iPhone into a USB webcam for Windows 11. CamoRip is free and open source, with camera controls on your PC and video sent locally over a USB data cable.

## Features

- Select CamoRip as the webcam in compatible camera and meeting apps.
- Control exposure, focus, white balance and torch from Windows. Adjustments run on the iPhone.
- Choose 720p or 1080p at 30/60 fps, or 4K at 30 fps, where the active camera supports it.
- Set the desktop preview to 60, 30 or 15 fps, or turn it off. Fit, fill, mirror and fullscreen options are included.
- Keep streaming from the Windows system tray and turn diagnostic logs on or off.
- Use the native WinUI 3 desktop app with H.264 video and Media Foundation decoding.

**Wireless support is in development:** same-network Wi-Fi streaming, QR code pairing and automatic reconnect. It is not yet validated on physical devices.

## Requirements

- Windows 11 x64, build 22000 or newer.
- Apple Devices or iTunes, with the Apple Mobile Device service running, for USB connections.
- iPhone 6s Plus with iOS 15+, or iPhone 16 with iOS 18+, and a USB data cable.
- Camera permission and an iPhone signing/install tool. Builds include an unsigned IPA; installation depends on your jailbreak or Apple ID signing method.

Both iPhone builds have the same features. Camera modes depend on the phone and selected camera. Physical 4K30 operation still needs validation, and 4K adds heat and processing load.

## Install and use

1. Open [Actions → Build webcam apps](https://github.com/Tskk-dev/CamoRip/actions/workflows/build.yml) and choose a successful run. Download `camorip-windows-x64` and either `camorip-ios-unsigned` (iOS 15+) or `camorip-iphone16-unsigned` (iOS 18+).
2. Run the Windows installer. Administrator access is needed to register the virtual camera; launch the installed app normally afterward.
3. Sign and install one IPA using your supported install method. Both packages share the same app identity. Open the iPhone app and allow camera access.
4. Connect the USB cable, unlock the iPhone and accept **Trust This Computer**. Open CamoRip on Windows.
5. Start video and select **CamoRip** in your camera or meeting app.

Keep both apps updated for camera controls. Choose a mode under **Camera → Resolution and frame rate**. Changing modes restarts the webcam; if a meeting app keeps the old format, reopen its camera.

The iPhone app must stay open while streaming. A hidden preview or dimmed screen can reduce heat; locking the phone stops camera capture.

## Documentation

- [Installation and troubleshooting](docs/INSTALLATION.md)
- [Desktop features and validation status](docs/CAMORIP.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Device validation checklist](docs/VALIDATION.md)
- [UniversalCam reuse audit](docs/UPSTREAM_AUDIT.md)

## About

Built by [Tskk-dev](https://github.com/Tskk-dev). CamoRip is a free iPhone webcam app for people who want local video and direct camera controls without a subscription.

Support development on [Ko-fi](https://ko-fi.com/tskkkdev).

## License

MIT. CamoRip reuses reviewed portions of [UniversalCam](https://github.com/Shivinder-s/universal-cam). Preserve its copyright notice in [LICENSE](LICENSE). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for dependency and platform details.
