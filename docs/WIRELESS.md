# Wireless iPhone webcam

## Pair

1. Install the updated Windows and iPhone apps.
2. Connect both devices to the same local network. The PC can use Wi-Fi or Ethernet.
3. Use a trusted Private network in Windows. The installer adds app-specific firewall rules for TCP 7781 and UDP 5353, restricted to Private networks and the local subnet.
4. On Windows, choose **Camera > Connect wirelessly**.
5. On iPhone, tap **Connect wirelessly** and scan the QR code. Scanning pauses video while the scanner uses the camera.
6. Allow Local Network access, then start video. Keep the iPhone app open.

The code expires after five minutes, works once, and is invalidated when the dialog closes or refreshes. Scan while the dialog is open. After pairing, the phone reconnects automatically. USB takes priority; unplug to use Wi-Fi.

## Privacy

Video and controls use TLS with the PC certificate pinned from the QR. Pairing tokens are random and sent inside TLS. Windows protects its identity and credential hashes with DPAPI; the phone stores its credential in Keychain. No account, cloud relay, audio or internet streaming is used.

On Windows, **Settings > Forget paired phones** revokes wireless credentials. On iPhone, **Forget paired PC** removes the saved PC.

## Troubleshooting

- Refresh expired QR codes and scan again.
- Check iPhone Local Network permission, Windows Private-network firewall rules and that both devices share a subnet.
- Guest Wi-Fi, client isolation and VPN routes can block connections.
- Bonjour discovery follows address changes. Rescan if the PC changes networks and discovery is blocked.
- Use strong Wi-Fi or lower resolution and bitrate to reduce dropped frames.

Version 0.4 supports IPv4 local networks. Simulator and loopback tests do not validate physical Wi-Fi, QR scanning, permission prompts, firewall installation or meeting apps. Those checks require a phone and PC.
