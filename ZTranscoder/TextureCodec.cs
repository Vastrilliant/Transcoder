
using System;
using System.IO;
using AstcSharp;
using AstcSharp.Core;
using Texture2DDecoder;

internal static class TextureCodec
{

    public const int FmtRGB24 = 3;
    public const int FmtRGBA32 = 4;
    public const int FmtDXT1 = 10;
    public const int FmtDXT5 = 12;
    public const int FmtDXT5Crunched = 29;
    public const int FmtETC2_RGB = 45;
    public const int FmtETC2_RGBA8 = 47;
    public const int FmtASTC_RGBA_4x4 = 48;
    public const int FmtASTC_RGBA_6x6 = 50;
    public const int FmtASTC_RGBA_8x8 = 51;

    public static string FormatName(int format) => format switch
    {
        FmtRGB24 => "RGB24",
        FmtRGBA32 => "RGBA32",
        FmtDXT1 => "DXT1",
        FmtDXT5 => "DXT5",
        FmtDXT5Crunched => "DXT5Crunched",
        FmtETC2_RGB => "ETC2_RGB",
        FmtETC2_RGBA8 => "ETC2_RGBA8",
        FmtASTC_RGBA_4x4 => "ASTC_RGBA_4x4",
        FmtASTC_RGBA_6x6 => "ASTC_RGBA_6x6",
        FmtASTC_RGBA_8x8 => "ASTC_RGBA_8x8",
        _ => $"Format{format}"
    };

    public static byte[] DecodeToRgba32(byte[] encodedData, int width, int height, int format, string texName)
    {
        switch (format)
        {
            case FmtRGB24:
                return DecodeRGB24(encodedData, width, height);

            case FmtRGBA32:
            {
                int expected = checked(width * height * 4);
                if (encodedData.Length < expected)
                    throw new InvalidDataException(
                        $"RGBA32 data too small for '{texName}': got {encodedData.Length}, expected at least {expected}");
                var rgba = new byte[expected];
                Buffer.BlockCopy(encodedData, 0, rgba, 0, expected);
                return rgba;
            }

            case FmtDXT1:
                return DecodeKyaruDXT(encodedData, width, height, isDxt5: false);

            case FmtDXT5:
                return DecodeKyaruDXT(encodedData, width, height, isDxt5: true);

            case FmtDXT5Crunched:
                return DecodeKyaruDXT5Crunched(encodedData, width, height);

            case FmtETC2_RGB:
                return DecodeKyaruETC2(encodedData, width, height, hasAlpha: false);

            case FmtETC2_RGBA8:
                return DecodeKyaruETC2(encodedData, width, height, hasAlpha: true);

            case FmtASTC_RGBA_4x4:
                return DecodeAstc(encodedData, width, height, FootprintType.Footprint4x4, texName);

            case FmtASTC_RGBA_6x6:
                return DecodeAstc(encodedData, width, height, FootprintType.Footprint6x6, texName);

            case FmtASTC_RGBA_8x8:
                return DecodeAstc(encodedData, width, height, FootprintType.Footprint8x8, texName);

            default:
                throw new NotSupportedException(
                    $"TextureCodec has no decoder for format {format} ('{texName}'); " +
                    "failing closed rather than assuming the pixels are unchanged.");
        }
    }

    public static byte[] EncodeFromRgba32(byte[] rgba32, int width, int height, int outputFormat, string texName)
    {
        switch (outputFormat)
        {
            case FmtRGBA32:
                return rgba32;

            case FmtASTC_RGBA_4x4:
                return EncodeAstc(rgba32, width, height, FootprintType.Footprint4x4, texName);

            case FmtASTC_RGBA_6x6:
                return EncodeAstc(rgba32, width, height, FootprintType.Footprint6x6, texName);

            case FmtASTC_RGBA_8x8:
                return EncodeAstc(rgba32, width, height, FootprintType.Footprint8x8, texName);

            case FmtETC2_RGB:
            case FmtETC2_RGBA8:
                return Etc2Encoder.Encode(rgba32, width, height, outputFormat, texName);

            default:
                throw new NotSupportedException(
                    $"TextureCodec has no encoder for output format {outputFormat} ('{texName}').");
        }
    }

    private static byte[] DecodeRGB24(byte[] data, int width, int height)
    {
        int pixelCount = checked(width * height);
        int expected = checked(pixelCount * 3);
        if (data.Length < expected)
            throw new InvalidDataException(
                $"RGB24 data too small: got {data.Length}, expected at least {expected}");

        var rgba = new byte[pixelCount * 4];
        for (int i = 0, src = 0, dst = 0; i < pixelCount; i++, src += 3, dst += 4)
        {
            rgba[dst + 0] = data[src + 0];
            rgba[dst + 1] = data[src + 1];
            rgba[dst + 2] = data[src + 2];
            rgba[dst + 3] = 255;
        }
        return rgba;
    }

    private static byte[] DecodeKyaruDXT(byte[] encodedData, int width, int height, bool isDxt5)
    {
        int outputSize = checked(width * height * 4);
        var bgra = new byte[outputSize];

        bool ok = isDxt5
            ? TextureDecoder.DecodeDXT5(encodedData, width, height, bgra)
            : TextureDecoder.DecodeDXT1(encodedData, width, height, bgra);

        if (!ok)
            throw new InvalidDataException($"Kyaru Texture2DDecoder failed to decode {(isDxt5 ? "DXT5" : "DXT1")}");

        return BgraToRgba(bgra);
    }

    private static byte[] DecodeKyaruDXT5Crunched(byte[] encodedData, int width, int height)
    {
        byte[]? unpacked = TextureDecoder.UnpackUnityCrunch(encodedData);
        if (unpacked == null || unpacked.Length == 0)
            throw new InvalidDataException("Kyaru Texture2DDecoder failed to unpack UnityCrunch DXT5 data");

        int outputSize = checked(width * height * 4);
        var bgra = new byte[outputSize];
        if (!TextureDecoder.DecodeDXT5(unpacked, width, height, bgra))
            throw new InvalidDataException("Kyaru Texture2DDecoder failed to decode unpacked UnityCrunch DXT5 data");

        return BgraToRgba(bgra);
    }

    private static byte[] DecodeKyaruETC2(byte[] encodedData, int width, int height, bool hasAlpha)
    {
        int outputSize = checked(width * height * 4);
        var bgra = new byte[outputSize];

        bool ok = hasAlpha
            ? TextureDecoder.DecodeETC2A8(encodedData, width, height, bgra)
            : TextureDecoder.DecodeETC2(encodedData, width, height, bgra);

        if (!ok)
            throw new InvalidDataException(
                $"Kyaru Texture2DDecoder failed to decode {(hasAlpha ? "ETC2_RGBA8" : "ETC2_RGB")}");

        return BgraToRgba(bgra);
    }

    private static byte[] DecodeAstc(byte[] encodedData, int width, int height, FootprintType footprintType, string texName)
    {
        using var source = new MemoryStream(encodedData, writable: false);
        using var destination = new MemoryStream();

        var footprint = Footprint.FromFootprintType(footprintType);
        AstcDecoder.DecompressImage(source, destination, width, height, footprint);
        byte[] rgba32 = destination.ToArray();

        int expected = checked(width * height * 4);
        if (rgba32.Length != expected)
        {
            throw new InvalidDataException(
                $"ASTC decode size mismatch for '{texName}': got {rgba32.Length:N0}, expected {expected:N0}");
        }

        return rgba32;
    }

    private static byte[] EncodeAstc(byte[] rgba32, int width, int height, FootprintType footprintType, string texName)
    {
        int blockWidth = footprintType switch
        {
            FootprintType.Footprint4x4 => 4,
            FootprintType.Footprint6x6 => 6,
            FootprintType.Footprint8x8 => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(footprintType), $"Unsupported ASTC footprint {footprintType}")
        };

        return NativeAstcEncoder.Encode(rgba32, width, height, blockWidth, blockWidth, texName);
    }

    private static byte[] BgraToRgba(byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            rgba[i + 0] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i + 0];
            rgba[i + 3] = bgra[i + 3];
        }
        return rgba;
    }

    public static byte[] DownsampleToGrid(byte[] rgba32, int width, int height, int gridSize)
    {
        var grid = new byte[gridSize * gridSize * 4];

        for (int gy = 0; gy < gridSize; gy++)
        {
            int y0 = (int)((long)gy * height / gridSize);
            int y1 = (int)((long)(gy + 1) * height / gridSize);
            if (y1 <= y0) y1 = y0 + 1;
            y1 = Math.Min(y1, height);

            for (int gx = 0; gx < gridSize; gx++)
            {
                int x0 = (int)((long)gx * width / gridSize);
                int x1 = (int)((long)(gx + 1) * width / gridSize);
                if (x1 <= x0) x1 = x0 + 1;
                x1 = Math.Min(x1, width);

                long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                int count = 0;
                for (int y = y0; y < y1; y++)
                {
                    int rowBase = y * width * 4;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = rowBase + x * 4;
                        sumR += rgba32[i + 0];
                        sumG += rgba32[i + 1];
                        sumB += rgba32[i + 2];
                        sumA += rgba32[i + 3];
                        count++;
                    }
                }

                int gi = (gy * gridSize + gx) * 4;
                grid[gi + 0] = (byte)(sumR / count);
                grid[gi + 1] = (byte)(sumG / count);
                grid[gi + 2] = (byte)(sumB / count);
                grid[gi + 3] = (byte)(sumA / count);
            }
        }

        return grid;
    }

    private const double kAlphaWeight = 0.25;

    public static double Percentile95CellDifference(byte[] gridA, byte[] gridB, int gridSize)
    {
        if (gridA.Length != gridB.Length)
            throw new ArgumentException("grids must be the same size to compare");

        int cellCount = gridSize * gridSize;
        int expected = checked(cellCount * 4);
        if (gridA.Length != expected)
            throw new ArgumentException(
                $"grid length {gridA.Length} doesn't match gridSize {gridSize} (expected {expected})");

        var cellScores = new double[cellCount];
        for (int c = 0; c < cellCount; c++)
        {
            int i = c * 4;
            double dr = Math.Abs(gridA[i + 0] - gridB[i + 0]);
            double dg = Math.Abs(gridA[i + 1] - gridB[i + 1]);
            double db = Math.Abs(gridA[i + 2] - gridB[i + 2]);
            double da = Math.Abs(gridA[i + 3] - gridB[i + 3]);
            cellScores[c] = (dr + dg + db + da * kAlphaWeight) / (3.0 + kAlphaWeight);
        }

        Array.Sort(cellScores);

        int rank = (int)Math.Ceiling(0.95 * cellCount) - 1;
        rank = Math.Clamp(rank, 0, cellCount - 1);
        return cellScores[rank];
    }

    public static byte[] ResampleBilinear(byte[] srcRgba32, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        if (srcWidth == dstWidth && srcHeight == dstHeight)
            return srcRgba32;

        var dst = new byte[dstWidth * dstHeight * 4];

        for (int dy = 0; dy < dstHeight; dy++)
        {
            double sy = (dy + 0.5) * srcHeight / dstHeight - 0.5;
            int y0 = (int)Math.Floor(sy);
            double fy = sy - y0;
            int y0c = Math.Clamp(y0, 0, srcHeight - 1);
            int y1c = Math.Clamp(y0 + 1, 0, srcHeight - 1);

            for (int dx = 0; dx < dstWidth; dx++)
            {
                double sx = (dx + 0.5) * srcWidth / dstWidth - 0.5;
                int x0 = (int)Math.Floor(sx);
                double fx = sx - x0;
                int x0c = Math.Clamp(x0, 0, srcWidth - 1);
                int x1c = Math.Clamp(x0 + 1, 0, srcWidth - 1);

                int i00 = (y0c * srcWidth + x0c) * 4;
                int i10 = (y0c * srcWidth + x1c) * 4;
                int i01 = (y1c * srcWidth + x0c) * 4;
                int i11 = (y1c * srcWidth + x1c) * 4;

                int di = (dy * dstWidth + dx) * 4;
                for (int c = 0; c < 4; c++)
                {
                    double top = srcRgba32[i00 + c] * (1 - fx) + srcRgba32[i10 + c] * fx;
                    double bot = srcRgba32[i01 + c] * (1 - fx) + srcRgba32[i11 + c] * fx;
                    dst[di + c] = (byte)Math.Round(top * (1 - fy) + bot * fy);
                }
            }
        }

        return dst;
    }
}

