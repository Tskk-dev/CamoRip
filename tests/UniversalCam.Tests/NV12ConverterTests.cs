using UniversalCam.VirtualCamera;

namespace UniversalCam.Tests;

public class NV12ConverterTests
{
    [Fact]
    public void FitNv12_1080p_ReturnsDecoderBufferWithoutCopying()
    {
        var source = new byte[1920 * 1080 * 3 / 2];
        Assert.Same(source, NV12Converter.FitNv12(source, 1920, 1080, 1920, 1080));
    }

    [Fact]
    public void FitNv12_ScalesLumaAndChromaPlanes()
    {
        byte[] source = [16, 32, 64, 96, 128, 129];
        var output = NV12Converter.FitNv12(source, 2, 2, 4, 4);
        Assert.Equal(new byte[]
        {
            16, 16, 32, 32,
            16, 16, 32, 32,
            64, 64, 96, 96,
            64, 64, 96, 96,
            128, 129, 128, 129,
            128, 129, 128, 129
        }, output);
    }

    [Theory]
    [InlineData((byte)16, (byte)0)]
    [InlineData((byte)235, (byte)255)]
    public void CreateBgraPreview_ConvertsLimitedRangeNeutralNv12(byte luma, byte expected)
    {
        byte[] source = [luma, luma, luma, luma, 128, 128];
        var preview = NV12Converter.CreateBgraPreview(source, 2, 2, 2, 2);
        for (var index = 0; index < preview.Length; index += 4)
        {
            Assert.Equal(expected, preview[index]);
            Assert.Equal(expected, preview[index + 1]);
            Assert.Equal(expected, preview[index + 2]);
            Assert.Equal((byte)255, preview[index + 3]);
        }
    }

    [Theory]
    [InlineData((byte)0, (byte)16)]
    [InlineData((byte)255, (byte)235)]
    public void ScaledCameraOutput_UsesLimitedRange709NeutralColors(byte rgb, byte expectedLuma)
    {
        var bgra = Enumerable.Repeat(rgb, 4 * 4 * 4).ToArray();
        var nv12 = NV12Converter.ConvertBgra32ToNv12Scaled(bgra, 4, 4, 8, 8);
        Assert.Equal(96, nv12.Length);
        Assert.All(nv12.Take(64), value => Assert.Equal(expectedLuma, value));
        Assert.All(nv12.Skip(64), value => Assert.Equal((byte)128, value));
    }

    [Fact]
    public void ScaledCameraOutput_LetterboxesPortraitWithNeutralBlackBars()
    {
        var whitePortrait = Enumerable.Repeat((byte)255, 2 * 4 * 4).ToArray();
        var nv12 = NV12Converter.ConvertBgra32ToNv12Scaled(whitePortrait, 2, 4, 8, 4);
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 8; column++)
                Assert.Equal(column is 2 or 3 ? (byte)235 : (byte)16, nv12[row * 8 + column]);
        }
        Assert.All(nv12.Skip(32), value => Assert.Equal((byte)128, value));
    }

    [Theory]
    [InlineData(7, 4)]
    [InlineData(8, 3)]
    public void ScaledCameraOutput_RejectsOddNv12Dimensions(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NV12Converter.ConvertBgra32ToNv12Scaled(new byte[16], 2, 2, width, height));
    }

    [Fact]
    public void ConvertBgra32ToNv12_ValidInput_ProducesCorrectSize()
    {
        // Arrange: 1920x1080 BGRA32 frame
        int width = 1920;
        int height = 1080;
        int bgraSize = width * height * 4;
        byte[] bgra = new byte[bgraSize];

        // Act
        byte[] nv12 = NV12Converter.ConvertBgra32ToNv12(bgra, width, height);

        // Assert
        int expectedSize = width * height * 3 / 2;
        Assert.Equal(expectedSize, nv12.Length);
    }

    [Fact]
    public void ConvertBgra32ToNv12_SmallFrame_ProducesCorrectSize()
    {
        // Arrange: 2x2 BGRA32 frame (simplest non-trivial case)
        int width = 2;
        int height = 2;
        byte[] bgra = new byte[width * height * 4];

        // Fill with a pattern (R=255, G=128, B=64 for each pixel)
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 0] = 64;  // B
            bgra[i + 1] = 128; // G
            bgra[i + 2] = 255; // R
            bgra[i + 3] = 255; // A
        }

        // Act
        byte[] nv12 = NV12Converter.ConvertBgra32ToNv12(bgra, width, height);

        // Assert
        // 2x2 frame: Y plane = 4 bytes, UV plane = 2 bytes
        int expectedSize = 2 * 2 * 3 / 2;  // 6 bytes
        Assert.Equal(expectedSize, nv12.Length);
    }

    [Fact]
    public void ConvertBgra32ToNv12_InvalidSize_ThrowsArgumentException()
    {
        // Arrange
        byte[] bgra = new byte[100];  // Wrong size

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            NV12Converter.ConvertBgra32ToNv12(bgra, 1920, 1080));
    }

    [Fact]
    public void ConvertBgra32ToNv12_BlackFrame_ProducesZeroYValues()
    {
        // Arrange: All black pixels (R=0, G=0, B=0)
        int width = 4;
        int height = 4;
        byte[] bgra = new byte[width * height * 4];

        // Act
        byte[] nv12 = NV12Converter.ConvertBgra32ToNv12(bgra, width, height);

        // Assert: Y plane should be all zeros (black = no luminance)
        for (int i = 0; i < width * height; i++)
        {
            Assert.Equal(0, nv12[i]);
        }
    }

    [Fact]
    public void ConvertBgra32ToNv12_WhiteFrame_ProducesMaxYValues()
    {
        // Arrange: All white pixels (R=255, G=255, B=255)
        int width = 4;
        int height = 4;
        byte[] bgra = new byte[width * height * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 0] = 255;  // B
            bgra[i + 1] = 255;  // G
            bgra[i + 2] = 255;  // R
            bgra[i + 3] = 255;  // A
        }

        // Act
        byte[] nv12 = NV12Converter.ConvertBgra32ToNv12(bgra, width, height);

        // Assert: Y plane should be all 255 (white = max luminance)
        for (int i = 0; i < width * height; i++)
        {
            Assert.Equal(255, nv12[i]);
        }
    }
}

