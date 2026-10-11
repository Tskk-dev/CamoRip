# CamoRip desktop and camera controls

## Implemented architecture

The WinUI 3 window owns presentation only. `App` owns `StreamingService`, which retains the existing USB transport, hardware-preferred decoder, dedicated MMCSS workers, bounded H.264 queue, pooled NV12 buffers and Media Foundation virtual webcam. Hiding the window changes preview visibility; it does not dispose the pipeline.

The preview uses a composition swap chain and D3D11 shaders for video-range BT.709 NV12. It uploads the existing decoded planes to reusable GPU textures, rather than converting to BGRA in a CPU loop. This is **not a zero-copy decode-to-preview pipeline**: the webcam channel still requires the decoder's CPU NV12 readback. WinUI's display clock consumes the newest preview frame, at up to 60/30/15 fps. Cadence uses accumulated deadlines so a 75 Hz display does not round a 60 fps setting down to 37.5 fps. Off, hidden and minimized detach the rendering callback and stop adding preview frames. Webcam publication is independent.

## Interface and settings

- Native WinUI navigation, sliders, toggles and menus provide immediate pressed feedback, pointer capture, keyboard interaction and platform accessibility. The shell uses Segoe UI/system typography, system theme brushes and Mica with Windows' system fallbacks. There are no custom gesture animations that lock input or wait for transitions.
- Camera: Start/Stop, camera switch, resolution/frame rate selection from phone-reported modes, torch and supported torch brightness.
- Camera adjustments: exposure compensation, auto/manual ISO and shutter, focus, white balance temperature/tint, Reset to Auto. Unsupported controls are disabled.
- Preview: 60/30/15/Off, fit/fill, mirror, fullscreen (F11; Escape exits).
- Settings: close-to-tray, start minimized, sign-in launch, diagnostic logging, Open Logs, Exit.
- Tray: Open, Start/Stop, Torch On/Off, Exit. Reopening the executable signals the existing instance.
- Settings and logs remain under `%LOCALAPPDATA%\UniversalCam` for upgrade continuity. Turning logging off synchronously closes the writer; live statistics continue and old files remain.

## Phone controls

The phone advertises `camera_controls_v1`. Windows coalesces each slider's pending values over 50 ms without delaying the thumb or value feedback. Commands contain a monotonic request ID and the camera identity. The phone validates finite numbers, modes and active-format ranges, applies AVFoundation settings on its capture queue, then returns actual state after asynchronous camera setters complete. Errors are returned in camera-state acknowledgements without stopping the video stream.

Exposure duration is limited to the active frame interval so requesting a long shutter cannot silently turn 60 fps into a lower capture rate. White balance gains are clamped to device-supported gains; the acknowledged temperature/tint reflects the resulting gains. Torch requires a streaming session and a supported/available camera. Stop, disconnect, switch and background/shutdown paths turn it off. Heartbeats refresh actual state and thermal availability. Camera switches and reconnects invalidate pending commands.

Update **both** companions for these controls. Older H.264 phone apps still stream, but Windows disables the new controls and asks for a phone update. The iPhone must remain in the foreground; screen dimming and preview-off cooling behavior from PR #3 are included.

## Upgrade behavior

The installer retains its AppId, upgrades the existing installation directory, and keeps the native source CLSID and phone bundle ID. It asks for the old companion to be closed using the existing mutex, removes old executable/shortcuts and creates CamoRip shortcuts. The native host attempts to retire the old display-name camera identity before starting CamoRip. Camera registration migration still needs validation on an installed machine.

## Verification and outstanding physical checks

Local WinUI build passed and 81 Windows tests passed at the initial implementation. CI also builds the native renderer, installer and unsigned iOS 15 IPA, and runs the phone simulator protocol/display lifecycle checks. See the PR for the final run and commit results.

The local shell was launched in isolated `--prototype` mode, with separate settings/logs and no USB connection, while the installed companion kept running. Native UI inspection was blocked by the Computer Use app permission check; successful launch is not a visual/accessibility review.

Physical acceptance remains required: install on the iPhone 6s Plus; verify rear/front capability changes, denied camera permission, torch off on cable pull/stop/camera switch, actual exposure/focus/WB in a meeting app, preview 60/30/15/off, fullscreen, tray continuity, sign-in startup, disabled logging, installer upgrade without duplicate cameras and sustained 1080p60 metrics compared with pipeline v4. Also review keyboard/screen-reader behavior, high contrast, text scaling, reduced animations/transparency and resizing at different DPIs.

## 0.3.0 mode negotiation and torch diagnostics

Both apps must be updated for 720p30/60, 1080p30/60 and 4K30 selection. Only
supported active-camera modes are shown. The saved preference is requested at
connection; the actual acknowledgement drives webcam registration, buffer size,
media type, sample duration and UI. Changing mode restarts the camera and may
require reopening it in meeting apps. Reconfiguration preserves the user's
streaming intent and ignores superseded configuration replies.

Windows torch feedback preserves Slider.Header, avoids rewriting the active
thumb's range/value, validates ranges and clamps incoming values. Optional logs
record outgoing settings and actual acknowledgements. The 0.2.1 native crash
reported while changing torch brightness still needs a stack/reproduction; these
changes are defensive and are not a confirmed root-cause fix.
