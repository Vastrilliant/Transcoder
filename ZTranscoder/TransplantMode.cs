
using System;
using System.Collections.Generic;
using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

internal static class TransplantMode
{

    private const int DefaultGridSize = 48;

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
                "usage: ZTranscoder transplant <original.bundle> <modded.bundle> <output.bundle> " +
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
                originalManager, opt.OriginalPath, "ZTranscoder-transplant-orig", out tempOriginalUnpacked);
            BundleFileInstance moddedBunInst = LoadFullyUnpacked(
                moddedManager, opt.ModdedPath, "ZTranscoder-transplant-modded", out tempModdedUnpacked);

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
                    touchedFiles++;
                }
            }

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
                        origBase["m_TextureFormat"].AsInt = origFormat;
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

    private static void WritePackedOutput(BundleFileInstance bunInst, string outputPath)
    {
        string tempUnpackedPath = Path.Combine(Path.GetTempPath(), $"ZTranscoder-transplant-{Guid.NewGuid():N}.unity3d");
        string tempPackedPath = Path.Combine(Path.GetTempPath(), $"ZTranscoder-transplant-packed-{Guid.NewGuid():N}.unity3d");

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
        try { File.Delete(path); } catch {  }
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

