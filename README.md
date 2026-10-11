# USB iPhone Camera

Use an iPhone 6s Plus as a Windows 11 webcam through a Lightning USB cable. The iPhone app captures camera video with AVFoundation and encodes H.264 with VideoToolbox. The Windows companion reads it through Apple's local USB device service, decodes it with Windows Media Foundation, and exposes **USB iPhone Camera** to camera apps using the Windows 11 virtual camera API.

I made this app because FUCK CAMO STUDIO AND EVERY SINGLE FUCKING APP THAT NEEDS PAYMENT FOR THIS SHIT LOL I WILL NOT PAY FOR YOUR SHIT. 

#NO SUPPORT WILL BE PROVIDED FOR THIS SHIT YOU GET CODE AND CODE ONLY DO WITH THAT HOW YOU WILL I WILL NOT HELP ANYONE SET THIS UP. 

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

## License

MIT. This project reuses reviewed portions of [UniversalCam](https://github.com/Shivinder-s/universal-cam); preserve its copyright notice in [LICENSE](LICENSE). Dependency and platform details are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
