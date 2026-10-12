# CamoRip

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

Built by [Tskk-dev](https://github.com/Tskk-dev). Support development on [Ko-fi](https://ko-fi.com/tskkkdev).

## License

CamoRip is MIT licensed. It includes adapted code from [UniversalCam](https://github.com/Shivinder-s/universal-cam).
