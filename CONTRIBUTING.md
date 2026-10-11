# Contributing

Read AGENTS.md, README.md, and docs/UPSTREAM_AUDIT.md first. V1 supports a video-only iOS 15 USB companion and a Windows 11 x64 Media Foundation virtual camera.

## Build and verify

- Windows: install .NET 8, MSVC, CMake, and a Windows 11 SDK, then run `./scripts/build-windows.ps1`. This builds both native DLLs, runs the managed and native tests, and publishes the desktop app.
- iOS: install Xcode and XcodeGen on macOS, run `xcodegen generate` in `ios/`, and open the generated Xcode project. GitHub Actions builds an unsigned IPA without a paid membership.
- Use docs/VALIDATION.md for checks requiring an actual iPhone, Apple USB support, Frame Server, and a meeting app. Record failures and unperformed checks accurately.

Branch from `main`, keep changes focused, preserve third-party notices, and document protocol changes on both platforms. Include relevant build results and device details in pull requests and bug reports. Exclude pairing records, personal device identifiers, and private images from logs and reports.
