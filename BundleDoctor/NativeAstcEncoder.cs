// BundleDoctor/AstcEncoder.cs
//
// Managed bridge to the vendored ARM astc-encoder (astcenc) native ASTC
// encoder (see native/astcenc/astcenc_encode.cpp for the wire protocol and
// build.sh to build it). Replaces AstcSharp's pure-managed AstcEncoder for
// the *encode* direction only - AstcSharp's decoder is untouched (see
// TextureCodec.cs's DecodeAstc) since decode was never the bottleneck here;
// it's a single texture-format walk over already-small blocks, not a
// from-scratch endpoint/weight search.
//
// Follows the exact same subprocess + raw-bytes-over-stdio shape as
// Etc2Encoder.cs so the two vendored native encoders behave identically from
// the managed side: one process per call, binary header + raw pixels in,
// raw compressed blocks out, no temp files.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

internal enum AstcQuality
{
    Fastest,
    Fast,
    Medium,
    Thorough,
    VeryThorough,
    Exhaustive,
}

internal static class NativeAstcEncoder
{
    // Mirrors astcenc.h's ASTCENC_PRE_* presets exactly (see astcenc.h) so a
    // caller picking AstcQuality.Fast gets the same search effort the
    // upstream astcenc CLI's own "-fast" flag would use.
    private static float QualityToPreset(AstcQuality quality) => quality switch
    {
        AstcQuality.Fastest => 0.0f,
        AstcQuality.Fast => 10.0f,
        AstcQuality.Medium => 60.0f,
        AstcQuality.Thorough => 98.0f,
        AstcQuality.VeryThorough => 99.0f,
        AstcQuality.Exhaustive => 100.0f,
        _ => throw new ArgumentOutOfRangeException(nameof(quality))
    };

    /// <summary>
    /// Encodes RGBA32 pixels into raw ASTC blocks (block-row-major, 16 bytes
    /// each) for the given footprint. blockWidth/blockHeight are the ASTC
    /// texel-footprint dimensions (4, 6, or 8 for the formats this project
    /// uses), not pixel counts.
    /// </summary>
    public static byte[] Encode(
        byte[] rgba32, int width, int height, int blockWidth, int blockHeight, string texName,
        AstcQuality quality = AstcQuality.Medium)
    {
        int expectedPixels = checked(width * height);
        int expectedBytes = checked(expectedPixels * 4);
        if (rgba32.Length != expectedBytes)
            throw new InvalidDataException(
                $"ASTC input size mismatch for '{texName}': got {rgba32.Length:N0}, expected {expectedBytes:N0}");

        string encoderPath = FindEncoderPath();
        var psi = new ProcessStartInfo
        {
            FileName = encoderPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException($"failed to start vendored astcenc encoder '{encoderPath}'");

        using (process.StandardInput.BaseStream)
        {
            Span<byte> header = stackalloc byte[20];
            WriteUInt32LE(header[0..4], checked((uint)width));
            WriteUInt32LE(header[4..8], checked((uint)height));
            WriteUInt32LE(header[8..12], checked((uint)blockWidth));
            WriteUInt32LE(header[12..16], checked((uint)blockHeight));
            WriteFloat32LE(header[16..20], QualityToPreset(quality));
            process.StandardInput.BaseStream.Write(header);
            process.StandardInput.BaseStream.Write(rgba32, 0, rgba32.Length);
        }

        // Output is bounded (16 bytes per block), so ReadToEnd is safe. Drain
        // stderr concurrently, same reasoning as Etc2Encoder.Encode: avoids a
        // pipe back-pressure deadlock if astcenc_encode ever logs more than
        // fits in the OS pipe buffer while stdout is also filling up.
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        byte[] encoded = ReadAll(process.StandardOutput.BaseStream);
        process.WaitForExit();
        string stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"vendored astcenc encoder failed for '{texName}' (exit {process.ExitCode}): {stderr.Trim()}");
        }

        int blocksX = (width + blockWidth - 1) / blockWidth;
        int blocksY = (height + blockHeight - 1) / blockHeight;
        int expectedEncoded = checked(blocksX * blocksY * 16);
        if (encoded.Length != expectedEncoded)
        {
            throw new InvalidDataException(
                $"ASTC output size mismatch for '{texName}': got {encoded.Length:N0}, " +
                $"expected {expectedEncoded:N0} for {blockWidth}x{blockHeight}");
        }

        return encoded;
    }

    private static string FindEncoderPath()
    {
        string? env = Environment.GetEnvironmentVariable("ASTCENC_ENCODER");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return Path.GetFullPath(env);

        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "astcenc_encode.exe"
            : "astcenc_encode";

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "native", "astcenc", "build", exeName),
            Path.Combine(AppContext.BaseDirectory, "native", "astcenc", "build", "Release", exeName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "native", "astcenc", "build", exeName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "native", "astcenc", "build", "Release", exeName),
            Path.Combine(Directory.GetCurrentDirectory(), "native", "astcenc", "build", exeName),
            Path.Combine(Directory.GetCurrentDirectory(), "native", "astcenc", "build", "Release", exeName),
        };

        foreach (string candidate in candidates.Select(Path.GetFullPath).Distinct())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "Vendored astcenc encoder was not found. Build native/astcenc with native/astcenc/build.sh " +
            "or set ASTCENC_ENCODER to the astcenc_encode executable path.");
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static void WriteUInt32LE(Span<byte> destination, uint value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
        destination[3] = (byte)(value >> 24);
    }

    private static void WriteFloat32LE(Span<byte> destination, float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        WriteUInt32LE(destination, unchecked((uint)bits));
    }
}
