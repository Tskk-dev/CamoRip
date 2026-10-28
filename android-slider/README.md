# CamoRip Slider

Android USB touch controller for Chunithm, built for use alongside CamoRip AIR.
Installs separately from Chuniboard and Rustnithm. Android 8.0 or newer.

- 32 horizontal sensors or a 16-column, two-row layout.
- Independent finger tracking, adjustable overlap and play-area height.
- Immediate input changes, configurable heartbeat rate, automatic reconnect.
- Game LED feedback, sensor grid, touch diagnostics and optional haptics.
- Slider-only packets. CamoRip keeps control of AIR.

## Connect over USB

1. Enable USB debugging on the tablet and authorize this PC.
2. Install `app-debug.apk` from the Android workflow artifact.
3. Start the game using the Brokenithm-Evolved input DLL.
4. Run `CamoRip.SliderBridge.exe` from the Windows bridge artifact.
5. Run `adb reverse tcp:52468 tcp:52468` on the PC.
6. Open CamoRip Slider and tap **Connect USB**.

Stop other slider servers first. Leave CamoRip AIR running. The bridge binds to
localhost and writes only the 32 slider bytes in `Local\BROKENITHM_SHARED_BUFFER`.
It releases slider input on disconnect or after 500 ms without a complete packet.
Use Ctrl+C to close the bridge cleanly.

Settings control layout, sensitivity, play area, heartbeat rate, port and visuals.
The heartbeat setting does not change the tablet's hardware touch sampling rate.
If you change the port, pass it to the bridge and change both sides of `adb reverse`.
USB reconnection or an ADB restart may require running `adb reverse` again.

## Builds and tests

Use JDK 17 and Android SDK 35:

```sh
./gradlew testDebugUnitTest lintDebug assembleDebug
./gradlew connectedDebugAndroidTest
```

The workflow produces a signed debug APK for testing, not a Play Store release.
CI debug signing keys can change between runs; if an update reports an incompatible
signature, uninstall the previous CamoRip Slider test build first. This clears its settings.

Tests cover simultaneous contacts, overlapping fingers, release order, cancellation,
queued transitions, fragmented TCP packets, LED feedback and AIR isolation.
Physical touch latency and game compatibility still require testing.

## Feature references

[Rustnithm-Client](https://github.com/Ra1nify/Rustnithm-Client) informs the two-row
layout, sensitivity and connection controls.
[MAV3Ndev/Brokenithm-Android](https://github.com/MAV3Ndev/Brokenithm-Android)
informs the 32-column layout and LED grid.
[Brokenithm-Android-Server](https://github.com/tindy2013/Brokenithm-Android-Server)
documents the compatible `IPT` slider and `LED` packet layouts.
These are feature and protocol references; their app and server code is not bundled.
