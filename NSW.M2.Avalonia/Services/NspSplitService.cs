using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Ncm;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Tools.Ncm;
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
using Path = System.IO.Path;
using Res = NSW.Core.Properties.Resources;

namespace NSW.M2.Avalonia.Services;

public static class NspSplitService
{
    public static int Split(string sourceNspPath, string outputDir, int index, int groupCount, IProgress<ProgressInfo> progress, Action<string, LogLevel, string> log, CancellationToken ct = default)
    {
        var keySet = KeySetProvider.Instance.KeySet;
        var allMetas = LibHacHelper.GetMetadataFromContainer(keySet, sourceNspPath);
        var disposables = new List<IDisposable>();
        var allFiles = new Dictionary<string, IStorage>();
        int successCount = 0;

        try
        {
            var storage = new LocalStorage(sourceNspPath, FileAccess.Read);
            disposables.Add(storage);
            var fs = storage.OpenFileSystem(keySet, sourceNspPath);
            disposables.Add(fs);

            foreach (var entry in fs.EnumerateEntries("/", "*"))
            {
                var file = new UniqueRef<IFile>();
                if (fs.OpenFile(ref file.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).IsFailure()) continue;
                IFile rawFile = file.Release();
                disposables.Add(rawFile);
                allFiles.Add(entry.Name, rawFile.AsStream().AsStorage());
            }

            string cachedBaseTitle =
                allMetas.FirstOrDefault(m => m.Type == ContentMetaType.Application)
                is { } appMeta
                ? (!string.IsNullOrEmpty(appMeta.KrTitle) ? appMeta.KrTitle : appMeta.EnTitle)
                : allMetas.FirstOrDefault()
                is { } firstMeta
                ? (!string.IsNullOrEmpty(firstMeta.KrTitle) ? firstMeta.KrTitle : firstMeta.EnTitle)
                : string.Empty;

            foreach(var meta in allMetas)
            {                
                ct.ThrowIfCancellationRequested();
                if (ProcessSplitItem(meta, allFiles, keySet, cachedBaseTitle, outputDir, index, groupCount, progress, log, ct))
                    successCount++;
            }
        }
        finally
        {
            for (int i = disposables.Count - 1; i >= 0; i--) disposables[i]?.Dispose();
        }

        return successCount;
    }

    private static bool ProcessSplitItem(MetadataResult meta, Dictionary<string, IStorage> allFiles, KeySet keySet, string baseTitle, string outputDir, int index, int groupCount, IProgress<ProgressInfo> progress, Action<string, LogLevel, string> log, CancellationToken ct)
    {
        try
        {
            string typeTag = meta.GetTypeTag();
            string displayVer = meta.GetEffectiveDisplayVersion();
            
            log?.Invoke($"{string.Format(Res.Log_SplitPreparing, $"{baseTitle} [{typeTag}]")} ({index}/{groupCount})", LogLevel.Highlight, meta.TitleId);

            string versionPart = typeTag != "DLC" ? $" [v{displayVer}]" : string.Empty;
            string outName = $"{baseTitle} [{meta.TitleId}] ({typeTag}){versionPart}.nsp";

            var invalidChars = Path.GetInvalidFileNameChars()
                .Concat(['\\', '/', ':'])
                .Distinct()
                .ToArray();

            foreach (var c in invalidChars)
                outName = outName.Replace(c.ToString(), "");

            outName = Regex.Replace(outName, @"\s+", " ").Trim();

            var builder = new PartitionFileSystemBuilder();
            string titleIdHex = meta.TitleId.ToUpper();

            var tikName = allFiles.Keys.FirstOrDefault(k => k.EndsWith(".tik") && k.Contains(titleIdHex, StringComparison.OrdinalIgnoreCase));
            if (tikName != null) builder.AddFile(tikName, allFiles[tikName].AsFile(OpenMode.Read));

            var certName = allFiles.Keys.FirstOrDefault(k => k.EndsWith(".cert") && k.Contains(titleIdHex, StringComparison.OrdinalIgnoreCase));
            if (certName != null) builder.AddFile(certName, allFiles[certName].AsFile(OpenMode.Read));

            if (!allFiles.ContainsKey(meta.FileName)) return false;
            string cnmtNcaName = meta.FileName;

            builder.AddFile(cnmtNcaName, allFiles[cnmtNcaName].AsFile(OpenMode.Read));

            using var ncaStorage = new FileStorage(allFiles[cnmtNcaName].AsFile(OpenMode.Read));
            var nca = new Nca(keySet, ncaStorage);
            using var cnmtFs = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
            var entry = cnmtFs.EnumerateEntries("/", "*.cnmt").First();

            using var cFile = new UniqueRef<IFile>();
            cnmtFs.OpenFile(ref cFile.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
            var cnmt = new Cnmt(cFile.Get.AsStream());

            foreach (var record in cnmt.ContentEntries)
            {
                string targetId = BitConverter.ToString(record.NcaId).Replace("-", string.Empty).ToLower();
                string matchName = allFiles.Keys.FirstOrDefault(k => k.StartsWith(targetId, StringComparison.OrdinalIgnoreCase));

                if (matchName != null)
                {
                    var file = allFiles[matchName].AsFile(OpenMode.Read);
                    string displayText = $"{baseTitle} [{typeTag}/{record.Type}]";

                    if (matchName.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase))
                        displayText = string.Format(Res.Log_SplitDecompressing, displayText);
                    else
                        displayText = string.Format(Res.Log_SplitExtracting, displayText);

                    log?.Invoke(displayText, LogLevel.Info, meta.TitleId);

                    var stream = LibHacHelper.GetDecodedStream(file, matchName, keySet);
                    builder.AddFile(Path.ChangeExtension(matchName, ".nca"), stream.AsStorage().AsFile(OpenMode.Read));
                }
            }

            WriteNsp(builder, Path.Combine(outputDir, outName), meta, typeTag, progress, ct);

            log?.Invoke($"{string.Format(Res.Log_SplitComplete, outName)} ({index}/{groupCount})", LogLevel.Ok, meta.TitleId);

            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"{string.Format(Res.Log_SplitFailed, meta.TitleId, ex.Message)} ({ index}/{ groupCount})", LogLevel.Error, meta.TitleId);
            return false;
        }
    }

    private static void WriteNsp(PartitionFileSystemBuilder builder, string outPath, MetadataResult meta, string typeTag, IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        bool isCompleted = false;
        string displayName = NspNameBuilder.DisplayNameBuild(meta.EnTitle, meta.TitleId, meta.DisplayVersion);

        const int bufferSize = 0x800000;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        var reportSw = System.Diagnostics.Stopwatch.StartNew();
        var startTime = System.Diagnostics.Stopwatch.GetTimestamp();
        double freq = System.Diagnostics.Stopwatch.Frequency;

        try
        {
            using var nspStorage = builder.Build(PartitionFileSystemType.Standard);
            nspStorage.GetSize(out long size);

            outPath = Common.GetUniqueFilePath(outPath);
            using var fout = File.Open(outPath, FileMode.Create, FileAccess.Write);
            using var nspStream = nspStorage.AsStream();

            long totalRead = 0;

            while (totalRead < size)
            {
                ct.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(bufferSize, size - totalRead);
                int read = nspStream.Read(buffer, 0, toRead);

                if (read <= 0) break;

                fout.Write(buffer, 0, read);
                totalRead += read;

                if (reportSw.ElapsedMilliseconds >= 100)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    double elapsedSec = (now - startTime) / freq;

                    double bytesPerSec = elapsedSec > 0 ? totalRead / elapsedSec : 0;
                    double mibPerSec = bytesPerSec / (1024.0 * 1024.0);

                    double remainingBytes = size - totalRead;
                    double etaSec = bytesPerSec > 0 ? remainingBytes / bytesPerSec : 0;

                    var elapsed = TimeSpan.FromSeconds(elapsedSec);
                    var totalEta = TimeSpan.FromSeconds(elapsedSec + Math.Max(0, etaSec));

                    var r = Common.CalculateProgress(totalRead, size, displayName);
                    int pct = size > 0 ? (int)(totalRead * 100 / size) : 0;

                    progress?.Report(new ProgressInfo(
                        Percent: pct,
                        Label: $"{Res.Log_Splitting} {r.label} {typeTag}",
                        TitleId: meta.TitleId,
                        Speed: $"{mibPerSec:F1} MiB/s",
                        TimeInfo: $"{elapsed:mm\\:ss} / {totalEta:mm\\:ss}"
                    ));

                    reportSw.Restart();
                }
            }

            fout.Flush();
            isCompleted = true;
        }
        catch (Exception ex)
        {
            throw new Exception(string.Format(Res.Log_WriteNspFailed, ex.Message), ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);

            if (!isCompleted && File.Exists(outPath))
                try { File.Delete(outPath); } catch { }
        }
    }
}