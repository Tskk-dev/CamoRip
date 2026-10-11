# Encoded GPU regression fixtures

These original test patterns were generated locally with FFmpeg 7.1/libx264,
using its synthetic `testsrc2` source; they contain no camera footage. FFmpeg is
a development tool and is not bundled with the app.

```text
ffmpeg -f lavfi -i testsrc2=size=1920x1080:rate=60 -frames:v 1 -c:v libx264 -profile:v main -preset fast -tune zerolatency -g 60 -bf 0 -pix_fmt yuv420p -f h264 Hardware1080p.h264
```

The 720p fixture uses `size=1280x720`. The automatic decoder test compares every
decoded sample with forced software decoding, including after a resolution
change. CI may use software fallback; set `UCAM_REQUIRE_HARDWARE_TESTS=1` on a GPU
machine to require confirmed D3D11 output.

The separately generated I_PCM syntax fixtures run in software. On the tested
Vega 10 driver, those handcrafted PCM pictures produced zero luma through DXVA,
while these encoded Main-profile patterns matched the reference byte for byte.
