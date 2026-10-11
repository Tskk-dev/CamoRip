# UniversalCam audit

The approved reference was `Shivinder-s/universal-cam`, MIT, reviewed at revision `48d11a5756dde98a656578414fddaffaf03419b0`. The user-selected destination `Tskk-dev/ios-camera` was confirmed empty before project initialization.

Findings in the upstream reference:

- Its iOS deployment target was 16.0, which excludes an iPhone 6s Plus on iOS 15.
- Its purported USB path did not establish a Windows Apple/usbmux tunnel by itself. A normal TCP connection is insufficient evidence of cable transport.
- `VirtualCameraSession.InitializeServer()` disabled the Windows 11 Media Foundation camera path.
- Its `MFCreateVirtualCamera` declaration passed a live managed `IMFMediaSource`. The documented Windows API requires a source identifier string (registered COM CLSID) and category pointer/count. Correcting one interface IID does not complete activation.
- The `IMFPresentationDescriptor` IID was incorrect, and source activation/registration was incomplete.
- DirectShow fallback code and status documents claimed more readiness than the source/build evidence supported. Those claims are not carried forward.
- Some decoder tests accepted a null result and therefore could pass without decoding any video.

Reused pieces are reviewed capture/encoding, protocol, preview/conversion, and applicable test code. New implementation work replaces the disabled virtual camera with a native COM provider, confines transport to phone loopback and Apple USB devices, and uses the Windows H.264 decoder. Audio, Wi-Fi, Windows 10, and DirectShow code are outside v1.

References:

- https://github.com/Shivinder-s/universal-cam
- https://learn.microsoft.com/en-us/windows/win32/api/mfvirtualcamera/nf-mfvirtualcamera-mfcreatevirtualcamera
- https://github.com/microsoft/Windows-Camera
- https://github.com/libimobiledevice/libusbmuxd

These findings are source inspection results. Successful hardware streaming, signing/install compatibility, sustained frame rate, and meeting app output must be demonstrated separately.
