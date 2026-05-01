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
using NSW.Avalonia.Models;
using NSW.Avalonia.Services;
using NSW.Core;
using NSW.Core.Enums;
using NSW.Core.Models;
using NSW.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Path = System.IO.Path;
using Res = NSW.Core.Properties.Resources;

namespace NSW.M2.Avalonia.Services;

public static class NspMergeService
{
    public static async Task<List<string>> Merge(IReadOnlyList<string> inputPaths, string outputDir, int nczCompressionLevel, bool verify, IProgress<ProgressInfo> progress, Action<string, LogLevel, string> log, CancellationToken ct = default)
    {
        return await RunMergeAll(inputPaths, outputDir, nczCompressionLevel > 0, nczCompressionLevel, verify, KeySetProvider.Instance.KeySet.Clone(), progress, log, ct);
    }

    public static async Task<List<string>> RunMergeAll(IReadOnlyList<string> inputPaths, string outputDir, bool compressToNcz, int nczCompressionLevel, bool verify, KeySet ks, IProgress<ProgressInfo> progress, Action<string, LogLevel, string> log, CancellationToken ct = default)
    {
        log?.Invoke(Res.Log_AnalyzeMetadata, LogLevel.Info, string.Empty);

        var allMeta = new List<MetadataResult>();
        foreach (var path in inputPaths)
        {
            ct.ThrowIfCancellationRequested();
            allMeta.AddRange(LibHacHelper.GetMetadataFromContainer(ks, path));
        }

        if (allMeta.Count == 0)
            throw new InvalidOperationException(Res.Error_NoMetadata);

        var groups = BuildTitleGroups(allMeta);
        log?.Invoke(string.Format(Res.Log_TitleGroupDetected, groups.Count), LogLevel.Info, string.Empty);

        var results = new List<string>();
        int idx = 0;

        foreach (var group in groups.Values)
        {
            ct.ThrowIfCancellationRequested();
            idx++;

            bool hasAnyContent = group.BaseMetas.Count > 0 || group.PatchMetas.Count > 0 || group.DlcMetas.Count > 0;
            if (!hasAnyContent) continue;

            var baseMeta = group.BaseMetas.FirstOrDefault()
                           ?? group.PatchMetas.OrderByDescending(m => m.TitleVersion).FirstOrDefault()
                           ?? group.DlcMetas.FirstOrDefault();

            if (baseMeta == null) continue;

            var allSources = group.BaseMetas
                .Concat(group.PatchMetas)
                .Concat(group.DlcMetas)
                .Select(m => m.SourcePath)
                .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var latestPatch = group.PatchMetas
                .OrderByDescending(m => m.TitleVersion)
                .FirstOrDefault();

            var allowedNcaIds = BuildAllowedNcaIds(group, latestPatch);

            var req = new BuildRequest(
                group.BaseMetas.FirstOrDefault()?.SourcePath ?? string.Empty,
                latestPatch?.SourcePath ?? string.Empty,
                [.. group.DlcMetas.Select(m => m.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase)],
                outputDir)
            {
                CompressToNcz = compressToNcz,
                NczCompressionLevel = nczCompressionLevel,
                AllSourcePaths = allSources,
                TargetBaseTitleId = group.BaseTitleId,
                TargetBaseTitleName = group.BaseTitleName,
                AllowedNcaIds = allowedNcaIds,
                ResolvedMeta = new MetadataResult(
                    baseMeta.TitleId,
                    (latestPatch ?? baseMeta).TitleVersion,
                    (latestPatch ?? baseMeta).DisplayVersion,
                    baseMeta.KrTitle,
                    baseMeta.EnTitle,
                    group.DlcMetas.GroupBy(m => m.TitleId, StringComparer.OrdinalIgnoreCase).Count(),
                    Type: group.BaseMetas.Count > 0 ? ContentMetaType.Application : baseMeta.Type),
            };

            log?.Invoke($"{group.BaseTitleName} {Res.Button_MergeStart} ({idx}/{groups.Count})", LogLevel.Highlight, group.BaseTitleId);

            try
            {
                results.Add(await RunMergeProcess(req, ks, verify, idx, groups.Count, group.BaseMetas.Count > 0, group.PatchMetas.Count > 0, progress, log, ct));
            }
            catch (Exception ex)
            {
                log?.Invoke(string.Format(Res.Log_MergeFailed, group.BaseTitleId, ex.Message), LogLevel.Error, group.BaseTitleId);
            }
        }

        return results;
    }

    private static Dictionary<string, TitleGroup> BuildTitleGroups(List<MetadataResult> allMeta)
    {
        var groups = new Dictionary<string, TitleGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var meta in allMeta)
        {
            if (string.IsNullOrEmpty(meta.TitleId)) continue;
            if (!ulong.TryParse(meta.TitleId,
                System.Globalization.NumberStyles.HexNumber, null, out ulong tid)) continue;

            string baseTid = (tid & 0xFFFFFFFFFFFF0000UL).ToString("X16");

            if (!groups.TryGetValue(baseTid, out var group))
            {
                group = new TitleGroup(baseTid, meta.KrTitle);
                groups[baseTid] = group;
            }

            switch (meta.Type)
            {
                case ContentMetaType.Application: group.BaseMetas.Add(meta); break;
                case ContentMetaType.Patch: group.PatchMetas.Add(meta); break;
                case ContentMetaType.AddOnContent: group.DlcMetas.Add(meta); break;
            }
        }

        return groups;
    }

    private static HashSet<string>? BuildAllowedNcaIds(TitleGroup group, MetadataResult? latestPatch)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var meta in group.BaseMetas
            .GroupBy(m => m.TitleId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()))
        {
            AddNcaIds(allowed, meta);
        }

        if (latestPatch != null)
            AddNcaIds(allowed, latestPatch);

        foreach (var meta in group.DlcMetas
            .GroupBy(m => m.TitleId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()))
        {
            AddNcaIds(allowed, meta);
        }

        return allowed.Count > 0 ? allowed : null;
    }

    private static void AddNcaIds(HashSet<string> set, MetadataResult meta)
    {
        if (meta.ContentNcaIds == null) return;
        foreach (var id in meta.ContentNcaIds) set.Add(id);
    }

    public static async Task<string> RunMergeProcess(BuildRequest req, KeySet keySet, bool verify, int index, int groupCount, bool hasBase, bool hasUpdate, IProgress<ProgressInfo> progress, Action<string, LogLevel, string> log, CancellationToken ct = default)
    {
        var disposables = new List<IDisposable>();
        var converters = new Dictionary<string, NcaToNczConverter>(StringComparer.OrdinalIgnoreCase);
        string? finalPath = null;
        bool isCompleted = false;
        var fileRegistry = new Dictionary<string, (string Path, string EntryName, string Ext)>(StringComparer.OrdinalIgnoreCase);
        var fsCache = new Dictionary<string, IFileSystem>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var allPaths = GetAllPaths(req);

            var allMetaCache = allPaths
                .SelectMany(p => LibHacHelper.GetMetadataFromContainer(keySet, p))
                .ToList();

            var ncaIdToMeta = allMetaCache
                .Where(m => m.ContentNcaIds != null)
                .SelectMany(m => m.ContentNcaIds!.Select(id => (id, m)))
                .GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().m, StringComparer.OrdinalIgnoreCase);

            foreach (var path in allPaths)
            {
                ct.ThrowIfCancellationRequested();

                var storage = new LocalStorage(path, FileAccess.Read);
                disposables.Add(storage);
                IFileSystem fs = storage.OpenFileSystem(keySet, path);
                disposables.Add(fs);
                fsCache[path] = fs;
                keySet.RegisterTickets(fs);

                foreach (var entry in fs.EnumerateEntries("/", "*"))
                {
                    string entryName = entry.Name.ToString();
                    string entryExt = Path.GetExtension(entryName).ToLowerInvariant();

                    if (req.AllowedNcaIds != null && entryExt is ".nca" or ".ncz")
                    {
                        string ncaId = ExtractNcaId(entryName);
                        if (!string.IsNullOrEmpty(ncaId) && !req.AllowedNcaIds.Contains(ncaId)) continue;
                    }

                    string finalName = entryExt == ".ncz" ? Path.ChangeExtension(entryName, ".nca") : entryName;
                    if (!fileRegistry.TryGetValue(finalName, out var value) || (value.Ext == ".ncz" && entryExt == ".nca"))
                        fileRegistry[finalName] = (path, entryName, entryExt);
                }
            }

            var fileEntries = new List<(string Name, Func<Stream, Action<long>, Task> Writer, long EstimatedSize, string Label)>();
            var addedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in fileRegistry)
            {
                ct.ThrowIfCancellationRequested();
                var (sourcePath, entryName, originalExt) = kvp.Value;

                if (!fsCache.TryGetValue(sourcePath, out var fs)) continue;

                var fileRef = new UniqueRef<IFile>();
                if (!fs.OpenFile(ref fileRef.Ref, ("/" + entryName).ToU8Span(), OpenMode.Read).IsSuccess()) continue;

                fileRef.Get.GetSize(out long size).ThrowIfFailure();
                if (size == 0) { fileRef.Destroy(); continue; }

                IFile rawFile = fileRef.Release();
                disposables.Add(rawFile);
                IStorage currentStorage = new FileStorage(rawFile);
                disposables.Add(currentStorage);

                if (originalExt is not (".nca" or ".ncz"))
                {
                    if (!addedFileNames.Add(kvp.Key)) continue;
                    var capturedStorage = currentStorage;
                    fileEntries.Add((kvp.Key, async (s, onRead) => await Common.CopyStreamAsync(capturedStorage.AsStream(), s, ct, onRead), size, kvp.Key));
                    continue;
                }

                var nca = new Nca(keySet, currentStorage);
                string tid = nca.Header.TitleId.ToString("X16");

                ncaIdToMeta.TryGetValue(ExtractNcaId(entryName), out var metaInfo);

                string typeTag = metaInfo != null ? LibHacHelper.GetContentMetaTypeTag(metaInfo.Type) : "Unknown";
                string ncaContentType = nca.Header.ContentType.ToString();
                string titleName = !string.IsNullOrEmpty(metaInfo?.KrTitle) ? metaInfo.KrTitle
                                 : !string.IsNullOrEmpty(metaInfo?.EnTitle) ? metaInfo.EnTitle
                                 : tid;

                if (originalExt == ".ncz")
                {
                    if (req.CompressToNcz)
                    {
                        if (!addedFileNames.Add(kvp.Key)) continue;
                        string finalName = entryName;
                        log?.Invoke($"- {titleName} [{typeTag}/{ncaContentType}] {Res.Log_Merging}", LogLevel.Info, req.TargetBaseTitleId);
                        var capturedStorage = currentStorage;
                        string label = $"{titleName} [{typeTag}] [{ncaContentType}] {Res.Log_Merging}";
                        fileEntries.Add((finalName, async (s, onRead) => await Common.CopyStreamAsync(capturedStorage.AsStream(), s, ct, onRead), size, label));
                    }
                    else
                    {
                        if (!addedFileNames.Add(kvp.Key)) continue;
                        string finalName = Path.ChangeExtension(entryName, ".nca");
                        log?.Invoke($"- {titleName} [{typeTag}/{ncaContentType}] {Res.Log_DecompressAndMerge}", LogLevel.Info, req.TargetBaseTitleId);
                        var ncz = new Ncz(keySet, currentStorage.AsStream(), NczReadMode.Original);
                        var decStorage = ncz.BaseStorage;
                        decStorage.GetSize(out long decSize).ThrowIfFailure();
                        string label = $"{titleName} [{typeTag}] [{ncaContentType}] {Res.Log_DecompressAndMerge}";
                        fileEntries.Add((finalName, async (s, onRead) => await Common.CopyStreamAsync(decStorage.AsStream(), s, ct, onRead), decSize, label));
                    }
                    continue;
                }

                if (req.CompressToNcz && nca.Header.ContentType is NcaContentType.Program or NcaContentType.PublicData)
                {
                    string finalName = Path.ChangeExtension(entryName, ".ncz");
                    if (!addedFileNames.Add(finalName)) continue;

                    log?.Invoke($"- {titleName} [{typeTag}/{ncaContentType}] {Res.Log_CompressAndMerge}", LogLevel.Info, req.TargetBaseTitleId);
                    var capturedStorage = currentStorage;
                    string label = $"{titleName} [{typeTag}] [{ncaContentType}] {Res.Log_CompressAndMerge}";
                    string capturedName = entryName;
                    var converter = new NcaToNczConverter(keySet);
                    converters[capturedName] = converter;

                    fileEntries.Add((finalName, async (s, onRead) =>
                    {
                        await converter.ConvertAsync(capturedStorage.AsStream(), s, req.NczCompressionLevel,onRead, ct);
                    }, size, label));
                }
                else
                {
                    if (!addedFileNames.Add(entryName)) continue;

                    log?.Invoke($"- {titleName} [{typeTag}/{ncaContentType}] {Res.Log_Merging}", LogLevel.Info, req.TargetBaseTitleId);
                    var capturedStorage = currentStorage;
                    string label = $"{titleName} [{typeTag}] [{ncaContentType}] {Res.Log_Merging}";

                    fileEntries.Add((entryName, async (s, onRead) => await Common.CopyStreamAsync(capturedStorage.AsStream(), s, ct, onRead), size, label));
                }
            }

            var meta = req.ResolvedMeta ?? ExtractFinalMetadata(keySet, allPaths, req.TargetBaseTitleId);
            log?.Invoke(string.Format(Res.Log_FinalId, meta.TitleId, meta.DisplayVersion), LogLevel.Ok, req.TargetBaseTitleId);

            string finalFileName = NspNameBuilder.FileNameBuild("Merged", meta.KrTitle, meta.EnTitle, meta.TitleId, meta.DisplayVersion, meta.TitleVersion, meta.DlcCount, hasBase, hasUpdate, req.CompressToNcz);
            finalPath = Path.Combine(req.OutputDir, finalFileName);
            finalPath = Common.GetUniqueFilePath(finalPath);

            while (allPaths.Any(p => string.Equals(p, finalPath, StringComparison.OrdinalIgnoreCase)) || File.Exists(finalPath))
            {
                string nameWithoutExt = Path.GetFileNameWithoutExtension(finalPath);
                string ext = Path.GetExtension(finalPath);
                finalPath = Path.Combine(req.OutputDir, nameWithoutExt + "_" + ext);
            }

            string displayName = NspNameBuilder.DisplayNameBuild(meta.EnTitle, meta.TitleId, meta.DisplayVersion, meta.DlcCount, hasBase, hasUpdate, req.CompressToNcz);
            using var fout = File.Open(finalPath, FileMode.Create, FileAccess.ReadWrite);            
            await Pfs0Builder.WriteAsync($"{Res.Log_Merging} {displayName}", meta.TitleId, fileEntries, fout, progress, ct);

            if (req.CompressToNcz && converters.Count > 0 && verify)
            {
                log?.Invoke($"{req.TargetBaseTitleName} {Res.Log_ValidationStart} ({index}/{groupCount})", LogLevel.Highlight, req.TargetBaseTitleId);
                fout.Position = 0;
                var verifyPfs = new PartitionFileSystem();
                verifyPfs.Initialize(fout.AsStorage()).ThrowIfFailure();

                var nczEntries = verifyPfs.EnumerateEntries("/", "*.ncz")
                    .Where(e => converters.ContainsKey(Path.ChangeExtension(e.Name, ".nca")))
                    .ToList();

                long totalVerifySize = nczEntries.Sum(e => e.Size);

                foreach (var entry in nczEntries)
                {
                    ct.ThrowIfCancellationRequested();
                    string origName = Path.ChangeExtension(entry.Name, ".nca");
                    if (!converters.TryGetValue(origName, out var converter)) continue;

                    using var nczFile = new UniqueRef<IFile>();
                    verifyPfs.OpenFile(ref nczFile.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                    ncaIdToMeta.TryGetValue(ExtractNcaId(entry.Name), out var nczMetaInfo);
                    string nczTypeTag = nczMetaInfo != null ? LibHacHelper.GetContentMetaTypeTag(nczMetaInfo.Type) : "Unknown";
                    string label = $"{(nczMetaInfo?.KrTitle ?? nczMetaInfo?.EnTitle ?? entry.Name)} [{nczTypeTag}]";

                    log?.Invoke($"- {label} {Res.ToolTip_ValidateCompress}", LogLevel.Info, req.TargetBaseTitleId);
                    await converter.ValidateAsync(nczFile.Get.AsStream(), nczMetaInfo?.TitleId, totalVerifySize, label, progress, ct);
                    log?.Invoke($"- {label} OK", LogLevel.Ok, req.TargetBaseTitleId);
                }
                log?.Invoke($"{req.TargetBaseTitleName} {Res.Log_ValidationComplete} ({index}/{groupCount})", LogLevel.Ok, req.TargetBaseTitleId);
            }

            isCompleted = true;
            log?.Invoke(string.Format($"{Res.Log_MergeComplete} ({index}/{groupCount})", finalFileName), LogLevel.Ok, req.TargetBaseTitleId);
            return finalPath;
        }
        catch (Exception ex)
        {
            log?.Invoke(string.Format($"{Res.Log_Error} ({index}/{groupCount})", ex.Message), LogLevel.Error, req.TargetBaseTitleId);
            throw;
        }
        finally
        {
            for (int i = disposables.Count - 1; i >= 0; i--) disposables[i]?.Dispose();

            if (!isCompleted && !string.IsNullOrEmpty(finalPath) && File.Exists(finalPath))
            {
                try { File.Delete(finalPath); log?.Invoke(Res.Log_DeleteIncompleteFile, LogLevel.Info, req.TargetBaseTitleId); }
                catch { }
            }
        }
    }

    private static string ExtractNcaId(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.EndsWith(".cnmt", StringComparison.OrdinalIgnoreCase))
            stem = Path.GetFileNameWithoutExtension(stem);

        return stem.Length >= 32 ? stem[..32].ToLowerInvariant() : string.Empty;
    }

    private static List<string> GetAllPaths(BuildRequest req)
    {
        if (req.AllSourcePaths is { Count: > 0 })
            return [.. req.AllSourcePaths.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))];

        var list = new List<string>();
        if (!string.IsNullOrEmpty(req.UpdateFilePath) && File.Exists(req.UpdateFilePath))
            list.Add(req.UpdateFilePath);
        foreach (var p in req.DlcFilePaths)
            if (!string.IsNullOrEmpty(p) && File.Exists(p) && !list.Contains(p))
                list.Add(p);
        if (!string.IsNullOrEmpty(req.BaseFilePath) && !list.Contains(req.BaseFilePath))
            list.Add(req.BaseFilePath);
        return list;
    }

    private static MetadataResult ExtractFinalMetadata(KeySet ks, List<string> paths, string? targetBaseTitleId = null)
    {
        var allMetas = paths
            .SelectMany(p => LibHacHelper.GetMetadataFromContainer(ks, p))
            .GroupBy(m => new { m.TitleId, m.TitleVersion, m.Type })
            .Select(g => g.First())
            .ToList();

        if (!string.IsNullOrEmpty(targetBaseTitleId))
        {
            allMetas = [.. allMetas.Where(m =>
            {
                if (!ulong.TryParse(m.TitleId, System.Globalization.NumberStyles.HexNumber, null, out ulong tid))
                    return false;
                return (tid & 0xFFFFFFFFFFFF0000UL).ToString("X16")
                    .Equals(targetBaseTitleId, StringComparison.OrdinalIgnoreCase);
            })];
        }

        if (allMetas.Count == 0) return new MetadataResult(string.Empty, 0, "1.0.0", string.Empty, string.Empty, 0, ContentMetaType.Application);

        int dlcCount = allMetas.Count(m => m.Type == ContentMetaType.AddOnContent);

        var latestPatch = allMetas
            .Where(m => m.Type == ContentMetaType.Patch)
            .OrderByDescending(m => m.TitleVersion)
            .FirstOrDefault();

        var baseGame = allMetas.FirstOrDefault(m => m.Type == ContentMetaType.Application) ?? allMetas.First();
        var versionSource = latestPatch ?? baseGame;

        return new MetadataResult(baseGame.TitleId, versionSource.TitleVersion, versionSource.DisplayVersion, baseGame.KrTitle, baseGame.EnTitle, dlcCount, ContentMetaType.Application);
    }
}