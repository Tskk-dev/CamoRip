using CamoRip.VirtualCamera;

namespace CamoRip.Tests;

public class NV12ConverterTests
{
    [Theory]
    [InlineData(1280, 720, 1920, 1080)]
    [InlineData(6, 10, 16, 12)]
    [InlineData(18, 8, 10, 14)]
    public void FitNv12_OptimizedScalingMatchesNearestNeighbor(int width, int height, int outputWidth, int outputHeight)
    {
        var source = new byte[width * height * 3 / 2];
        new Random(42).NextBytes(source);
        int yBytes = outputWidth * outputHeight;
        var expected = new byte[yBytes * 3 / 2];
        Array.Fill(expected, (byte)16, 0, yBytes);
        Array.Fill(expected, (byte)128, yBytes, yBytes / 2);
        double scale = Math.Min((double)outputWidth / width, (double)outputHeight / height);
        int drawWidth = Math.Max(2, (int)(width * scale) & ~1);
        int drawHeight = Math.Max(2, (int)(height * scale) & ~1);
        int left = ((outputWidth - drawWidth) / 2) & ~1;
        int top = ((outputHeight - drawHeight) / 2) & ~1;
        for (int y = 0; y < drawHeight; ++y)
        for (int x = 0; x < drawWidth; ++x)
            expected[(top + y) * outputWidth + left + x] =
                source[(int)((long)y * height / drawHeight) * width + (int)((long)x * width / drawWidth)];
        for (int y = 0; y < drawHeight; y += 2)
        for (int x = 0; x < drawWidth; x += 2)
        {
            int input = width * height + (int)((long)(y / 2) * (height / 2) / (drawHeight / 2)) * width +
                ((int)((long)x * width / drawWidth) & ~1);
            int output = yBytes + ((top + y) / 2) * outputWidth + left + x;
            expected[output] = source[input];
            expected[output + 1] = source[input + 1];
        }
        Assert.Equal(expected, NV12Converter.FitNv12(source, width, height, outputWidth, outputHeight));
    }

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
}
