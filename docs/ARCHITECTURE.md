# Architecture

Version 0.4 also supports a paired local-network TLS connection on Windows TCP 7781. A short-lived QR pins the PC certificate and bootstraps a saved credential. Bonjour advertises `_camorip._tcp`; USB takes priority over wireless. Both paths use the same bounded H.264/control protocol and decoder. See [wireless pairing](WIRELESS.md).

```mermaid
flowchart LR
    A[AVFoundation camera] --> B[VideoToolbox H.264]
    B --> C[iPhone loopback TCP 7780]
    C -->|usbmux over Lightning USB| D[Apple Mobile Device Service]
    D --> E[Windows usbmux client]
    E --> F[Media Foundation H.264 decoder]
    F --> G[BGRA preview and NV12 frame publisher]
    G --> H[Registered COM Media Foundation source]
    H --> I[Windows virtual camera]
    I --> J[Meeting application]
```

The phone listens at `127.0.0.1:7780`. Windows talks to Apple Mobile Device Service at `127.0.0.1:27015`, lists its devices, filters the USB connection type, and requests a connection to port 7780. After the service's success response, that socket carries the app protocol. Pair-record presence is a diagnostic hint; it does not prove that the phone is currently unlocked or that every trust prerequisite has been satisfied.

The app protocol multiplexes newline-delimited UTF-8 JSON control messages and length-prefixed H.264 access units. Media headers carry stream type, payload length, timestamp, and keyframe flags. Both ends bound buffering; after a dropped prediction chain or camera transition, the decoder waits for a new keyframe with parameter sets. See `ios/UniversalCamPhone/Transport/PROTOCOL.md` for byte layout and control messages.

Windows uses its built-in H.264 decoder MFT. The companion publishes frames to a bounded, per-user file mapping. The camera source is activated by CLSID in the Windows camera infrastructure; it does not use the old upstream live-object P/Invoke. Frame storage grants the current user and the required Windows service identities access, and is cleared when the publisher closes. Treat active-session temporary storage as video data; runtime validation must confirm cleanup and service activation.

The decoder keeps the MFT's NV12 output packed and sends it directly to the camera channel when the phone supplies 1080p. This avoids converting each full-resolution frame to BGRA and back to NV12. Only lower-resolution formats are scaled. The desktop preview is a separate 640×360 BGRA conversion limited to 15 updates per second, so it cannot throttle the webcam stream.

The source produces one NV12 stream at the acknowledged capture dimensions and frame rate. The selector exposes phone-supported 720p30/60, 1080p30/60 and 4K30 modes. Mode changes retire the old source and mapped channel, create a new channel, and register the camera with the selected media type and sample cadence. Meeting apps holding the old source may need to reopen the camera. Native-sized frames avoid scaling; mismatched frames use a defensive fit/letterbox path. Stopping, disconnecting, or stale video produces a neutral frame.

The installer registers the x64 source DLL in COM. The companion creates a virtual camera for its current user and session, with the source's CLSID and frame publisher attributes. Registration of the provider and creation of a camera session are distinct operations. Source activation, ACL compatibility with Windows Camera Frame Server, timing, and client compatibility need actual Windows 11 runtime checks.

