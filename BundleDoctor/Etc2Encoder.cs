// BundleDoctor/Etc2Encoder.cs
//
// Managed bridge to the vendored wolfpld/etcpak native ETC2 encoder
// (previously google/etc2comp - see native/etcpak/etcpak_encode.cpp for the
// rationale). The native helper consumes the same tiny binary protocol over
// stdin and returns raw ETC2 blocks over stdout as the old etc2comp bridge
// did, so no temporary PNG/KTX files are needed and nothing above this file
// (TextureCodec.cs, Program.cs) had to change.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

internal static class Etc2Encoder
{
    public const int UnityEtc2Rgb = 45;
    public const int UnityEtc2Rgba8 = 47;

    public static byte[] Encode(byte[] rgba32, int width, int height, int outputFormat, string texName)
    {
        if (outputFormat != UnityEtc2Rgb && outputFormat != UnityEtc2Rgba8)
            throw new ArgumentOutOfRangeException(nameof(outputFormat), "Expected Unity ETC2 format 45 or 47.");

        int expectedPixels = checked(width * height);
        int expectedBytes = checked(expectedPixels * 4);
        if (rgba32.Length != expectedBytes)
            throw new InvalidDataException(
                $"ETC2 input size mismatch for '{texName}': got {rgba32.Length:N0}, expected {expectedBytes:N0}");

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
            throw new InvalidOperationException($"failed to start vendored etcpak ETC2 encoder '{encoderPath}'");

        // etcpak's vendored ProcessRGB compressor reads its source buffer as
        // BGRA (src[0]=B, src[1]=G, src[2]=R - see e.g. Average()/Planar() in
        // ProcessRGB.cpp), not RGBA despite the wire protocol's own "raw
        // RGBA8" naming. Every other codec path in TextureCodec.cs converts
        // BGRA->RGBA on decode (BgraToRgba); this is the mirror-image
        // RGBA->BGRA conversion needed before *encoding*, or every ETC2
        // texture comes out with red and blue swapped.
        byte[] bgra32 = RgbaToBgra(rgba32);

        using (process.StandardInput.BaseStream)
        {
            Span<byte> header = stackalloc byte[12];
            WriteUInt32LE(header[0..4], checked((uint)width));
            WriteUInt32LE(header[4..8], checked((uint)height));
            WriteUInt32LE(header[8..12], checked((uint)outputFormat));
            process.StandardInput.BaseStream.Write(header);
            process.StandardInput.BaseStream.Write(bgra32, 0, bgra32.Length);
        }

        // Output is bounded by 16 bytes per 4x4 block, so ReadToEnd is safe
        // for the raw encoded payload. STDERR is also drained before WaitForExit
        // to prevent pipe back-pressure from deadlocking the process.
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        byte[] encoded = ReadAll(process.StandardOutput.BaseStream);
        process.WaitForExit();
        string stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"vendored etcpak ETC2 encoder failed for '{texName}' (exit {process.ExitCode}): {stderr.Trim()}");
        }

        // etcpak_encode pads non-block-aligned images internally before
        // encoding (see its header comment) but still emits a full block for
        // every 4x4 cell of the padded (ceil(width/4) x ceil(height/4)) grid,
        // same as etc2comp did via its own extendedWidth/extendedHeight - so
        // this size check needed no changes when swapping encoders.
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        int bytesPerBlock = outputFormat == UnityEtc2Rgb ? 8 : 16;
        int expectedEncoded = checked(blocksX * blocksY * bytesPerBlock);
        if (encoded.Length != expectedEncoded)
        {
            throw new InvalidDataException(
                $"ETC2 output size mismatch for '{texName}': got {encoded.Length:N0}, " +
                $"expected {expectedEncoded:N0} for {TextureName(outputFormat)}");
        }

        return encoded;
    }

    private static string FindEncoderPath()
    {
        string? env = Environment.GetEnvironmentVariable("ETCPAK_ENCODER");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return Path.GetFullPath(env);

        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "etcpak_encode.exe"
            : "etcpak_encode";

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "native", "etcpak", "build", exeName),
            Path.Combine(AppContext.BaseDirectory, "native", "etcpak", "build", "Release", exeName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "native", "etcpak", "build", exeName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "native", "etcpak", "build", "Release", exeName),
            Path.Combine(Directory.GetCurrentDirectory(), "native", "etcpak", "build", exeName),
            Path.Combine(Directory.GetCurrentDirectory(), "native", "etcpak", "build", "Release", exeName),
        };

        foreach (string candidate in candidates.Select(Path.GetFullPath).Distinct())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "Vendored etcpak ETC2 encoder was not found. Build native/etcpak with native/etcpak/build.sh " +
            "or set ETCPAK_ENCODER to the etcpak_encode executable path.");
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] RgbaToBgra(byte[] rgba)
    {
        var bgra = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            bgra[i + 0] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i + 0];
            bgra[i + 3] = rgba[i + 3];
        }
        return bgra;
    }

    private static void WriteUInt32LE(Span<byte> destination, uint value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
        destination[3] = (byte)(value >> 24);
    }

    private static string TextureName(int format) => format switch
    {
        UnityEtc2Rgb => "ETC2_RGB",
        UnityEtc2Rgba8 => "ETC2_RGBA8",
        _ => $"Format{format}"
    };
}
