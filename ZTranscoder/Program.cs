
using System;
using System.Collections.Generic;
using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using AstcSharp;
using AstcSharp.Core;
using Texture2DDecoder;

internal static class Program
{

    private const int kTargetWindows64 = 19;
    private const int kTargetIOS = 9;

    internal const string kTargetUnityVersion = "6000.3.12f1";

    private const int kFmtRGB24 = 3;
    private const int kFmtRGBA32 = 4;
    private const int kFmtDXT1 = 10;
    private const int kFmtDXT5 = 12;
    private const int kFmtDXT5Crunched = 29;
    private const int kFmtETC2_RGB = 45;
    private const int kFmtETC2_RGBA8 = 47;
    private const int kFmtASTC_RGBA_4x4 = 48;
    private const int kFmtASTC_RGBA_6x6 = 50;
    private const int kFmtASTC_RGBA_8x8 = 51;

    private const int kDefaultOutputTextureFormat = kFmtRGBA32;

    private static int Main(string[] args)
    {

        if (args.Length > 0 && string.Equals(args[0], "transplant", StringComparison.OrdinalIgnoreCase))
        {
            return TransplantMode.Run(args[1..]);
        }

        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: ZTranscoder <input.bundle> <output.bundle> [--original original.bundle] [outputFormat] [classdata.tpk]\n" +
                "   or: ZTranscoder transplant <original.bundle> <modded.bundle> <output.bundle> [--threshold N] [--dry-run] [--new-texture-format FMT] [classdata.tpk]");
            return 2;
        }

        var positional = new List<string>(args);
        string? originalBundlePath = null;
        for (int i = 0; i < positional.Count - 1; i++)
        {
            if (string.Equals(positional[i], "--original", StringComparison.OrdinalIgnoreCase))
            {
                originalBundlePath = positional[i + 1];
                positional.RemoveRange(i, 2);
                break;
            }
        }

        if (positional.Count < 2)
        {
            Console.Error.WriteLine(
                "usage: ZTranscoder <input.bundle> <output.bundle> [--original original.bundle] [outputFormat] [classdata.tpk]");
            return 2;
        }

        string inputPath = positional[0];
        string outputPath = positional[1];

        string? tpkPath = null;
        string outputFormatName = "RGBA32";

        if (positional.Count >= 3)
        {

            if (IsOutputTextureFormatName(positional[2]))
                outputFormatName = positional[2];
            else
                tpkPath = positional[2];
        }

        if (positional.Count >= 4)
            outputFormatName = positional[3];

        int outputTextureFormat =
            ParseOutputTextureFormat(outputFormatName, kDefaultOutputTextureFormat);

        Console.WriteLine(
            $"[config] OutputTextureFormat={FormatName(outputTextureFormat)} ({outputTextureFormat})");

        var manager = new AssetsManager();
        if (tpkPath != null)
        {
            manager.LoadClassPackage(tpkPath);
        }

        string? tempInputUnpackedPath = null;
        BundleFileInstance bunInst;
        AssetBundleFile bundle;

        try
        {
            BundleFileInstance loadedInput =
                manager.LoadBundleFile(inputPath, unpackIfPacked: false);

            AssetBundleCompressionType inputCompression =
                loadedInput.file.GetCompressionType();

            Console.WriteLine(
                $"[bundle] Input compression: {inputCompression}");

            if (inputCompression != AssetBundleCompressionType.None)
            {
                tempInputUnpackedPath = Path.Combine(
                    Path.GetTempPath(),
                    $"ZTranscoder-input-{Guid.NewGuid():N}.unity3d");

                using (var unpackedStream = File.Create(tempInputUnpackedPath))
                using (var unpackedWriter = new AssetsFileWriter(unpackedStream))
                {
                    loadedInput.file.Unpack(unpackedWriter);
                }

                manager.UnloadBundleFile(loadedInput);

                bunInst = manager.LoadBundleFile(
                    tempInputUnpackedPath,
                    unpackIfPacked: false);

                bundle = bunInst.file;

                AssetBundleCompressionType workingCompression =
                    bundle.GetCompressionType();

                if (workingCompression != AssetBundleCompressionType.None ||
                    bundle.DataIsCompressed)
                {
                    throw new InvalidDataException(
                        $"failed to fully decompress input bundle; " +
                        $"working compression={workingCompression}, " +
                        $"DataIsCompressed={bundle.DataIsCompressed}");
                }

                Console.WriteLine(
                    "[bundle] Input was fully decompressed to an uncompressed working bundle.");
            }
            else
            {
                bunInst = loadedInput;
                bundle = bunInst.file;
                Console.WriteLine(
                    "[bundle] Input is already uncompressed; no decompression step required.");
            }
        }
        catch
        {
            if (tempInputUnpackedPath != null)
            {
                try { File.Delete(tempInputUnpackedPath); } catch { }
            }

            manager.UnloadAll();
            throw;
        }

        int convertedCount = 0;
        int totalTextures = 0;
        int touchedFiles = 0;
        int retargetedFiles = 0;
        int totalShaders = 0;
        int shadersRestored = 0;
        int shadersMissingInOriginal = 0;

        AssetsManager? originalManager = null;
        var originalShaderIndex = new Dictionary<string, Dictionary<long, AssetFileInfo>>();
        var originalFileInstances = new Dictionary<string, AssetsFileInstance>();

        if (originalBundlePath != null)
        {
            originalManager = new AssetsManager();
            try
            {

                BundleFileInstance originalBunInst =
                    originalManager.LoadBundleFile(originalBundlePath, unpackIfPacked: false);

                for (int i = 0; i < originalBunInst.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
                {
                    var origDirInfo = originalBunInst.file.BlockAndDirInfo.DirectoryInfos[i];
                    if (!LooksLikeSerializedFile(origDirInfo.Name))
                        continue;

                    AssetsFileInstance? origAfileInst;
                    try
                    {
                        origAfileInst = originalManager.LoadAssetsFileFromBundle(originalBunInst, i, loadDeps: false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"[original:{origDirInfo.Name}] skipped: LoadAssetsFileFromBundle threw {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }

                    if (origAfileInst == null || origAfileInst.file == null)
                        continue;

                    var pathIdToInfo = new Dictionary<long, AssetFileInfo>();
                    foreach (AssetFileInfo shaderInfo in origAfileInst.file.GetAssetsOfType(AssetClassID.Shader))
                    {
                        pathIdToInfo[shaderInfo.PathId] = shaderInfo;
                    }

                    originalFileInstances[origDirInfo.Name] = origAfileInst;
                    originalShaderIndex[origDirInfo.Name] = pathIdToInfo;
                }

                Console.WriteLine(
                    $"[original] Loaded '{originalBundlePath}': " +
                    $"{originalShaderIndex.Count} SerializedFile(s) indexed for shader restore.");
            }
            catch (Exception ex)
            {

                originalManager.UnloadAll();
                throw new InvalidDataException(
                    $"--original was given ('{originalBundlePath}') but could not be loaded: {ex.Message}", ex);
            }
        }
        else
        {
            Console.WriteLine("[original] No --original given; skipping shader restore pass.");
        }

        for (int dirIndex = 0; dirIndex < bundle.BlockAndDirInfo.DirectoryInfos.Count; dirIndex++)
        {
            var dirInfo = bundle.BlockAndDirInfo.DirectoryInfos[dirIndex];
            if (!LooksLikeSerializedFile(dirInfo.Name))
                continue;

            AssetsFileInstance? afileInst = null;
            try
            {
                afileInst = manager.LoadAssetsFileFromBundle(bunInst, dirIndex, loadDeps: false);
            }
            catch (Exception ex)
            {

                Console.WriteLine(
                    $"[{dirInfo.Name}] skipped: LoadAssetsFileFromBundle threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (afileInst == null || afileInst.file == null)
            {
                Console.WriteLine(
                    $"[{dirInfo.Name}] skipped: entry is not a readable SerializedFile (AssetsFileInstance was null).");
                continue;
            }

            AssetsFile af = afileInst.file;

            if (tpkPath != null)
            {
                manager.LoadClassDatabaseFromPackage(kTargetUnityVersion);
            }

            bool fileTouched = false;

            uint originalTargetPlatform = af.Metadata.TargetPlatform;
            Console.WriteLine(
                $"[{dirInfo.Name}] TargetPlatform before: {originalTargetPlatform} -> requested {kTargetIOS}");

            if (originalTargetPlatform != kTargetIOS)
            {
                af.Metadata.TargetPlatform = kTargetIOS;
                fileTouched = true;
                retargetedFiles++;
            }

            Console.WriteLine(
                $"[{dirInfo.Name}] TargetPlatform after:  {af.Metadata.TargetPlatform}");

            foreach (AssetFileInfo info in af.GetAssetsOfType(AssetClassID.Texture2D))
            {
                totalTextures++;
                AssetTypeValueField baseField = manager.GetBaseField(afileInst, info);

                int format = baseField["m_TextureFormat"].AsInt;
                if (!NeedsConversion(format))
                {

                    continue;
                }

                int width = baseField["m_Width"].AsInt;
                int height = baseField["m_Height"].AsInt;
                string texName = baseField["m_Name"].AsString;

                if (width <= 0 || height <= 0)
                    throw new InvalidDataException($"invalid dimensions for '{texName}': {width}x{height}");

                TextureFile tf = TextureFile.ReadTextureFile(baseField);
                byte[] encodedData = tf.FillPictureData(afileInst)
                    ?? throw new InvalidDataException($"could not load texture data for '{texName}'");

                byte[] rgba32;
                switch (format)
                {
                    case kFmtRGB24:
                        rgba32 = DecodeRGB24(encodedData, width, height);
                        break;

                    case kFmtRGBA32:
                        int expectedRgbaBytes = checked(width * height * 4);
                        if (encodedData.Length < expectedRgbaBytes)
                        {
                            throw new InvalidDataException(
                                $"RGBA32 data too small for '{texName}': got {encodedData.Length}, " +
                                $"expected at least {expectedRgbaBytes}");
                        }

                        rgba32 = new byte[expectedRgbaBytes];
                        Buffer.BlockCopy(encodedData, 0, rgba32, 0, expectedRgbaBytes);
                        break;

                    case kFmtDXT1:
                        rgba32 = DecodeKyaruDXT(encodedData, width, height, isDxt5: false);
                        break;

                    case kFmtDXT5:
                        rgba32 = DecodeKyaruDXT(encodedData, width, height, isDxt5: true);
                        break;

                    case kFmtDXT5Crunched:
                        rgba32 = DecodeKyaruDXT5Crunched(encodedData, width, height);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"conversion dispatch missing for texture format {format} ('{texName}')");
                }

                int expectedDecodedSize = checked(width * height * 4);
                if (rgba32.Length != expectedDecodedSize)
                {
                    throw new InvalidDataException(
                        $"decoded RGBA size mismatch for '{texName}' (format {format}): " +
                        $"got {rgba32.Length}, expected {expectedDecodedSize}");
                }

                byte[] outputData = EncodeOutputTexture(
                    rgba32,
                    width,
                    height,
                    outputTextureFormat,
                    texName);

                Console.WriteLine(
                    $"[Texture] '{texName}' {width}x{height}: format {format} -> " +
                    $"{FormatName(outputTextureFormat)} ({outputTextureFormat}), " +
                    $"{encodedData.Length:N0} -> {outputData.Length:N0} bytes");

                baseField["m_TextureFormat"].AsInt = outputTextureFormat;
                baseField["m_MipCount"].AsInt = 1;
                baseField["m_CompleteImageSize"].AsInt = outputData.Length;

                AssetTypeValueField streamData = baseField["m_StreamData"];
                streamData["offset"].AsULong = 0;
                streamData["size"].AsInt = 0;
                streamData["path"].AsString = string.Empty;
                baseField["image data"].AsByteArray = outputData;

                info.SetNewData(baseField);
                convertedCount++;
                fileTouched = true;
            }

            if (originalShaderIndex.TryGetValue(dirInfo.Name, out Dictionary<long, AssetFileInfo>? origPathIdToInfo) &&
                originalFileInstances.TryGetValue(dirInfo.Name, out AssetsFileInstance? origAfileInst))
            {
                foreach (AssetFileInfo shaderInfo in af.GetAssetsOfType(AssetClassID.Shader))
                {
                    totalShaders++;

                    if (!origPathIdToInfo.TryGetValue(shaderInfo.PathId, out AssetFileInfo? origInfo))
                    {
                        shadersMissingInOriginal++;
                        Console.WriteLine(
                            $"[{dirInfo.Name}] shader PathId {shaderInfo.PathId} not found in original bundle; " +
                            "leaving this object's bytes as-is.");
                        continue;
                    }

                    AssetsFileReader origReader = origAfileInst.file.Reader;
                    long origOffset = origInfo.GetAbsoluteByteOffset(origAfileInst.file);
                    origReader.Position = origOffset;
                    byte[] rawShaderBytes = origReader.ReadBytes((int)origInfo.ByteSize);

                    shaderInfo.SetNewData(rawShaderBytes);
                    shadersRestored++;
                    fileTouched = true;
                }
            }
            else if (originalBundlePath != null)
            {

                int shaderCountHere = 0;
                foreach (AssetFileInfo _ in af.GetAssetsOfType(AssetClassID.Shader)) shaderCountHere++;
                if (shaderCountHere > 0)
                {
                    Console.WriteLine(
                        $"[{dirInfo.Name}] no matching SerializedFile in original bundle; " +
                        $"{shaderCountHere} shader(s) here left un-restored.");
                }
            }

            if (fileTouched)
            {
                dirInfo.SetNewData(af);
                touchedFiles++;
            }
        }

        if (originalManager != null)
        {
            originalManager.UnloadAll();
            Console.WriteLine(
                $"[original] Discarded. Shaders restored: {shadersRestored}/{totalShaders} " +
                $"({shadersMissingInOriginal} had no match in the original).");
        }

        string tempUnpackedPath = Path.Combine(
            Path.GetTempPath(),
            $"ZTranscoder-{Guid.NewGuid():N}.unity3d");
        string tempPackedPath = Path.Combine(
            Path.GetTempPath(),
            $"ZTranscoder-packed-{Guid.NewGuid():N}.unity3d");

        try
        {
            using (var tempStream = File.Create(tempUnpackedPath))
            using (var tempWriter = new AssetsFileWriter(tempStream))
            {
                bundle.Write(tempWriter, 0);
            }

            var packManager = new AssetsManager();
            try
            {
                BundleFileInstance materializedInst =
                    packManager.LoadBundleFile(tempUnpackedPath, unpackIfPacked: false);

                using (var packedStream = File.Create(tempPackedPath))
                using (var packedWriter = new AssetsFileWriter(packedStream))
                {

                    materializedInst.file.Pack(packedWriter, AssetBundleCompressionType.LZ4);
                }
            }
            finally
            {
                packManager.UnloadAll();
            }

            byte[] packedBytes = File.ReadAllBytes(tempPackedPath);
            byte[] forcedLz4Bytes = UnityFsLz4Transcoder.ForceStandardLz4(packedBytes);
            File.WriteAllBytes(outputPath, forcedLz4Bytes);
        }
        finally
        {
            try { File.Delete(tempUnpackedPath); } catch { }
            try { File.Delete(tempPackedPath); } catch { }
        }

        var verifyManager = new AssetsManager();
        try
        {
            BundleFileInstance verifyBundle = verifyManager.LoadBundleFile(outputPath, unpackIfPacked: true);
            int verifiedSerializedFiles = 0;
            int verifiedTextures = 0;
            int remainingDesktopTextureFormats = 0;

            for (int i = 0; i < verifyBundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                var verifyDir = verifyBundle.file.BlockAndDirInfo.DirectoryInfos[i];
                if (!LooksLikeSerializedFile(verifyDir.Name))
                    continue;

                try
                {
                    AssetsFileInstance verifyFile = verifyManager.LoadAssetsFileFromBundle(
                        verifyBundle, i, loadDeps: false);

                    uint target = verifyFile.file.Metadata.TargetPlatform;
                    Console.WriteLine(
                        $"[verify] {verifyDir.Name}: TargetPlatform={target}");

                    verifiedSerializedFiles++;
                    if (target != kTargetIOS)
                    {
                        Console.Error.WriteLine(
                            $"ERROR: {verifyDir.Name} still targets platform {target}; expected {kTargetIOS}.");
                        return 1;
                    }

                    foreach (AssetFileInfo verifyInfo in verifyFile.file.GetAssetsOfType(AssetClassID.Texture2D))
                    {
                        verifiedTextures++;
                        AssetTypeValueField verifyBase = verifyManager.GetBaseField(verifyFile, verifyInfo);
                        int verifyFormat = verifyBase["m_TextureFormat"].AsInt;
                        if (NeedsConversion(verifyFormat))
                            remainingDesktopTextureFormats++;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"ERROR: could not verify serialized file '{verifyDir.Name}': {ex}");
                    return 1;
                }
            }

            if (verifiedSerializedFiles == 0)
            {
                Console.Error.WriteLine("ERROR: output bundle contains no verifiable SerializedFiles.");
                return 1;
            }

            if (remainingDesktopTextureFormats != 0)
            {
                Console.Error.WriteLine(
                    $"ERROR: output still contains {remainingDesktopTextureFormats} texture(s) " +
                    "using a format that this doctor is supposed to convert.");
                return 1;
            }

            Console.WriteLine(
                $"[verify] SerializedFiles={verifiedSerializedFiles}, Texture2D={verifiedTextures}, " +
                "all converted-source formats removed.");
        }
        finally
        {
            verifyManager.UnloadAll();
        }

        manager.UnloadAll();
        if (tempInputUnpackedPath != null)
        {
            try { File.Delete(tempInputUnpackedPath); } catch { }
        }

        Console.WriteLine(
            $"Converted {convertedCount}/{totalTextures} textures across {touchedFiles} " +
            $"serialized file(s); retargeted {retargetedFiles} file(s); restored " +
            $"{shadersRestored}/{totalShaders} shader(s) from original. Wrote and verified {outputPath}.");
        return 0;
    }

    private static bool NeedsConversion(int format) => format switch
    {

        kFmtRGB24 => true,
        kFmtDXT1 => true,
        kFmtDXT5 => true,
        kFmtDXT5Crunched => true,

        kFmtETC2_RGB => false,
        kFmtETC2_RGBA8 => false,
        kFmtASTC_RGBA_4x4 => false,
        kFmtASTC_RGBA_6x6 => false,
        kFmtASTC_RGBA_8x8 => false,
        kFmtRGBA32 => false,

        _ => throw new NotSupportedException(
            $"texture format {format} has no conversion rule; failing closed rather than guessing")
    };

    private static bool IsOutputTextureFormatName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Trim().ToUpperInvariant() switch
        {
            "RGBA32" => true,
            "ETC2" => true,
            "ETC2_RGB" => true,
            "ETC2_RGBA8" => true,
            "ASTC_RGBA_4X4" => true,
            "ASTC_RGBA_6X6" => true,
            "ASTC_8X8" => true,
            "ASTC_RGBA_8X8" => true,
            "ASTC8X8" => true,
            "3" => true,
            "4" => true,
            "45" => true,
            "47" => true,
            "48" => true,
            "50" => true,
            "51" => true,
            _ => false
        };
    }

    private static int ParseOutputTextureFormat(string value, int defaultFormat)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultFormat;

        return value.Trim().ToUpperInvariant() switch
        {
            "RGBA32" => kFmtRGBA32,
            "ETC2_RGB" => kFmtETC2_RGB,
            "ETC2_RGBA8" => kFmtETC2_RGBA8,
            "ASTC_RGBA_4X4" => kFmtASTC_RGBA_4x4,
            "ASTC_RGBA_6X6" => kFmtASTC_RGBA_6x6,
            "ASTC_8X8" => kFmtASTC_RGBA_8x8,
            "ASTC_RGBA_8X8" => kFmtASTC_RGBA_8x8,
            "ASTC8X8" => kFmtASTC_RGBA_8x8,
            "ETC2" => kFmtETC2_RGBA8,
            "3" => kFmtRGB24,
            "4" => kFmtRGBA32,
            "45" => kFmtETC2_RGB,
            "47" => kFmtETC2_RGBA8,
            "48" => kFmtASTC_RGBA_4x4,
            "50" => kFmtASTC_RGBA_6x6,
            "51" => kFmtASTC_RGBA_8x8,
            _ => throw new ArgumentException(
                $"Unknown output texture format '{value}'. " +
                "Use RGBA32, ETC2, ETC2_RGB, ETC2_RGBA8, ASTC_RGBA_4x4, ASTC_RGBA_6x6, or ASTC_RGBA_8x8.")
        };
    }

    private static string FormatName(int format) => format switch
    {
        kFmtRGBA32 => "RGBA32",
        kFmtETC2_RGB => "ETC2_RGB",
        kFmtETC2_RGBA8 => "ETC2_RGBA8",
        kFmtASTC_RGBA_4x4 => "ASTC_RGBA_4x4",
        kFmtASTC_RGBA_6x6 => "ASTC_RGBA_6x6",
        kFmtASTC_RGBA_8x8 => "ASTC_RGBA_8x8",
        _ => $"Format{format}"
    };

    private static byte[] EncodeOutputTexture(
        byte[] rgba32,
        int width,
        int height,
        int outputFormat,
        string texName)
    {
        switch (outputFormat)
        {
            case kFmtRGBA32:

                return rgba32;

            case kFmtASTC_RGBA_4x4:
                return EncodeAstc(
                    rgba32,
                    width,
                    height,
                    FootprintType.Footprint4x4,
                    outputFormat,
                    texName);

            case kFmtASTC_RGBA_6x6:
                return EncodeAstc(
                    rgba32,
                    width,
                    height,
                    FootprintType.Footprint6x6,
                    outputFormat,
                    texName);

            case kFmtASTC_RGBA_8x8:
                return EncodeAstc(
                    rgba32,
                    width,
                    height,
                    FootprintType.Footprint8x8,
                    outputFormat,
                    texName);

            case kFmtETC2_RGB:
            case kFmtETC2_RGBA8:
                return Etc2Encoder.Encode(rgba32, width, height, outputFormat, texName);

            default:
                throw new NotSupportedException(
                    $"Output texture format {outputFormat} is not implemented.");
        }
    }

    private static byte[] EncodeAstc(
        byte[] rgba32,
        int width,
        int height,
        FootprintType footprintType,
        int outputFormat,
        string texName)
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
        {
            throw new InvalidDataException(
                $"Kyaru Texture2DDecoder failed to decode {(isDxt5 ? "DXT5" : "DXT1")}");
        }

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

    private static byte[] EncodeAstc6x6(byte[] rgba32, int width, int height)
    {
        return NativeAstcEncoder.Encode(rgba32, width, height, blockWidth: 6, blockHeight: 6, texName: "<EncodeAstc6x6>");
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

    private static bool LooksLikeSerializedFile(string name) =>
        !name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) &&
        !name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase);
}

