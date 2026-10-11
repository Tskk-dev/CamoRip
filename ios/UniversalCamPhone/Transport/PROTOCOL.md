# USB webcam protocol v1

Windows connects through Apple's usbmux service to the iPhone loopback listener
`127.0.0.1:7780`. There is one ordered TCP byte stream. There is no Wi-Fi discovery,
audio stream, external server, or TLS certificate exchange.

## Framing

Control messages: byte `0x00`, UTF-8 JSON object, byte `0x0A` newline. The receiver
must buffer across TCP receive boundaries and support multiple coalesced messages.
Control JSON is limited to 64 KiB on the phone. Unknown/malformed packet types
close the connection; Windows sends only control messages to the phone.

Video messages (phone → Windows):

| Offset | Bytes | Meaning |
| --- | --- | --- |
| 0 | 1 | `0x01` video |
| 1 | 4 | UInt32 little-endian payload length |
| 5 | 8 | Int64 little-endian presentation timestamp, microseconds |
| 13 | 1 | Bit 0: keyframe; all other bits zero |
| 14 | length | H.264 Annex B NAL units with four-byte start codes |

Each keyframe includes SPS and PPS before its IDR slices. Timestamps use the
capture clock; the host establishes its output time origin from the first frame.
The capture clock is not synchronized to the PC's wall clock.

## Handshake and control

```text
Windows -> phone: {"type":"welcome"}
phone -> Windows: {"type":"hello","deviceName":"iPhone","capabilities":["h264","usb","video_only"]}
phone -> Windows: {"type":"orientation_changed","orientation":"landscape"}
Windows -> phone: {"type":"configure","resolution":"1920x1080","fps":30,"codec":"h264","bitrate":8000000}
phone -> Windows: {"type":"configure_ack","resolution":"1920x1080","fps":30}
Windows -> phone: {"type":"start_stream"}
phone -> Windows: video packets
```

`configure_ack` contains the **actual** capture dimensions. It may be 1280x720
when a front camera cannot supply 1080p. Windows must honor a new acknowledgement
after `switch_camera`, including while a stream is active. Supported requests:
H.264, width 640–1920, height 360–1080, 15–30 fps, bitrate 0.5–12 Mbps. Capture
chooses the largest supported 16:9 format within requested dimensions.

`start_stream` and `stop_stream` may originate on either side. Phone buttons send
the corresponding control message. The phone also replies to an accepted Windows
request with that message, allowing Windows to distinguish an accepted start from
a permission/configuration failure. Windows treats an incoming message as a state
acknowledgement and never echoes it; this prevents control loops. An unsolicited
`configure_ack` during a camera switch must not start a previously stopped stream.
`switch_camera` optionally includes `cameraID`;
without it the phone toggles front/rear. The phone responds with `configure_ack`
then `switch_camera_ack` (`cameraID`, `cameraName`), and restarts with an IDR.

`list_cameras` asks for `available_cameras` with `cameras` and `currentCameraID`.
Each camera has `id`, `name`, `position`, `type`, `zoom_factor`.

Either side may send `ping` with `ts` (Unix milliseconds); the peer responds with
`pong` and the unchanged timestamp. The phone closes a connection if no heartbeat
reply arrives for ten seconds. A new USB connection starts a fresh handshake and
compression session. No video is sent until configured and started.

Errors use `{"type":"error","code":"camera_denied","message":"Enable Camera permission in iPhone Settings."}`.
Other phone error codes include `camera_unavailable`, `capture_configuration_failed`,
`unsupported_configuration`, `camera_switch_failed`, `invalid_control`, and
`encoder_failed`, and `app_in_background`. Errors stop video and are displayed locally; the USB control
connection stays available for a retry unless framing is invalid.

To bound delay the phone permits three video sends in flight. If a compressed
reference frame is dropped it discards delta frames until a fresh keyframe with
SPS/PPS, and requests that keyframe from VideoToolbox.
