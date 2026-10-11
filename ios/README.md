# iPhone USB camera

Native AVFoundation capture and VideoToolbox H.264 encoding for iOS 15 or later,
including iPhone 6s Plus. Video only. Keep the app in the foreground and hold the
phone sideways; the encoded stream always uses landscape dimensions.

## Build

On a macOS runner or Mac with Xcode and XcodeGen:

```sh
cd ios
xcodegen generate
xcodebuild -project UniversalCamPhone.xcodeproj -scheme UniversalCamPhone \
  -configuration Release -sdk iphoneos -destination 'generic/platform=iOS' \
  -derivedDataPath build CODE_SIGNING_ALLOWED=NO build
mkdir -p build/Payload
cp -R build/Build/Products/Release-iphoneos/UniversalCam.app build/Payload/
(cd build && zip -r UniversalCam-unsigned.ipa Payload)
```

This makes an **unsigned IPA**, not a universally installable IPA. No paid Apple
membership or signing secrets are required to compile it. A jailbreak installer
must support unsigned or locally signed apps on the phone's actual iOS version.
An AppSync-style installation environment, if already available on that jailbreak,
may be usable. This project neither installs a jailbreak nor bypasses another
app's license. Confirm installation on the actual phone before assuming it works.

If that route fails, use a supported free Apple ID signing/sideloading tool that
can sign this IPA, or sign from Xcode with a Personal Team. Apple Personal Team
provisioning profiles expire after seven days and need renewal:
https://developer.apple.com/help/account/basics/about-your-developer-account

## Use

1. Install the IPA by a route supported on your phone; open UniversalCam.
2. Grant Camera permission. No Microphone or Local Network permission is requested.
3. Install Apple's USB device support on Windows, connect the cable, unlock the
   iPhone and accept Trust This Computer.
4. Open the Windows companion. It reaches the app's `127.0.0.1:7780` listener
   through Apple's USB multiplexing service.
5. Use Start/Stop on either device. Switch selects the other physical camera.

The rear camera targets 1920×1080 at 60 fps. Camera configuration reports its
actual dimensions in `configure_ack`; the front camera on this phone may use
1280×720. The Windows virtual camera can scale that to its advertised output.
Switching camera restarts H.264 with an IDR frame and SPS/PPS.

Entering the background stops streaming and capture. Returning opens the preview
again; press Start to resume. USB disconnect clears the encoder, releases the
screen wake lock, and leaves the loopback listener ready for reconnection. The
Windows companion initiates a fresh handshake when the cable is reconnected.

## Locality and checks

The listener binds IPv4 loopback explicitly using
[`NWParameters.requiredLocalEndpoint`](https://developer.apple.com/documentation/network/nwparameters/requiredlocalendpoint).
It does not bind Wi-Fi/cellular interfaces, advertise Bonjour, or open outbound
sockets. [usbmuxd](https://cgit.libimobiledevice.org/usbmuxd.git/tree/README) supports
connecting to listening localhost sockets on an iOS device. USB-only selection
must also be enforced by the Windows companion. Video is unencrypted inside the
local USB tunnel, so any trusted process that can access that phone can control
the app; there is no Internet service or account.

Physical acceptance remains necessary: unsigned installer compatibility, denied
camera permission and Settings recovery, front/rear switching, locking and
foreground recovery, disconnect/reconnect during a stream, and sustained rear
1080p60 performance/temperature. A macOS hosted build cannot validate those on an
iPhone 6s Plus. Also verify that the listener is unreachable from another machine
over Wi-Fi while USB streaming succeeds.

## Cooler display mode (0.1.1)

While streaming, the phone preview defaults off. Enable **Show preview while
streaming** to frame a shot; disabling it detaches AVCaptureVideoPreviewLayer
without stopping capture or changing video sent to Windows. The preview remains
available before streaming.

**Dim screen after 15 seconds** defaults on. After inactivity the app uses minimum
UIScreen brightness and a simple black screen with tap-to-restore controls. A tap
restores the saved brightness and restarts the 15-second timer. Stopping, USB
disconnection, errors, leaving the app or turning the option off restores
brightness. Both settings persist. Minimum brightness is not screen lock or a
backlight-off mode; keep the app in the foreground. The iPhone 6s Plus LCD still
uses its backlight at minimum brightness.

Capture remains 1080p60; no thermal FPS reduction is applied. Encoded contiguous
AVCC buffers are read directly into one preallocated Annex B packet, retaining
a copy fallback for noncontiguous Core Media storage. USB packets reserve their
final size. These remove avoidable copies; actual heat/fps benefits need a
physical retest. Phone logs now include captureDropped, thermal state and screen
brightness; serious/critical thermal state also appears in the UI.

The macOS workflow builds an iOS 15 IPA and runs simulator checks for brightness
restoration and Annex B conversion. The simulator does not prove physical
brightness, preview detachment, USB streaming or phone temperature changes.

Retest on the phone: stream with Wi-Fi off; enable/disable preview without
interrupting the PC feed; wait 15 seconds and tap to wake; repeat after camera
switch, Stop, unplug, Control Center and background/foreground; confirm original
brightness is restored and both screen orientations keep controls reachable.
Compare phone thermal states and capture/encode/send counters with preview off
and minimum brightness over the same capture duration.
