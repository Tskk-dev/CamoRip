# Windows 11 camera source

`UCamMediaSource.dll` is a native, user-mode Media Foundation camera source. It
implements an activatable `IMFMediaSourceEx`, one `IMFMediaStream2`, event queues,
capture stream and legacy sensor profile attributes, pipeline sample allocator
support, sample request tokens, timestamps and stream start/stop/shutdown.
The output type is NV12 at the negotiated capture dimensions and rate (720p30/60, 1080p30/60 or 4K30). Each source advertises its selected mode; changing mode creates a fresh channel and restarts registration. Native-sized decoded frames are copied directly. A defensive fit/letterbox path handles a mismatched frame; camera switches follow the new acknowledged mode.

The native class factory returns `IMFActivate`, as required by the Windows
virtual camera pipeline. `MFCreateVirtualCamera` receives its registered CLSID
string; no managed COM source objects are passed to that API.

## Build and check

Use Visual Studio 2022 C++ build tools and Windows SDK 10.0.22000 or newer:

```powershell
cmake -S src/UniversalCam.MediaSource -B artifacts/native -A x64
cmake --build artifacts/native --config Release
ctest --test-dir artifacts/native -C Release --output-on-failure
```

`ProviderSmokeTest.exe` loads the DLL directly without registering a camera or
writing COM registry entries. It verifies source activation and helper release,
explicit activation shutdown and reactivation, all five selected NV12 modes
stream, real pixels through the frame channel, 24 requests with alternating
null/non-null tokens across allocator reuse, monotonic sample timestamps,
the placeholder after the writer closes, stop/restart and shutdown. Windows runners need
desktop Media Foundation components. It does not prove FrameServer activation,
camera enumeration, USB capture or meeting-app compatibility.

## Installer contract

1. Install `UCamMediaSource.dll` beside the x64 desktop companion in a permanent
   directory under Program Files. The DLL uses the static MSVC runtime.
2. Register it elevated with `%SystemRoot%\System32\regsvr32.exe /s <DLL path>`.
   This writes the x64 HKLM COM registration for
   `{760F1341-0182-4E3B-8810-A446EB25070C}` with `ThreadingModel=Both`.
3. Launch the companion as the interactive user. `UCamStartCamera` creates
   `USB iPhone Camera` with **CurrentUser** access and **Session** lifetime.
   Windows appends “Windows Virtual Camera” to the friendly name.
4. On uninstall, close the companion and camera consumers first, then unregister
   with `%SystemRoot%\System32\regsvr32.exe /s /u <DLL path>` before deleting it.
   The session camera is removed when its native host shuts down; the exported
   `UCamRemoveCamera` / `VirtualCameraSession.RemoveInstalledCamera` can remove
   the matching instance explicitly for the current user.

Camera API calls and their COM/MF teardown run on one dedicated MTA thread.
Sample requests are bounded at four outstanding tokens, and a native worker
paces delivery at 60 fps using the pipeline's allocator. Stopping flushes
pending requests. Disconnected, stopped, or stale input uses a neutral camera
icon, so clients continue receiving a valid stream.

## Local frame channel and privacy

The companion creates `%LOCALAPPDATA%\UniversalCam\stream-<instance GUID>.bin` as a temporary
file and maps a fixed 6,220,864-byte header plus two NV12 frame slots. The path
is passed to the source through a private activation attribute. File mapping
supports FrameServer's service session without requiring the companion to
create privileged `Global\` pagefile mappings or run as administrator.

The file's explicit ACL grants the current user, SYSTEM and administrators
full access and LocalService read access; it does not grant other users access.
LocalService is the Windows Camera Frame Server identity. A versioned seqlock
rejects torn writes; the source retains only a bounded frame buffer and shows
its placeholder when no frame arrives for 1.5 seconds. The temporary file is
opened with `FILE_FLAG_DELETE_ON_CLOSE`, its pixels are zeroed on normal close,
and deletion completes when the source also closes its read handle. Temporary
files can still be paged or written by Windows; this is local IPC, not an
encrypted storage mechanism.

Each companion instance uses a different file, so a meeting app retaining an
old source cannot block a companion restart by keeping the old file open.
No TCP ports or network access are used by this camera output path.
The iPhone USB transport is implemented separately in `UniversalCam.Core`.

## Upstream review

The lifecycle, profile, stream-attribute and sample allocator patterns were
reviewed against Microsoft's MIT-licensed Windows-Camera virtual camera sample:

- https://github.com/microsoft/Windows-Camera/tree/master/Samples/VirtualCamera
- https://learn.microsoft.com/en-us/windows/win32/api/mfvirtualcamera/nf-mfvirtualcamera-mfcreatevirtualcamera
- https://learn.microsoft.com/en-us/windows/win32/api/mfidl/nn-mfidl-imfsampleallocatorcontrol

The bundled `MICROSOFT-LICENSE.txt` preserves its license. This implementation
uses SDK WRL rather than that sample's WIL/C++WinRT dependencies. Physical
FrameServer activation and app compatibility still require the installed DLL
and an actual Windows 11 desktop acceptance run.
