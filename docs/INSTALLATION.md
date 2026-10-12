# Installation

## Windows

For Wi-Fi, follow [wireless pairing](WIRELESS.md). Apple device support is required for USB only. The installer adds app-specific, local-subnet firewall rules on Private networks for video (TCP 7781) and discovery (UDP 5353).

Install Apple Devices or iTunes from Apple's supported distribution and connect the phone once. Confirm the Apple application can see it. Unlock the phone and accept Trust This Computer.

Download the Windows artifact from a successful Actions run and run its setup executable. Administrator elevation installs and registers `UCamMediaSource.dll`; the companion itself runs as the signed-in user. Builds are not Authenticode-signed, so Windows may show an unknown-publisher prompt. Verify that the download is from this repository and commit.

Keep the companion running while using the camera. Choose **CamoRip** in the target application's camera menu after the companion reports that the Windows camera started. If an app caches the device list, reopen its camera settings or restart that app.

Logs are stored under `%LOCALAPPDATA%/UniversalCam/Logs/latest.log`. Video is not intentionally written to the log. Session frames use a protected temporary mapping under the same application's local data directory.

## iPhone: unsigned IPA

CI produces two **unsigned arm64 IPAs** from the same source:

- `camorip-ios-unsigned`: `CamoRip-ios15-unsigned.ipa`, iOS 15 or later, including the iPhone 6s Plus.
- `camorip-iphone16-unsigned`: `CamoRip-iphone16-ios18-unsigned.ipa`, iOS 18 or later, intended for iPhone 16. This profile also runs on other compatible iPhones; it is not a different feature tier.

Both packages preserve camera modes, phone-side adjustments, torch, quality controls, screen dimming and preview controls. They share the bundle identifier and replace one another when installed. Use the matching Windows companion for all controls. The iPhone 16 uses a USB-C **data** cable and the same Apple Devices pairing/trust procedure; no Lightning adapter is required. Simulator checks and builds do not establish physical USB streaming on that model.

Sign the iPhone 16 IPA with an installer supported by its actual iOS version (for example a free Apple ID signing route); do not assume the older phone's jailbreak install method applies.

 A jailbreak alone does not establish that it can install arbitrary unsigned apps. Before relying on an install route, check the actual iOS version, jailbreak, installer, signature handling, and entitlements on this phone. Use the existing installer and its documented signing requirements. Do not mark this step passed until the app launches, requests Camera permission, and remains usable after relaunch.

If that route rejects the package, use an installer that signs with a free Apple ID/Personal Team. Follow its trusted local signing flow. Personal Team provisioning profiles expire after seven days and require renewal; CI does not bypass this. [Apple account guidance](https://developer.apple.com/help/account/basics/about-your-developer-account).

Grant Camera permission, leave the app in the foreground, and connect the data cable. iOS normally suspends camera capture in the background; the app stops streaming when it goes into the background. The display is kept awake during streaming.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Apple USB service unavailable | Install/repair Apple Devices or iTunes and confirm its Apple Mobile Device service is running. |
| No USB phone detected | Use a data cable, unlock the phone, reconnect, and confirm Apple Devices/iTunes detects it. Wi-Fi device entries are intentionally ignored. |
| Trust/pairing guidance | Accept Trust This Computer while unlocked; reconnect and check the Apple app. A stored pairing record alone cannot diagnose the exact lock/trust state. |
| Phone app port unavailable | Open the iPhone companion and keep it in the foreground; check its connection status. |
| Camera permission denied | Enable the app under iPhone Settings → Privacy → Camera or the app's Settings page, then reopen it. |
| IPA install/signature failure | Confirm that installer supports this iOS/jailbreak and signing mode, or use free Apple ID signing. Record the exact error. |
| Windows camera source unavailable | Reinstall the setup executable and allow administrator registration. Check Windows camera privacy settings and the companion's message. |
| Video interrupted | Keep the companion apps open, reconnect USB, and wait for the new handshake/keyframe. Check both status messages. |

There is no guarantee for legacy apps that require a separate DirectShow driver. Validate the actual meeting app you intend to use.
