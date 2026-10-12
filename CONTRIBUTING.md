# Contributing

Keep changes focused, preserve third-party notices, and document protocol changes on both platforms. Do not include pairing records, device identifiers or private images in logs or reports.

## Build

- Windows: install .NET 8, MSVC, CMake and the Windows 11 SDK, then run `./scripts/build-windows.ps1`.
- iOS: install Xcode and XcodeGen on macOS, run `xcodegen generate` in `ios/`, then build the generated project.
- CI builds unsigned iOS IPAs. Physical installation and USB or Wi-Fi streaming require a device check.

## C# style

Follow the repository `.editorconfig`, adapted from osu!lazer's C# conventions. Keep comments on their own line, use XML documentation for public APIs, and explain non-obvious behavior or constraints. Avoid comments that repeat the code, duplicate inherited documentation, or label test phases and code sections.

Keep PRs small and include relevant build results and device details. Do not claim physical-device checks based on simulator results.
