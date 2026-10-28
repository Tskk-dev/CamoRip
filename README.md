# CamoRip

## Experimental AIR branch

This branch adds a Chunithm AIR controller. Mainline CamoRip stays separate.

**Chunithm AIR → Actual capture** shows delivered camera FPS, the configured rate and dropped frames. It updates about once per second and works with video preview off. Update both the PC app and iPhone IPA for this readout.

The Android slider receiver is built into this branch's Windows app. Open **Android slider** to enable it, find your tablet and connect over USB. No separate bridge is needed. The [Android app](android-slider/README.md) also supports local-network TCP/UDP, skins and Material Expressive settings. AIR stays on the iPhone.

1. Install this branch's Windows build and matching Legacy or Regular IPA.
2. Connect over USB or pair over Wi-Fi. Select the rear camera and preferably 720p60.
3. Start the game with the Brokenithm input DLL that creates `Local\BROKENITHM_SHARED_BUFFER`.
4. Open **Chunithm AIR** in the PC sidebar and enable input. **Live calibration preview on PC** shows the camera and six sensor outlines inside CamoRip. Triggered zones turn green and show **TRIGGERED**; cleared zones show **clear**.
5. Set the zone column, height and threshold on PC. Clear your hands and click **Apply and recalibrate empty background**.

AIR detection always runs on the phone. PC calibration preview temporarily sends video as well as zone states. Turn that preview off for gameplay to stop video encoding/transmission without losing calibration. Minimizing to the tray also switches calibration video off. Zone indicators keep updating with video off and clear if sensor updates expire. Phone preview and screen dimming remain available. Input releases when disabled, disconnected, or stale for 500 ms; the watchdog checks every 100 ms.

This detects brightness changes, not fingers or hand landmarks. Shadows, camera movement and exposure changes can trigger zones. Recalibrate after moving the camera or changing lighting. Turn AIR off before using the webcam again.

Compatibility targets the six-zone input behavior of [Chuni Air Mobile](https://github.com/karnesss/chuni-air-mobile/releases/tag/newrelease). Its Android app, ADB bridge and source are not bundled. This branch's builds replace the installed CamoRip app; they are not a second installation. Physical-device accuracy and end-to-end latency still need measurement.

CamoRip turns an iPhone into a webcam for Windows 11. It streams video over USB or a paired local Wi-Fi network and exposes the camera to compatible apps such as Windows Camera and meeting software.

## Features

- Control exposure, focus, white balance and torch from Windows. The iPhone applies each camera adjustment.
- Choose supported video resolutions and frame rates, including 720p, 1080p and 4K modes.
- Use QR pairing and automatic reconnect on the same local network.
- Keep the webcam running from the Windows system tray.
- Hide, mirror, fit or fill the desktop preview and choose its frame rate.
- Use a Windows virtual camera with hardware-accelerated H.264 decoding where supported.

## Requirements

- Windows 11 x64.
- Apple Devices or iTunes for USB connections.
- An iPhone running iOS 15 or later and a data cable for USB.
- Camera and local network permissions when prompted.

## Install

Download the Windows installer and iPhone IPA from the latest successful [GitHub Actions build](https://github.com/Tskk-dev/CamoRip/actions/workflows/build.yml). Choose the **Legacy** IPA for iOS 15 and later, or **Regular** for iOS 18 and later. Both include the same features; supported camera modes depend on the iPhone.

Install the Windows companion, sign the IPA with an installer supported by the phone, then open CamoRip on both devices. For USB, unlock the phone, trust the computer and connect a data cable. For Wi-Fi, pair once by scanning the Windows QR code while both devices share a local network.

The iPhone app must stay open while streaming. Locking the iPhone stops camera capture. A hidden preview or dimmed display can reduce heat.

See the [installation guide](docs/INSTALLATION.md) and [wireless pairing guide](docs/WIRELESS.md) for setup and troubleshooting.

## About

- Built by [Tskk-dev](https://github.com/Tskk-dev). 
- Support development on [Ko-fi](https://ko-fi.com/tskkkdev).

## License

CamoRip is MIT licensed. Dependency notices are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
