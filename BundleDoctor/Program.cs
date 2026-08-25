// BundleDoctor/Program.cs
//
// Doctors a Limbus Company DESKTOP asset bundle into an iOS-loadable one.
//   - retargets SerializedFile TargetPlatform -> iOS (9)
//   - converts only these source Texture2D formats:
//       RGB24 (3), RGBA32 (4), DXT1 (10), DXT5 (12), DXT5Crunched (29)
//   - leaves ASTC RGBA 4x4 (48) and ASTC RGBA 6x6 (50) untouched
//   - decodes DXT/DXT5Crunched using Kyaru.Texture2DDecoder (the Unity Texture2D
//     decoder used by AssetStudio), with explicit UnityCrunch unpacking for format 29
//   - re-encodes every converted texture to a configurable output format
//     (default: RGBA32); ASTC 4x4/6x6/8x8 and ETC2 both go through vendored
//     native encoders (astcenc and etcpak respectively - see native/astcenc/
//     and native/etcpak/); AstcSharp is kept only for ASTC decode
//   - moves converted texture data inline and clears m_StreamData so the rewritten
//     Texture2D does not depend on hand-written .resS offsets
//   - fully decompresses compressed input bundles before any asset/texture mutation
//   - if --original is given, restores every Shader object's raw bytes from the
//     original (untouched, correctly-platformed) bundle byte-for-byte, keyed by
//     PathId within each same-named SerializedFile. Shader objects carry
//     platform-specific pre-compiled data this pipeline has no way to
//     re-target - re-serializing them through AssetsTools.NET at all (even
//     untouched) has been the actual source of shader corruption, not the
//     LZ4HC/LZ4 mislabeling this project first suspected. The original bundle
//     is only ever read from, never written to, and is fully discarded
//     (originalManager.UnloadAll()) once every SerializedFile has been
//     processed, before the Write()/Pack()/transcode stage below runs.
//   - materializes AssetsTools.NET replacers with Write(), packs the result with
//     AssetsTools.NET's own Pack() for a structurally correct archive, then
//     UnityFsLz4Transcoder.cs re-encodes every block as genuine standard LZ4 -
//     not LZ4HC, and not just a relabeled LZ4HC stream - see that file for why
//
// NuGet:
//   AssetsTools.NET 3.0.2
//   AssetsTools.NET.Texture 3.0.2 (raw Texture2D data access only)
//   Kyaru.Texture2DDecoder 0.17.1 + Kyaru.Texture2DDecoder.Linux 0.2.0
//   AstcSharp 3.1.0
//   K4os.Compression.LZ4 1.3.8 (real standard/fast LZ4 block encode/decode, used only
//   by UnityFsLz4Transcoder.cs's block transcoding step - see that file)
//
// Usage: BundleDoctor <input.bundle> <output.bundle> [--original original.bundle] [outputFormat] [classdata.tpk]
//
// --original is order-independent and is stripped out of args before the
// existing positional outputFormat/classdata.tpk parsing runs, so it does
// not shift either of those. Omitting it just skips the shader-restore
// pass entirely (logged, not an error) - e.g. when the client couldn't
// resolve a matching original via UnityCacheLocator.
//
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
    // Unity BuildTarget values (from Unity's own tooling / editor source)
    private const int kTargetWindows64 = 19;
    private const int kTargetIOS = 9;

    // Unity Editor version this pipeline is pinned to. Class-database (.tpk)
    // resolution uses this constant instead of the bundle's own serialized
    // af.Metadata.UnityVersion string - that field can be obfuscated/stripped
    // (see the UnityCrunch comment below), and even when present it's whatever
    // version modded/third-party bundles happened to ship with, not necessarily
    // the version this pipeline's TypeTree handling has actually been built
    // and verified against. Update this if/when Limbus Company's engine version
    // changes.
    internal const string kTargetUnityVersion = "6000.3.12f1";

    // TextureFormat values relevant to this pipeline
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

    // Default output format. The workflow can override this with the final CLI argument.
    // Accepted names: RGBA32, ETC2, ETC2_RGB, ETC2_RGBA8, ASTC_RGBA_4x4, ASTC_RGBA_6x6, ASTC_RGBA_8x8.
    private const int kDefaultOutputTextureFormat = kFmtRGBA32;

    private static int Main(string[] args)
    {
        // New reverse-direction pipeline - see TransplantMode.cs's header
        // comment for why this exists alongside (not instead of) the
        // desktop->iOS convert mode below. Dispatched on args[0] so the
        // existing `BundleDoctor <input> <output> ...` invocation (still used
        // by doctor-bundle.yml) is completely unaffected.
        if (args.Length > 0 && string.Equals(args[0], "transplant", StringComparison.OrdinalIgnoreCase))
        {
            return TransplantMode.Run(args[1..]);
        }

        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: BundleDoctor <input.bundle> <output.bundle> [--original original.bundle] [outputFormat] [classdata.tpk]\n" +
                "   or: BundleDoctor transplant <original.bundle> <modded.bundle> <output.bundle> [--threshold N] [--dry-run] [--new-texture-format FMT] [classdata.tpk]");
            return 2;
        }

        // Pull --original <path> out first (order-independent) so the existing
        // positional parsing below for outputFormat/classdata.tpk never has to
        // know about it.
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
                "usage: BundleDoctor <input.bundle> <output.bundle> [--original original.bundle] [outputFormat] [classdata.tpk]");
            return 2;
        }

        string inputPath = positional[0];
        string outputPath = positional[1];

        string? tpkPath = null;
        string outputFormatName = "RGBA32";

        if (positional.Count >= 3)
        {
            // If the third argument is a recognized format, treat it as the output format.
            // Otherwise retain the legacy third-argument classdata.tpk position.
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

        // Always materialize a compressed UnityFS input into a genuinely uncompressed
        // bundle before touching any SerializedFiles or Texture2D data.
        //
        // AssetsTools.NET's normal LZ4 path uses an LZ4BlockStream and transparently
        // decompresses blocks as they are read. That is fine for reading, but it still
        // leaves the working AssetBundleFile backed by compressed bundle data. We want
        // exactly one compression boundary in this pipeline: the final Pack() below.
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
                    $"BundleDoctor-input-{Guid.NewGuid():N}.unity3d");

                // AssetsTools.NET's AssetBundleFile.Unpack() is the canonical way to
                // materialize an LZ4/LZ4HC UnityFS bundle. It writes a new, genuinely
                // uncompressed UnityFS file and does not leave the working bundle backed
                // by the original LZ4BlockStream.
                using (var unpackedStream = File.Create(tempInputUnpackedPath))
                using (var unpackedWriter = new AssetsFileWriter(unpackedStream))
                {
                    loadedInput.file.Unpack(unpackedWriter);
                }

                // We no longer need the original compressed reader. Closing it before
                // reopening the materialized file also prevents accidental reads from
                // the old LZ4 block stream.
                manager.UnloadBundleFile(loadedInput);

                // Every subsequent operation now works against a genuinely uncompressed
                // bundle. No LZ4 block stream remains in the texture/asset read path.
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

        // --- Original-bundle shader restore: load once, index by SerializedFile
        // name -> (PathId -> AssetFileInfo), read-only for the lifetime of the
        // main loop below. Never written to, never packed - only ever a source
        // of raw bytes to copy out of. See this file's header comment.
        AssetsManager? originalManager = null;
        var originalShaderIndex = new Dictionary<string, Dictionary<long, AssetFileInfo>>();
        var originalFileInstances = new Dictionary<string, AssetsFileInstance>();

        if (originalBundlePath != null)
        {
            originalManager = new AssetsManager();
            try
            {
                // Deliberately NOT unpacked/decompressed like the modded input
                // above: this bundle is only ever read from at a byte offset via
                // its own AssetsFileReader, which transparently decompresses
                // through an LZ4BlockStream just fine for reads - the earlier
                // "materialize to a genuinely uncompressed file first" step exists
                // solely because Pack() further down can't work through that
                // stream, and we never Pack() this one.
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
                // --original was explicitly requested - failing to open it should
                // not silently fall back to "no shader restore", since that's
                // exactly the corrupted-shader failure mode this exists to fix.
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
                // Not every non-.resS entry is necessarily a SerializedFile. LZ4
                // decompression can also expose auxiliary files that have no useful
                // AssetsFile representation. Skip those rather than dereferencing a
                // null AssetsFileInstance.
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

            // --- 1. Retarget platform ---------------------------------------------
            uint originalTargetPlatform = af.Metadata.TargetPlatform;
            Console.WriteLine(
                $"[{dirInfo.Name}] TargetPlatform before: {originalTargetPlatform} -> requested {kTargetIOS}");

            // Do this unconditionally rather than only when the source says 19.
            // The caller has already identified this as a SerializedFile we want to
            // doctor, so there is no benefit in leaving a non-iOS platform value in it.
            if (originalTargetPlatform != kTargetIOS)
            {
                af.Metadata.TargetPlatform = kTargetIOS;
                fileTouched = true;
                retargetedFiles++;
            }

            Console.WriteLine(
                $"[{dirInfo.Name}] TargetPlatform after:  {af.Metadata.TargetPlatform}");

            // --- 2. Walk Texture2D objects ------------------------------------------
            foreach (AssetFileInfo info in af.GetAssetsOfType(AssetClassID.Texture2D))
            {
                totalTextures++;
                AssetTypeValueField baseField = manager.GetBaseField(afileInst, info);

                int format = baseField["m_TextureFormat"].AsInt;
                if (!NeedsConversion(format))
                {
                    // Formats 48 (ASTC RGBA 4x4) and 50 (ASTC RGBA 6x6) are already
                    // supported by the target iOS build, so leave them byte-for-byte alone.
                    continue;
                }

                int width = baseField["m_Width"].AsInt;
                int height = baseField["m_Height"].AsInt;
                string texName = baseField["m_Name"].AsString;

                if (width <= 0 || height <= 0)
                    throw new InvalidDataException($"invalid dimensions for '{texName}': {width}x{height}");

                // AssetsTools.NET.Texture is retained only for reliably resolving inline
                // image data vs. streamed .resS data. The actual compressed-texture decoder
                // is Kyaru.Texture2DDecoder, which is the same Unity Texture2D decoder used
                // by AssetStudio and has explicit UnityCrunch handling.
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

                        // Unity TextureFormat.RGBA32 is already RGBA byte order.
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

                // Encode the decoded RGBA32 pixels according to the selected output format.
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

                // --- 3. Write the selected format back into the Texture2D -------------
                baseField["m_TextureFormat"].AsInt = outputTextureFormat;
                baseField["m_MipCount"].AsInt = 1;
                baseField["m_CompleteImageSize"].AsInt = outputData.Length;

                AssetTypeValueField streamData = baseField["m_StreamData"];
                streamData["offset"].AsULong = 0;
                streamData["size"].AsInt = 0;
                streamData["path"].AsString = string.Empty;
                baseField["image data"].AsByteArray = outputData;

                info.SetNewData(baseField); // re-serializes just this object
                convertedCount++;
                fileTouched = true;
            }

            // --- 3. Restore Shader objects byte-for-byte from the original bundle ---
            // Deliberately raw-byte, not field-by-field like the Texture2D loop
            // above: Shader objects hold opaque platform-compiled program data
            // this pipeline has no way to interpret or re-target, and running it
            // through AssetTypeValueField/SetNewData(AssetTypeValueField) at all -
            // even with every field left alone - has been the actual source of
            // shader corruption. Copying the exact original bytes back in
            // sidesteps re-serialization entirely for these objects.
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
                // --original was given but this particular SerializedFile had no
                // same-named counterpart in it - e.g. the modded bundle contains
                // an extra file the original doesn't. Any Shader objects in here
                // are left exactly as the modded upload had them.
                int shaderCountHere = 0;
                foreach (AssetFileInfo _ in af.GetAssetsOfType(AssetClassID.Shader)) shaderCountHere++;
                if (shaderCountHere > 0)
                {
                    Console.WriteLine(
                        $"[{dirInfo.Name}] no matching SerializedFile in original bundle; " +
                        $"{shaderCountHere} shader(s) here left un-restored.");
                }
            }

            // --- 4. Write the modified SerializedFile back into the bundle directory ---
            if (fileTouched)
            {
                dirInfo.SetNewData(af);
                touchedFiles++;
            }
        }

        // The original bundle has served its only purpose (a source of raw Shader
        // bytes for the loop above) - discard it now, before the compress stage,
        // rather than holding it open any longer than necessary.
        if (originalManager != null)
        {
            originalManager.UnloadAll();
            Console.WriteLine(
                $"[original] Discarded. Shaders restored: {shadersRestored}/{totalShaders} " +
                $"({shadersMissingInOriginal} had no match in the original).");
        }

        // IMPORTANT: AssetBundleFile.Pack() streams DataReader.BaseStream directly and
        // does not apply DirectoryInfo.Replacer objects. That means calling Pack()
        // immediately after SetNewData(af) silently discards BOTH the target-platform
        // change and any Texture2D replacements.
        //
        // The write pipeline is three-stage:
        //   1. Write() -> materialize all replacers into a fresh, uncompressed bundle
        //      (this is what actually bakes in the retarget + texture edits).
        //   2. Re-load that materialized (replacer-free) bundle and hand it to
        //      AssetsTools.NET's own Pack(), requesting LZ4. This gets us a
        //      structurally correct archive - real multi-block chunking, node/
        //      directory table, header framing - all genuine AssetsTools.NET code,
        //      none of it hand-rolled. The one thing Pack() gets wrong is the actual
        //      encoder: it always compresses through its HC path regardless of which
        //      AssetBundleCompressionType is requested (LZ4 vs LZ4HC there only
        //      changes the declared compression-type byte, not the encoder that
        //      ran). LZ4 and LZ4HC decode with the same algorithm, but Limbus
        //      Company's own loader is known to choke on genuinely HC-encoded
        //      blocks, so mislabeling HC bytes as flag 2 (as opposed to requesting
        //      LZ4HC and correctly labeling them 3) is not an acceptable fix here -
        //      the bytes themselves need to be real standard LZ4, not just declared
        //      as such.
        //   3. UnityFsLz4Transcoder walks Pack()'s output block-by-block and
        //      transcodes each one to genuine standard/fast LZ4 (LZ4Level.L00_FAST) -
        //      decompress + re-encode per block, in place. It does not rebuild the
        //      container itself; only compressed bytes and each block's declared
        //      size change. See that file's header comment for the full writeup.
        string tempUnpackedPath = Path.Combine(
            Path.GetTempPath(),
            $"BundleDoctor-{Guid.NewGuid():N}.unity3d");
        string tempPackedPath = Path.Combine(
            Path.GetTempPath(),
            $"BundleDoctor-packed-{Guid.NewGuid():N}.unity3d");

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
                    // Requested type is LZ4; Pack() will actually emit HC-encoded
                    // bytes under that label regardless - the transcoder below fixes
                    // that by re-encoding every block for real.
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

        // Final verification: reload the actual output and make sure the serialized
        // files that can be parsed are now targeting iOS. This turns a silent no-op
        // into a workflow failure instead of handing the tweak a Windows-targeted bundle.
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

        // The primary working bundle may be backed by a temporary decompressed file.
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
        // Desktop-only / unsupported formats: decode then re-encode.
        kFmtRGB24 => true,
        kFmtDXT1 => true,
        kFmtDXT5 => true,
        kFmtDXT5Crunched => true,

        // Already-supported / already-converted formats: leave untouched.
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
                // No compression: directly store Unity RGBA32 pixels.
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

    // See TextureCodec.EncodeAstc's header comment: encoding goes through the
    // vendored native astcenc encoder (NativeAstcEncoder.cs) rather than
    // AstcSharp.AstcEncoder.CompressImage now, for speed. outputFormat is
    // unused by the encode itself; kept in the signature since callers above
    // already have it and it's useful context in error messages if this ever
    // needs to report which Unity format it was encoding for.
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
            rgba[dst + 0] = data[src + 0]; // R
            rgba[dst + 1] = data[src + 1]; // G
            rgba[dst + 2] = data[src + 2]; // B
            rgba[dst + 3] = 255;           // A
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
        // Limbus Company is a modern Unity build (Unity 6), so use UnityCrunch explicitly.
        // This avoids relying on the serialized Unity-version metadata, which may be obfuscated.
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
            rgba[i + 0] = bgra[i + 2]; // R <- B
            rgba[i + 1] = bgra[i + 1]; // G <- G
            rgba[i + 2] = bgra[i + 0]; // B <- R
            rgba[i + 3] = bgra[i + 3]; // A <- A
        }
        return rgba;
    }

    private static bool LooksLikeSerializedFile(string name) =>
        !name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) &&
        !name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase);
}
