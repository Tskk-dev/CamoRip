# CamoRip Slider

Android touch controller for Chunithm, built for use alongside CamoRip AIR.
Installs separately from Chuniboard and Rustnithm. Android 8.0 or newer.

- 32 horizontal sensors or a 16-column, two-row layout.
- Independent finger tracking, adjustable overlap and play-area height.
- Immediate input changes, configurable heartbeat rate, automatic reconnect.
- Game LED feedback, sensor grid, touch diagnostics and optional haptics.
- Slider-only packets. CamoRip keeps control of AIR.
- Material 3 Expressive, system/light/dark themes, dynamic colors and accent presets.
- Background images, JSON skin import/export, preset LEDs and language selection.
- Card access code, coin, service and test controls. No AIR modes in the Android app.

## Connect over USB

1. Enable USB debugging on the tablet and authorize this PC.
2. Install `app-debug.apk` from the Android workflow artifact.
3. Start the game using the Brokenithm-Evolved input DLL.
4. Open **Android slider** in CamoRip AIR on the PC.
5. Click **Find tablets**, select the Redmi and click **Connect over USB**.
6. Open CamoRip Slider and tap **Connect**.

Stop other slider servers first. The receiver runs inside CamoRip, including in the
system tray. USB binds only to localhost. Slider and optional game-button writes
leave the six AIR bytes alone. Input releases on disconnect or after 500 ms without
a complete packet. The old standalone bridge remains available as a CLI fallback.

For local-network play, choose TCP or UDP on the PC and tablet, enter the PC address
shown in CamoRip and use the same port. Windows Firewall must allow the selected port
on your private network. The USB mode needs no inbound LAN access.

Settings control layout, sensitivity, play area, heartbeat rate, port and visuals.
The heartbeat setting does not change the tablet's hardware touch sampling rate.
Touch changes bypass the heartbeat wait. Pending changes older than 20 ms cause
a reconnect instead of delayed playback. The PC ACK display measures round-trip
delivery to the bridge, not physical finger-to-game latency.
The Android endpoint uses a local socket to avoid an extra TCP connection on the tablet.
If you change the port, pass it to the bridge and change the number on both sides of `adb reverse`.
USB reconnection or an ADB restart may require running `adb reverse` again.
The PC's **Connect over USB** button does this for you.
The app respects the tablet's display refresh rate. **Hide** removes the control bar;
press Back to restore it when using a full-height play area.
JSON skins use CamoRip's settings format and do not include your card code or image file.

## Builds and tests

Use JDK 17 and Android SDK 35:

```sh
./gradlew testDebugUnitTest lintDebug assembleDebug
./gradlew connectedDebugAndroidTest
```

The current workflow builds the APK, runs lint, and builds the Windows installer.
Automated and device tests are paused at the user's request.
The workflow produces a signed debug APK for testing, not a Play Store release.
CI debug signing keys can change between runs; if an update reports an incompatible
signature, uninstall the previous CamoRip Slider test build first. This clears its settings.

Tests cover simultaneous contacts, overlapping fingers, release order, cancellation,
queued transitions, fragmented TCP packets, LED feedback and AIR isolation.
Physical touch latency and game compatibility still require testing.

For an isolated USB transport benchmark, run the bridge with `--benchmark`, then
`adb reverse localabstract:camorip-slider-62468 tcp:62468`. With the test APK installed, run:

```sh
adb shell am instrument -w -e class com.camorip.slider.SliderLatencyTest -e benchmark true com.camorip.slider.test/androidx.test.runner.AndroidJUnitRunner
```

This uses a separate memory mapping and cannot send inputs to the game.

## Feature references

[Rustnithm-Client](https://github.com/Ra1nify/Rustnithm-Client) informs the two-row
layout, sensitivity and connection controls.
[MAV3Ndev/Brokenithm-Android](https://github.com/MAV3Ndev/Brokenithm-Android)
informs the 32-column layout and LED grid.
[Brokenithm-Android-Server](https://github.com/tindy2013/Brokenithm-Android-Server)
documents the compatible `IPT` slider and `LED` packet layouts.
These are feature and protocol references; their app and server code is not bundled.
