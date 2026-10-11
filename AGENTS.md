# Project instructions

This repository implements an iOS 15 video-only USB camera companion and a Windows 11 x64 Media Foundation virtual camera. See README.md and docs/UPSTREAM_AUDIT.md before reusing any upstream UniversalCam component.

- Use Apple's loopback USB service and accept only USB-connected devices. Keep the phone listener bound to loopback. Do not add Wi-Fi, audio, an external camera server, Windows 10, or a custom DirectShow/kernel driver to v1.
- Build native components with MSVC and the Windows 11 SDK; `scripts/build-windows.ps1` builds/tests/publishes the Windows code. The macOS workflow builds an unsigned IPA targeting iOS 15 with XcodeGen.
- Preserve UniversalCam and Microsoft MIT notices for reused source.
- Automated builds do not prove iPhone installation, USB reachability, sustained 1080p30, Windows Frame Server activation, or meeting-app compatibility. Record physical checks using docs/VALIDATION.md and report unperformed checks honestly.
- Do not enable the old upstream managed COM scaffolding. Media Foundation camera creation requires the registered native source CLSID, correct interfaces, allocator/events, and a usable frame channel.
