using DynamicData;
using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Ncm;
using LibHac.NSZ;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Tools.Ncm;
using NSW.Avalonia.Services;
using NSW.Core;
using NSW.Core.Enums;
using NSW.Core.Models;
using NSW.Utils;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Path = System.IO.Path;
using Res = NSW.Core.Properties.Resources;

namespace NSW.M2.Avalonia.Services;

public static class NspSplitService
{
    public static async Task<int> Split(string sourceNspPath, string outputDir, int compressionLevel, bool useBlockMode, bool isValidationEnabled, bool forceKeyGen0, int index, int groupCount, IProgress<ProgressInfo> progress, Action<string, LogLevel, string> log, CancellationToken ct = default)
    {
        var keySet = KeySetProvider.Instance.KeySet.Clone();
        var allMetas = LibHacHelper.GetMetadataFromContainer(keySet, sourceNspPath);
        var disposables = new List<IDisposable>();
        bool useCompression = compressionLevel > 0;
        int successCount = 0;

        try
        {
            var storage = new LocalStorage(sourceNspPath, FileAccess.Read);
            disposables.Add(storage);
            var fs = storage.OpenFileSystem(keySet, sourceNspPath);
            disposables.Add(fs);
            keySet.RegisterTickets(fs);

            var fileRegistry = new Dictionary<string, (string EntryName, string Ext)>(StringComparer.OrdinalIgnoreCase);
            var tikRegistry = new Dictionary<string, (string EntryName, string Ext)>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in fs.EnumerateEntries("/", "*"))
            {
                string entryName = entry.Name.ToString();
                string entryExt = Path.GetExtension(entryName).ToLowerInvariant();

                if (entryExt is ".tik" or ".cert")
                {
                    tikRegistry[entryName] = (entryName, entryExt);
                    continue;
                }

                string finalName = entryExt == ".ncz" ? Path.ChangeExtension(entryName, ".nca") : entryName;

                if (!fileRegistry.TryGetValue(finalName, out var value) || (value.Ext == ".ncz" && entryExt == ".nca"))
                    fileRegistry[finalName] = (entryName, entryExt);
            }

            string cachedBaseTitle =
                allMetas.FirstOrDefault(m => m.Type == ContentMetaType.Application)
                is { } appMeta
                ? (!string.IsNullOrEmpty(appMeta.KrTitle) ? appMeta.KrTitle : appMeta.EnTitle)
                : allMetas.FirstOrDefault()
                is { } firstMeta
                ? (!string.IsNullOrEmpty(firstMeta.KrTitle) ? firstMeta.KrTitle : firstMeta.EnTitle)
                : string.Empty;

            foreach (var meta in allMetas)
            {
                ct.ThrowIfCancellationRequested();

                if (await ProcessSplitItem(meta, fileRegistry, tikRegistry, fs, keySet, cachedBaseTitle, outputDir, useCompression, compressionLevel, useBlockMode, isValidationEnabled, forceKeyGen0, index, groupCount, progress, log, ct))
                    successCount++;
            }
        }
        finally
        {
            for (int i = disposables.Count - 1; i >= 0; i--)
                disposables[i]?.Dispose();
        }

        return successCount;
    }

    private static async Task<bool> ProcessSplitItem(MetadataResult meta, Dictionary<string, (string EntryName, string Ext)> fileRegistry, Dictionary<string, (string EntryName, string Ext)> tikRegistry, IFileSystem fs, KeySet keySet, string baseTitle, string outputDir, bool useCompression, int compressionLevel, bool useBlockMode, bool isValidationEnabled, bool forceKeyGen0, int index, int groupCount, IProgress<ProgressInfo> progress, Action<string, LogLevel, string> log, CancellationToken ct)
    {
        var disposables = new List<IDisposable>();
        var converters = new Dictionary<string, NcaToNczConverter>(StringComparer.OrdinalIgnoreCase);
        string? finalPath = null;
        bool isCompleted = false;

        try
        {
            string typeTag = meta.GetTypeTag();
            string displayVer = meta.GetEffectiveDisplayVersion();
            log?.Invoke($"{string.Format(Res.Log_SplitPreparing, $"{baseTitle} [{typeTag}]")} ({index}/{groupCount})", LogLevel.Highlight, meta.TitleId);

            if (!fileRegistry.TryGetValue(meta.FileName, out var cnmtEntry))
                return false;

            var cnmtFileRef = new UniqueRef<IFile>();
            if (fs.OpenFile(ref cnmtFileRef.Ref, ("/" + cnmtEntry.EntryName).ToU8Span(), OpenMode.Read).IsFailure())
                return false;

            IFile cnmtRawFile = cnmtFileRef.Release();
            disposables.Add(cnmtRawFile);
            var cnmtNcaStorage = new FileStorage(cnmtRawFile);
            disposables.Add(cnmtNcaStorage);

            var cnmtNca = new Nca(keySet, cnmtNcaStorage);
            using var cnmtFs = cnmtNca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
            var cnmtFsEntry = cnmtFs.EnumerateEntries("/", "*.cnmt").First();
            using var cFile = new UniqueRef<IFile>();
            cnmtFs.OpenFile(ref cFile.Ref, cnmtFsEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
            var cnmt = new Cnmt(cFile.Get.AsStream());

            var fileEntries = new List<(string Name, Func<Stream, Action<long>, Task> Writer, long EstimatedSize, string Label)>();

            string titleIdHex = meta.TitleId.ToUpper();

            if (!forceKeyGen0)
            {
                foreach (var kvp in tikRegistry)
                {
                    if (!kvp.Key.Contains(titleIdHex, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var fileRef = new UniqueRef<IFile>();
                    if (fs.OpenFile(ref fileRef.Ref, ("/" + kvp.Value.EntryName).ToU8Span(), OpenMode.Read).IsFailure())
                        continue;

                    IFile rawFile = fileRef.Release();
                    disposables.Add(rawFile);
                    IStorage tikStorage = new FileStorage(rawFile);
                    disposables.Add(tikStorage);
                    tikStorage.GetSize(out long tikSize).ThrowIfFailure();
                    var captured = tikStorage;
                    fileEntries.Add((kvp.Value.EntryName, async (s, onRead) => await Common.CopyStreamAsync(captured.AsStream(), s, onRead, ct), tikSize, kvp.Value.EntryName));
                }
            }

            {
                cnmtNcaStorage.GetSize(out long cnmtSize).ThrowIfFailure();
                var captured = cnmtNcaStorage;
                fileEntries.Add((cnmtEntry.EntryName, async (s, onRead) =>
                {
                    await NcaRecryptService.RecryptAsync(captured.AsStream(), s, forceKeyGen0 ? 0 : (int)cnmtNca.Header.KeyGeneration, keySet, onRead, ct);
                }, cnmtSize, cnmtEntry.EntryName));
            }

            foreach (var record in cnmt.ContentEntries)
            {
                ct.ThrowIfCancellationRequested();
                string targetId = BitConverter.ToString(record.NcaId).Replace("-", string.Empty).ToLower();

                string ncaKey = fileRegistry.Keys.FirstOrDefault(k =>
                    Path.GetFileNameWithoutExtension(k).StartsWith(targetId, StringComparison.OrdinalIgnoreCase));

                if (ncaKey == null) 
                    continue;

                var (entryName, originalExt) = fileRegistry[ncaKey];

                var fileRef = new UniqueRef<IFile>();

                if (fs.OpenFile(ref fileRef.Ref, ("/" + entryName).ToU8Span(), OpenMode.Read).IsFailure()) 
                    continue;

                IFile rawFile = fileRef.Release();
                disposables.Add(rawFile);
                IStorage currentStorage = new FileStorage(rawFile);
                disposables.Add(currentStorage);
                currentStorage.GetSize(out long size).ThrowIfFailure();

                var nca = new Nca(keySet, currentStorage);
                string ncaContentType = nca.Header.ContentType.ToString();
                string label = $"{baseTitle} [{typeTag}/{ncaContentType}]";

                if (originalExt == ".ncz")
                {
                    var ncz = new Ncz(keySet, currentStorage, NczReadMode.Original);
                    var decStorage = ncz.BaseStorage;
                    decStorage.GetSize(out long decSize).ThrowIfFailure();

                    if (useCompression && nca.Header.ContentType is NcaContentType.Program or NcaContentType.PublicData)
                    {
                        string nczName = Path.ChangeExtension(entryName, ".ncz");
                        log?.Invoke($"- {label} {Res.Log_CompressAndMerge}", LogLevel.Info, meta.TitleId);
                        var converter = new NcaToNczConverter(keySet);
                        converters[entryName] = converter;
                        var captured = decStorage;
                        fileEntries.Add((nczName, async (s, onRead) =>
                        {
                            var recryptedHeader = await NcaRecryptService.GetRecryptedHeaderAsync(captured, forceKeyGen0 ? 0 : (int)nca.Header.KeyGeneration, keySet, ct);
                            using var headerStream = new MemoryStream(recryptedHeader);
                            await converter.ConvertAsync(headerStream, captured, s, useBlockMode, compressionLevel, onRead, ct);
                        }, decSize, label));
                    }
                    else
                    {
                        log?.Invoke($"- {label} {Res.Log_DecompressAndMerge}", LogLevel.Info, meta.TitleId);
                        var captured = decStorage;
                        fileEntries.Add((ncaKey, async (s, onRead) =>
                        {
                            await NcaRecryptService.RecryptAsync(captured.AsStream(), s, forceKeyGen0 ? 0 : (int)nca.Header.KeyGeneration, keySet, onRead, ct);
                        }, decSize, label));
                    }
                }
                else if (useCompression && nca.Header.ContentType is NcaContentType.Program or NcaContentType.PublicData)
                {
                    string nczName = Path.ChangeExtension(entryName, ".ncz");
                    log?.Invoke($"- {label} {Res.Log_CompressAndMerge}", LogLevel.Info, meta.TitleId);
                    var capturedStorage = currentStorage;
                    var converter = new NcaToNczConverter(keySet);
                    converters[entryName] = converter;
                    fileEntries.Add((nczName, async (s, onRead) =>
                    {
                        var recryptedHeader = await NcaRecryptService.GetRecryptedHeaderAsync(capturedStorage, forceKeyGen0 ? 0 : (int)nca.Header.KeyGeneration, keySet, ct);
                        using var headerStream = new MemoryStream(recryptedHeader);
                        await converter.ConvertAsync(headerStream, capturedStorage, s, useBlockMode, compressionLevel, onRead, ct);
                    }, size, label));
                }
                else
                {
                    log?.Invoke($"- {label} {Res.Log_Merging}", LogLevel.Info, meta.TitleId);
                    var captured = currentStorage;
                    fileEntries.Add((ncaKey, async (s, onRead) =>
                    {
                        await NcaRecryptService.RecryptAsync(captured.AsStream(), s, forceKeyGen0 ? 0 : (int)nca.Header.KeyGeneration, keySet, onRead, ct);
                    }, size, label));
                }
            }

            string versionPart = typeTag != "DLC" ? $" [v{meta.GetEffectiveDisplayVersion()}]" : string.Empty;
            string outName = $"{baseTitle} [{meta.TitleId}] ({typeTag}){versionPart}{(useCompression ? ".nsz" : ".nsp")}";
            outName = SanitizeFileName(outName);
            finalPath = Common.GetUniqueFilePath(Path.Combine(outputDir, outName));

            string displayName = NspNameBuilder.DisplayNameBuild(meta.EnTitle, meta.TitleId, meta.DisplayVersion);
            using var fout = File.Open(finalPath, FileMode.Create, FileAccess.ReadWrite);
            await Pfs0Builder.WriteAsync($"{Res.Log_Splitting} {displayName}", meta.TitleId, fileEntries, fout, progress, ct);

            if (useCompression && converters.Count > 0 && isValidationEnabled)
            {
                log?.Invoke($"{baseTitle} [{typeTag}] {Res.Log_ValidationStart} ({index}/{groupCount})", LogLevel.Highlight, meta.TitleId);
                fout.Position = 0;
                var validationFs = new PartitionFileSystem();
                validationFs.Initialize(fout.AsStorage()).ThrowIfFailure();
                var nczEntries = validationFs.EnumerateEntries("/", "*.ncz")
                    .Where(e => converters.ContainsKey(Path.ChangeExtension(e.Name, ".nca")))
                    .ToList();
                long totalValidationSize = nczEntries.Sum(e => e.Size);

                foreach (var entry in nczEntries)
                {
                    ct.ThrowIfCancellationRequested();
                    string origName = Path.ChangeExtension(entry.Name, ".nca");

                    if (!converters.TryGetValue(origName, out var converter)) 
                        continue;

                    using var nczFile = new UniqueRef<IFile>();
                    validationFs.OpenFile(ref nczFile.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                    string vlabel = $"{baseTitle} [{typeTag}]";
                    log?.Invoke($"- {vlabel} {Res.ToolTip_ValidateCompress}", LogLevel.Info, meta.TitleId);
                    await converter.ValidateAsync(nczFile.Get.AsStream(), meta.TitleId, totalValidationSize, vlabel, progress, ct);
                    log?.Invoke($"- {vlabel} OK", LogLevel.Ok, meta.TitleId);
                }
                log?.Invoke($"{baseTitle} [{typeTag}] {Res.Log_ValidationComplete} ({index}/{groupCount})", LogLevel.Ok, meta.TitleId);
            }

            isCompleted = true;
            log?.Invoke($"{string.Format(Res.Log_SplitComplete, outName)} ({index}/{groupCount})", LogLevel.Ok, meta.TitleId);

            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"{string.Format(Res.Log_SplitFailed, meta.TitleId, ex.Message)} ({index}/{groupCount})", LogLevel.Error, meta.TitleId);

            return false;
        }
        finally
        {
            for (int i = disposables.Count - 1; i >= 0; i--)
                disposables[i]?.Dispose();

            if (!isCompleted && !string.IsNullOrEmpty(finalPath) && File.Exists(finalPath))
                try { File.Delete(finalPath); } catch { }
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':']).Distinct().ToArray();

        foreach (var c in invalidChars)
            name = name.Replace(c.ToString(), string.Empty);

        return Regex.Replace(name, @"\s+", " ").Trim();
    }
}