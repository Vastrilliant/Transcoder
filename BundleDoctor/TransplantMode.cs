// BundleDoctor/TransplantMode.cs
//
// `BundleDoctor transplant <original.bundle> <modded.bundle> <output.bundle> [options]`
//
// Why this exists (see Program.cs's header comment for the mode this
// replaces): decoding every desktop-authored asset and re-encoding/retargeting
// the whole modded bundle turned out to be unsustainable once Material assets
// were confirmed to also carry platform-specific data, on top of the Shader
// problem Program.cs's --original restore pass already worked around. Rather
// than chase every asset type that turns out to be platform-sensitive, this
// mode never touches the modded bundle's Shaders or Materials AT ALL: it
// starts from the ORIGINAL bundle (already correctly built and platformed for
// iOS) and only ever surgically overwrites the two asset kinds we actually
// want from the mod:
//
//   - Sprite      : raw byte-for-byte transplant. Sprite objects (rect, pivot,
//                   border, the PPtr to their backing Texture2D, physics
//                   outline, etc.) carry no platform-compiled data the way
//                   Shader/Material do, so an untouched byte copy from modded
//                   into original is safe whenever a Sprite's PathId either
//                   differs in bytes from its original counterpart, or has no
//                   counterpart in original at all.
//
//   - Texture2D   : NOT a raw byte copy - the modded bundle's Texture2D
//                   objects are desktop-formatted (RGB24/DXT1/DXT5/
//                   DXT5Crunched typically) and the original's are iOS-
//                   formatted (typically ASTC 4x4/6x6, sometimes ETC2). Every
//                   Texture2D that exists in both is decoded on both sides to
//                   plain RGBA32 and compared with TextureCodec's dimension-
//                   agnostic downsample-and-diff (see that file) - encoding
//                   differences and resolution differences alone should not
//                   trigger a re-encode, only an actual difference in what's
//                   painted on the texture should. Only textures that come
//                   back "changed" pay the decode+resample+re-encode cost;
//                   everything else is left completely byte-for-byte alone.
//                   This is the resource saving the mod author asked for -
//                   hundreds of material-referenced textures no longer all
//                   get re-encoded on every doctor run, only the handful that
//                   actually changed.
//
// Matching key: PathId within same-named SerializedFile, exactly like
// Program.cs's existing Shader-restore pass already relies on (same build
// pipeline / same underlying asset database on both platforms -> deterministic
// PathIds across the desktop and iOS builds of the same content). If that
// assumption ever stops holding for a given game/project, this whole matching
// strategy needs to change to name-based matching instead - PathId collisions
// are checked for and logged, never silently overwritten.
//
// Whole SerializedFiles present in only one bundle are logged and skipped -
// inserting an entirely new SerializedFile into the bundle's own directory
// table is a bigger structural change than this pass makes and isn't
// implemented here.
//
using System;
using System.Collections.Generic;
using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

internal static class TransplantMode
{
    // Downsample grid used for the dimension-agnostic Texture2D content
    // compare. 48x48 is generous enough to catch a recolored region or a
    // swapped costume without being so fine-grained that ASTC/DXT block-
    // rounding noise starts to dominate the score.
    private const int DefaultGridSize = 48;

    // 95th-percentile cell-difference threshold (0-255 scale, see
    // TextureCodec.Percentile95CellDifference) above which a texture is
    // treated as genuinely changed rather than just re-compressed/re-sized
    // codec noise. This scorer looks at the top 5% of grid cells rather than
    // the whole-grid mean, so it stays low for uniform cross-codec
    // quantization noise and only spikes when a real edit clusters heavy
    // differences in a region of the texture. Deliberately exposed via
    // --threshold rather than hardcoded, since the right value depends on
    // how aggressively the two builds' compressors round color - run once
    // with --dry-run and read the logged score for every texture before
    // trusting a threshold on a real transplant. This default is a starting
    // point, not carried over from the old mean-based scorer - the two
    // scorers are on different scales, so recalibrate against your own
    // --dry-run output rather than assuming 4.0 still means the same thing.
    private const double DefaultThreshold = 4.0;

    private const int DefaultNewTextureFormat = TextureCodec.FmtASTC_RGBA_4x4;

    private sealed class Options
    {
        public string OriginalPath = "";
        public string ModdedPath = "";
        public string OutputPath = "";
        public string? TpkPath;
        public double Threshold = DefaultThreshold;
        public bool DryRun;
        public int NewTextureFormat = DefaultNewTextureFormat;
    }

    public static int Run(string[] modeArgs)
    {
        Options? opt = ParseArgs(modeArgs);
        if (opt == null)
        {
            Console.Error.WriteLine(
                "usage: BundleDoctor transplant <original.bundle> <modded.bundle> <output.bundle> " +
                "[--threshold N] [--dry-run] [--new-texture-format FMT] [classdata.tpk]");
            return 2;
        }

        Console.WriteLine(
            $"[config] threshold={opt.Threshold:F2} dry-run={opt.DryRun} " +
            $"new-texture-format={TextureCodec.FormatName(opt.NewTextureFormat)}");

        var originalManager = new AssetsManager();
        var moddedManager = new AssetsManager();

        if (opt.TpkPath != null)
        {
            originalManager.LoadClassPackage(opt.TpkPath);
            moddedManager.LoadClassPackage(opt.TpkPath);
        }

        string? tempOriginalUnpacked = null;
        string? tempModdedUnpacked = null;

        int spritesOverridden = 0, spritesAdded = 0, spritesUnchanged = 0;
        int texturesReencoded = 0, texturesUnchanged = 0, texturesSkippedErrors = 0;
        int texturesAddedNew = 0;
        int touchedFiles = 0;

        try
        {
            BundleFileInstance originalBunInst = LoadFullyUnpacked(
                originalManager, opt.OriginalPath, "BundleDoctor-transplant-orig", out tempOriginalUnpacked);
            BundleFileInstance moddedBunInst = LoadFullyUnpacked(
                moddedManager, opt.ModdedPath, "BundleDoctor-transplant-modded", out tempModdedUnpacked);

            // Index the modded bundle's SerializedFiles by name up front so the
            // main loop below (which walks the ORIGINAL's directory, since
            // that's the file we're rebuilding) can look each one up.
            var moddedIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < moddedBunInst.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                string name = moddedBunInst.file.BlockAndDirInfo.DirectoryInfos[i].Name;
                if (LooksLikeSerializedFile(name))
                    moddedIndexByName[name] = i;
            }

            var originalNames = new HashSet<string>(StringComparer.Ordinal);

            for (int dirIndex = 0; dirIndex < originalBunInst.file.BlockAndDirInfo.DirectoryInfos.Count; dirIndex++)
            {
                var origDirInfo = originalBunInst.file.BlockAndDirInfo.DirectoryInfos[dirIndex];
                if (!LooksLikeSerializedFile(origDirInfo.Name))
                    continue;

                originalNames.Add(origDirInfo.Name);

                if (!moddedIndexByName.TryGetValue(origDirInfo.Name, out int moddedDirIndex))
                {
                    Console.WriteLine(
                        $"[{origDirInfo.Name}] no counterpart in modded bundle; left untouched.");
                    continue;
                }

                AssetsFileInstance origAfileInst;
                AssetsFileInstance moddedAfileInst;
                try
                {
                    origAfileInst = originalManager.LoadAssetsFileFromBundle(originalBunInst, dirIndex, loadDeps: false);
                    moddedAfileInst = moddedManager.LoadAssetsFileFromBundle(moddedBunInst, moddedDirIndex, loadDeps: false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[{origDirInfo.Name}] skipped: could not load as a SerializedFile on one side ({ex.GetType().Name}: {ex.Message}).");
                    continue;
                }

                if (origAfileInst?.file == null || moddedAfileInst?.file == null)
                {
                    Console.WriteLine($"[{origDirInfo.Name}] skipped: not a readable SerializedFile on one side.");
                    continue;
                }

                if (opt.TpkPath != null)
                {
                    // Pinned to Program.kTargetUnityVersion rather than each bundle's own
                    // af.Metadata.UnityVersion - see that constant's comment for why.
                    originalManager.LoadClassDatabaseFromPackage(Program.kTargetUnityVersion);
                    moddedManager.LoadClassDatabaseFromPackage(Program.kTargetUnityVersion);
                }

                bool fileTouched = false;

                TransplantSprites(
                    origAfileInst, moddedAfileInst, origDirInfo.Name, opt,
                    ref spritesOverridden, ref spritesAdded, ref spritesUnchanged, ref fileTouched);

                TransplantTextures(
                    originalManager, moddedManager, origAfileInst, moddedAfileInst, origDirInfo.Name, opt,
                    ref texturesReencoded, ref texturesUnchanged, ref texturesSkippedErrors, ref texturesAddedNew,
                    ref fileTouched);

                if (fileTouched && !opt.DryRun)
                {
                    origDirInfo.SetNewData(origAfileInst.file);
                    touchedFiles++;
                }
                else if (fileTouched)
                {
                    touchedFiles++; // count it for the dry-run summary even though nothing is written
                }
            }

            // Log modded-only SerializedFiles (present in modded, absent from
            // original) - not merged, since that would mean inserting a whole
            // new directory entry rather than editing an existing one.
            foreach (var name in moddedIndexByName.Keys)
            {
                if (!originalNames.Contains(name))
                {
                    Console.WriteLine(
                        $"[{name}] exists only in the modded bundle; no original counterpart to transplant " +
                        "into - skipped (would require inserting a whole new SerializedFile).");
                }
            }

            Console.WriteLine(
                $"[summary] Sprites: {spritesOverridden} overridden, {spritesAdded} added, " +
                $"{spritesUnchanged} unchanged. Textures: {texturesReencoded} re-encoded, " +
                $"{texturesAddedNew} added new, {texturesUnchanged} unchanged, " +
                $"{texturesSkippedErrors} skipped due to errors. {touchedFiles} SerializedFile(s) touched.");

            if (opt.DryRun)
            {
                Console.WriteLine("[dry-run] no output written.");
                return 0;
            }

            WritePackedOutput(originalBunInst, opt.OutputPath);
            Console.WriteLine($"Wrote {opt.OutputPath}.");
            return 0;
        }
        finally
        {
            originalManager.UnloadAll();
            moddedManager.UnloadAll();
            if (tempOriginalUnpacked != null) TryDelete(tempOriginalUnpacked);
            if (tempModdedUnpacked != null) TryDelete(tempModdedUnpacked);
        }
    }

    // --- Sprite pass: raw byte-for-byte, no typetree parsing needed at all ---
    private static void TransplantSprites(
        AssetsFileInstance origAfileInst,
        AssetsFileInstance moddedAfileInst,
        string fileName,
        Options opt,
        ref int overridden,
        ref int added,
        ref int unchanged,
        ref bool fileTouched)
    {
        AssetsFile origAf = origAfileInst.file;
        AssetsFile moddedAf = moddedAfileInst.file;

        var origByPathId = new Dictionary<long, AssetFileInfo>();
        foreach (AssetFileInfo info in origAf.GetAssetsOfType(AssetClassID.Sprite))
            origByPathId[info.PathId] = info;

        var usedPathIds = new HashSet<long>();
        foreach (AssetFileInfo info in origAf.Metadata.AssetInfos)
            usedPathIds.Add(info.PathId);

        foreach (AssetFileInfo moddedInfo in moddedAf.GetAssetsOfType(AssetClassID.Sprite))
        {
            byte[] moddedBytes = ReadRawBytes(moddedAf, moddedInfo);

            if (origByPathId.TryGetValue(moddedInfo.PathId, out AssetFileInfo? origInfo))
            {
                byte[] origBytes = ReadRawBytes(origAf, origInfo);
                if (BytesEqual(origBytes, moddedBytes))
                {
                    unchanged++;
                    continue;
                }

                Console.WriteLine(
                    $"[{fileName}] Sprite PathId {moddedInfo.PathId}: differs from original " +
                    $"({origBytes.Length:N0} -> {moddedBytes.Length:N0} bytes) - overriding.");

                if (!opt.DryRun)
                    origInfo.SetNewData(moddedBytes);
                overridden++;
                fileTouched = true;
            }
            else
            {
                if (usedPathIds.Contains(moddedInfo.PathId))
                {
                    Console.WriteLine(
                        $"[{fileName}] Sprite PathId {moddedInfo.PathId} not present in original as a Sprite, " +
                        "but that PathId is already used by a DIFFERENT object in original - skipping this one " +
                        "rather than risk corrupting an unrelated asset.");
                    continue;
                }

                Console.WriteLine(
                    $"[{fileName}] Sprite PathId {moddedInfo.PathId}: not present in original - adding " +
                    $"({moddedBytes.Length:N0} bytes).");

                if (!opt.DryRun)
                {
                    var newInfo = AssetFileInfo.Create(origAf, moddedInfo.PathId, (int)AssetClassID.Sprite);
                    newInfo.SetNewData(moddedBytes);
                    origAf.Metadata.AddAssetInfo(newInfo);
                    usedPathIds.Add(moddedInfo.PathId);
                }
                added++;
                fileTouched = true;
            }
        }
    }

    // --- Texture2D pass: decode both sides to RGBA32, diff ignoring dimensions,
    // only re-encode what's actually changed ---------------------------------
    private static void TransplantTextures(
        AssetsManager originalManager,
        AssetsManager moddedManager,
        AssetsFileInstance origAfileInst,
        AssetsFileInstance moddedAfileInst,
        string fileName,
        Options opt,
        ref int reencoded,
        ref int unchanged,
        ref int skippedErrors,
        ref int addedNew,
        ref bool fileTouched)
    {
        AssetsFile origAf = origAfileInst.file;
        AssetsFile moddedAf = moddedAfileInst.file;

        var origByPathId = new Dictionary<long, AssetFileInfo>();
        foreach (AssetFileInfo info in origAf.GetAssetsOfType(AssetClassID.Texture2D))
            origByPathId[info.PathId] = info;

        var usedPathIds = new HashSet<long>();
        foreach (AssetFileInfo info in origAf.Metadata.AssetInfos)
            usedPathIds.Add(info.PathId);

        foreach (AssetFileInfo moddedInfo in moddedAf.GetAssetsOfType(AssetClassID.Texture2D))
        {
            AssetTypeValueField moddedBase = moddedManager.GetBaseField(moddedAfileInst, moddedInfo);
            string texName = moddedBase["m_Name"].AsString;

            try
            {
                if (origByPathId.TryGetValue(moddedInfo.PathId, out AssetFileInfo? origInfo))
                {
                    AssetTypeValueField origBase = originalManager.GetBaseField(origAfileInst, origInfo);

                    int origFormat = origBase["m_TextureFormat"].AsInt;
                    int origWidth = origBase["m_Width"].AsInt;
                    int origHeight = origBase["m_Height"].AsInt;

                    int moddedFormat = moddedBase["m_TextureFormat"].AsInt;
                    int moddedWidth = moddedBase["m_Width"].AsInt;
                    int moddedHeight = moddedBase["m_Height"].AsInt;

                    byte[] origRgba = DecodeTextureRgba32(origAfileInst, origBase, origFormat, origWidth, origHeight, texName);
                    byte[] moddedRgba = DecodeTextureRgba32(moddedAfileInst, moddedBase, moddedFormat, moddedWidth, moddedHeight, texName);

                    byte[] origGrid = TextureCodec.DownsampleToGrid(origRgba, origWidth, origHeight, DefaultGridSize);
                    byte[] moddedGrid = TextureCodec.DownsampleToGrid(moddedRgba, moddedWidth, moddedHeight, DefaultGridSize);
                    double diff = TextureCodec.Percentile95CellDifference(origGrid, moddedGrid, DefaultGridSize);

                    Console.WriteLine(
                        $"[{fileName}] Texture2D '{texName}' PathId {moddedInfo.PathId}: " +
                        $"orig {origWidth}x{origHeight} {TextureCodec.FormatName(origFormat)} vs " +
                        $"modded {moddedWidth}x{moddedHeight} {TextureCodec.FormatName(moddedFormat)}, diff={diff:F2}" +
                        (diff > opt.Threshold ? " -> CHANGED" : " -> unchanged"));

                    if (diff <= opt.Threshold)
                    {
                        unchanged++;
                        continue;
                    }

                    byte[] resampled = TextureCodec.ResampleBilinear(moddedRgba, moddedWidth, moddedHeight, origWidth, origHeight);
                    byte[] encoded = TextureCodec.EncodeFromRgba32(resampled, origWidth, origHeight, origFormat, texName);

                    if (!opt.DryRun)
                    {
                        origBase["m_TextureFormat"].AsInt = origFormat; // unchanged - keep original's own format
                        origBase["m_MipCount"].AsInt = 1;
                        origBase["m_CompleteImageSize"].AsInt = encoded.Length;

                        AssetTypeValueField streamData = origBase["m_StreamData"];
                        streamData["offset"].AsULong = 0;
                        streamData["size"].AsInt = 0;
                        streamData["path"].AsString = string.Empty;
                        origBase["image data"].AsByteArray = encoded;

                        origInfo.SetNewData(origBase);
                    }
                    reencoded++;
                    fileTouched = true;
                }
                else
                {
                    if (usedPathIds.Contains(moddedInfo.PathId))
                    {
                        Console.WriteLine(
                            $"[{fileName}] Texture2D '{texName}' PathId {moddedInfo.PathId} not present in " +
                            "original as a Texture2D, but that PathId is already used by a different object - skipping.");
                        skippedErrors++;
                        continue;
                    }

                    // No counterpart at all: decode the modded texture and encode it
                    // fresh into the configured default new-texture format, then
                    // insert it as a brand new object using the SAME baseField we
                    // already read from modded (correct field layout for this Unity
                    // build), just with the format/image-data fields swapped - the
                    // same in-place mutation the "changed" branch above does, only
                    // targeting a newly created AssetFileInfo in orig instead of an
                    // existing one.
                    int moddedFormat = moddedBase["m_TextureFormat"].AsInt;
                    int moddedWidth = moddedBase["m_Width"].AsInt;
                    int moddedHeight = moddedBase["m_Height"].AsInt;

                    byte[] moddedRgba = DecodeTextureRgba32(moddedAfileInst, moddedBase, moddedFormat, moddedWidth, moddedHeight, texName);
                    byte[] encoded = TextureCodec.EncodeFromRgba32(moddedRgba, moddedWidth, moddedHeight, opt.NewTextureFormat, texName);

                    Console.WriteLine(
                        $"[{fileName}] Texture2D '{texName}' PathId {moddedInfo.PathId}: not present in original - " +
                        $"adding as {TextureCodec.FormatName(opt.NewTextureFormat)} ({moddedWidth}x{moddedHeight}).");

                    if (!opt.DryRun)
                    {
                        moddedBase["m_TextureFormat"].AsInt = opt.NewTextureFormat;
                        moddedBase["m_MipCount"].AsInt = 1;
                        moddedBase["m_CompleteImageSize"].AsInt = encoded.Length;

                        AssetTypeValueField streamData = moddedBase["m_StreamData"];
                        streamData["offset"].AsULong = 0;
                        streamData["size"].AsInt = 0;
                        streamData["path"].AsString = string.Empty;
                        moddedBase["image data"].AsByteArray = encoded;

                        var newInfo = AssetFileInfo.Create(origAf, moddedInfo.PathId, (int)AssetClassID.Texture2D);
                        newInfo.SetNewData(moddedBase);
                        origAf.Metadata.AddAssetInfo(newInfo);
                        usedPathIds.Add(moddedInfo.PathId);
                    }
                    addedNew++;
                    fileTouched = true;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[{fileName}] Texture2D '{texName}' PathId {moddedInfo.PathId}: skipped due to error - " +
                    $"{ex.GetType().Name}: {ex.Message}");
                skippedErrors++;
            }
        }
    }

    private static byte[] DecodeTextureRgba32(
        AssetsFileInstance afileInst, AssetTypeValueField baseField, int format, int width, int height, string texName)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"invalid dimensions for '{texName}': {width}x{height}");

        TextureFile tf = TextureFile.ReadTextureFile(baseField);
        byte[] encodedData = tf.FillPictureData(afileInst)
            ?? throw new InvalidDataException($"could not load texture data for '{texName}'");

        return TextureCodec.DecodeToRgba32(encodedData, width, height, format, texName);
    }

    // --- shared plumbing -----------------------------------------------------

    private static byte[] ReadRawBytes(AssetsFile af, AssetFileInfo info)
    {
        AssetsFileReader reader = af.Reader;
        reader.Position = info.GetAbsoluteByteOffset(af);
        return reader.ReadBytes((int)info.ByteSize);
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        return ((ReadOnlySpan<byte>)a).SequenceEqual(b);
    }

    private static bool LooksLikeSerializedFile(string name) =>
        !name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) &&
        !name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase);

    // Same "materialize a genuinely uncompressed bundle first" idiom Program.cs
    // uses for its input bundle - kept here as its own helper since this mode
    // needs it for BOTH bundles (original and modded), not just one.
    private static BundleFileInstance LoadFullyUnpacked(
        AssetsManager manager, string path, string tempPrefix, out string? tempPath)
    {
        tempPath = null;
        BundleFileInstance loaded = manager.LoadBundleFile(path, unpackIfPacked: false);

        AssetBundleCompressionType compression = loaded.file.GetCompressionType();
        if (compression == AssetBundleCompressionType.None)
        {
            Console.WriteLine($"[bundle] '{path}' is already uncompressed.");
            return loaded;
        }

        string unpackedPath = Path.Combine(Path.GetTempPath(), $"{tempPrefix}-{Guid.NewGuid():N}.unity3d");
        using (var unpackedStream = File.Create(unpackedPath))
        using (var unpackedWriter = new AssetsFileWriter(unpackedStream))
        {
            loaded.file.Unpack(unpackedWriter);
        }

        manager.UnloadBundleFile(loaded);
        tempPath = unpackedPath;

        BundleFileInstance reloaded = manager.LoadBundleFile(unpackedPath, unpackIfPacked: false);
        if (reloaded.file.GetCompressionType() != AssetBundleCompressionType.None || reloaded.file.DataIsCompressed)
            throw new InvalidDataException($"failed to fully decompress '{path}'");

        Console.WriteLine($"[bundle] '{path}' decompressed ({compression} -> None).");
        return reloaded;
    }

    // Same three-stage Write -> Pack -> transcode-to-real-LZ4 pipeline
    // Program.cs's Main uses for its own output, reused verbatim here since
    // the container-format concerns (Pack() mislabeling HC as LZ4) apply
    // identically regardless of which direction produced the working bundle.
    private static void WritePackedOutput(BundleFileInstance bunInst, string outputPath)
    {
        string tempUnpackedPath = Path.Combine(Path.GetTempPath(), $"BundleDoctor-transplant-{Guid.NewGuid():N}.unity3d");
        string tempPackedPath = Path.Combine(Path.GetTempPath(), $"BundleDoctor-transplant-packed-{Guid.NewGuid():N}.unity3d");

        try
        {
            using (var tempStream = File.Create(tempUnpackedPath))
            using (var tempWriter = new AssetsFileWriter(tempStream))
            {
                bunInst.file.Write(tempWriter, 0);
            }

            var packManager = new AssetsManager();
            try
            {
                BundleFileInstance materializedInst = packManager.LoadBundleFile(tempUnpackedPath, unpackIfPacked: false);
                using var packedStream = File.Create(tempPackedPath);
                using var packedWriter = new AssetsFileWriter(packedStream);
                materializedInst.file.Pack(packedWriter, AssetBundleCompressionType.LZ4);
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
            TryDelete(tempUnpackedPath);
            TryDelete(tempPackedPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static Options? ParseArgs(string[] args)
    {
        var positional = new List<string>(args);
        var opt = new Options();

        for (int i = 0; i < positional.Count; i++)
        {
            if (string.Equals(positional[i], "--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                opt.DryRun = true;
                positional.RemoveAt(i);
                i--;
            }
            else if (string.Equals(positional[i], "--threshold", StringComparison.OrdinalIgnoreCase) && i + 1 < positional.Count)
            {
                if (!double.TryParse(positional[i + 1], out double t))
                    return null;
                opt.Threshold = t;
                positional.RemoveRange(i, 2);
                i--;
            }
            else if (string.Equals(positional[i], "--new-texture-format", StringComparison.OrdinalIgnoreCase) && i + 1 < positional.Count)
            {
                opt.NewTextureFormat = ParseFormatName(positional[i + 1]);
                positional.RemoveRange(i, 2);
                i--;
            }
        }

        if (positional.Count < 3)
            return null;

        opt.OriginalPath = positional[0];
        opt.ModdedPath = positional[1];
        opt.OutputPath = positional[2];
        if (positional.Count >= 4)
            opt.TpkPath = positional[3];

        return opt;
    }

    private static int ParseFormatName(string value) => value.Trim().ToUpperInvariant() switch
    {
        "RGBA32" => TextureCodec.FmtRGBA32,
        "ASTC_RGBA_4X4" => TextureCodec.FmtASTC_RGBA_4x4,
        "ASTC_RGBA_6X6" => TextureCodec.FmtASTC_RGBA_6x6,
        "ASTC_8X8" => TextureCodec.FmtASTC_RGBA_8x8,
        "ASTC_RGBA_8X8" => TextureCodec.FmtASTC_RGBA_8x8,
        "ASTC8X8" => TextureCodec.FmtASTC_RGBA_8x8,
        "ETC2" => TextureCodec.FmtETC2_RGBA8,
        "ETC2_RGBA8" => TextureCodec.FmtETC2_RGBA8,
        "ETC2_RGB" => TextureCodec.FmtETC2_RGB,
        _ => throw new ArgumentException(
            $"Unknown --new-texture-format '{value}'. Use RGBA32, ETC2, ETC2_RGB, ETC2_RGBA8, ASTC_RGBA_4x4, ASTC_RGBA_6x6, or ASTC_RGBA_8x8.")
    };
}
