using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Ncm;
using LibHac.Ns;
using LibHac.NSZ;
using LibHac.Tools.Es;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Tools.Ncm;
using NSW.Core.Models;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using static LibHac.Ns.ApplicationControlProperty;

namespace NSW.Core;

public static class LibHacHelper
{
    private static readonly string[] sourceArray = ["B", "U", "D"];

    public static Language Current
    {
        get
        {
            var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

            return lang switch
            {
                "en" => Language.AmericanEnglish,
                "de" => Language.German,
                "es" => Language.Spanish,
                "fr" => Language.French,
                "ja" => Language.Japanese,
                "ko" => Language.Korean,
                "ru" => Language.Russian,
                "zh" => GetChineseVariant,
                _ => Language.AmericanEnglish
            };
        }
    }

    private static Language GetChineseVariant
    {
        get
        {
            var culture = CultureInfo.CurrentUICulture.Name;

            if (culture.StartsWith("zh-TW") || culture.StartsWith("zh-HK"))
                return Language.TraditionalChinese;

            return Language.SimplifiedChinese;
        }
    }

    public static string GetContentMetaTypeTag(ContentMetaType type) => type switch
    {
        ContentMetaType.Application => "Base",
        ContentMetaType.Patch => "Update",
        ContentMetaType.AddOnContent => "DLC",
        ContentMetaType.Delta => "DLC",
        _ => "?"
    };

    public static IFileSystem OpenFileSystem(this LocalStorage storage, KeySet ks, string path)
    {
        IFileSystem fs;
        string ext = System.IO.Path.GetExtension(path).ToLower();        

        if (ext == ".xci" || ext == ".xcz")
            fs = new Xci(ks, storage).OpenPartition(XciPartitionType.Secure);
        else if (ext == ".nsp" || ext == ".nsz")
        {
            var nsp = new PartitionFileSystem();
            nsp.Initialize(storage).ThrowIfFailure();
            fs = nsp;
        }
        else
            throw new NotSupportedException("지원하지 않는 ROM 형식입니다.");

        return fs;
    }

    public static void RegisterTickets(this KeySet ks, IFileSystem fs)
    {
        foreach (var entry in fs.EnumerateEntries("/", "*.tik"))
        {
            using var tikFile = new UniqueRef<IFile>();
            if (fs.OpenFile(ref tikFile.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).IsSuccess())
            {
                var ticket = new Ticket(tikFile.Get.AsStream());
                var rightsId = new RightsId(ticket.RightsId);
                if (!ks.ExternalKeySet.Contains(rightsId))
                    ks.ExternalKeySet.Add(rightsId, new LibHac.Spl.AccessKey(ticket.GetTitleKey(ks)));
            }
        }
    }

    public static Stream GetDecodedStream(IFile file, string fileName, KeySet keySet)
    {
        var stream = file.AsStream();
        if (System.IO.Path.GetExtension(fileName).Equals(".ncz", StringComparison.CurrentCultureIgnoreCase))
        {
            var ncz = new Ncz(keySet, stream, NczReadMode.Original);
            return ncz.BaseStorage.AsStream();
        }
        return stream;
    }

    public static string DetectFileType(KeySet keySet, string path)
    {
        if (keySet == null) return "Not Key";
        try
        {
            using var storage = new LocalStorage(path, FileAccess.Read);
            using var fs = storage.OpenFileSystem(keySet, path);

            var foundTypes = new HashSet<string>();
            var entries = fs.EnumerateEntries("/", "*.nca")
                .Concat(fs.EnumerateEntries("/", "*.ncz"))
                .Where(e => e.Type == DirectoryEntryType.File);

            foreach (var entry in entries)
            {
                using var file = new UniqueRef<IFile>();
                if (fs.OpenFile(ref file.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).IsFailure()) continue;

                var cnmt = file.Get.GetCnmtFromNca(keySet);
                if (cnmt != null)
                {
                    var letter = GetContentMetaTypeTag(cnmt.Type)[..1];
                    if (letter != "?") foundTypes.Add(letter);
                }
            }

            return foundTypes.Count == 0 ? "?" : string.Join("+", sourceArray.Where(foundTypes.Contains));
        }
        catch { return "?"; }
    }

    public static GameFileInfo GetGameFileInfo(KeySet keySet, string path)
    {
        var info = new GameFileInfo { Path = path };
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return info;

        try
        {
            using var storage = new LocalStorage(path, FileAccess.Read);
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            using IFileSystem fs = storage.OpenFileSystem(keySet, path);

            var types = new HashSet<string>();
            var entries = fs.EnumerateEntries("/", "*.nca").ToList();

            foreach (var entry in entries)
            {
                using var file = new UniqueRef<IFile>();
                if (fs.OpenFile(ref file.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).IsFailure()) continue;
                var nca = new Nca(keySet, file.Release().AsStorage());

                if (nca.Header.ContentType == NcaContentType.Meta)
                {
                    using var ncaFs = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
                    foreach (var cnmtEntry in ncaFs.EnumerateEntries("/", "*.cnmt"))
                    {
                        using var cnmtFile = new UniqueRef<IFile>();
                        if (ncaFs.OpenFile(ref cnmtFile.Ref, cnmtEntry.FullPath.ToU8Span(), OpenMode.Read).IsSuccess())
                        {
                            var cnmt = new Cnmt(cnmtFile.Get.AsStream());
                            info.TitleId = cnmt.TitleId.ToString("X16");
                            string? t = GetContentMetaTypeTag(cnmt.Type)[..1];

                            if (t != null) types.Add(t);
                        }
                    }
                }
                else if (nca.Header.ContentType == NcaContentType.Control)
                {
                    using var cFs = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
                    using var nacpFile = new UniqueRef<IFile>();

                    if (cFs.OpenFile(ref nacpFile.Ref, "/control.nacp".ToU8Span(), OpenMode.Read).IsSuccess())
                    {
                        var control = new ApplicationControlProperty();
                        nacpFile.Get.Read(out _, 0, SpanHelpers.AsByteSpan(ref control)).ThrowIfFailure();
                        info.DisplayVersion = control.DisplayVersionString.ToString().Trim('\0').Trim();
                        var (titleName, developer, language) = control.GetTitleByLanguage(Current);
                        info.TitleName = titleName;
                        info.Developer = developer;
                        info.IconData = cFs.GetIconData(language);
                    }
                }
            }
            info.Type = types.Count == 0 ? "?" : string.Join("+", sourceArray.Where(types.Contains));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
        return info;
    }

    public static List<MetadataResult> GetMetadataFromContainer(KeySet keySet, string path)
    {
        var results = new List<MetadataResult>();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return results;

        using var storage = new LocalStorage(path, FileAccess.Read);
        IFileSystem fs = storage.OpenFileSystem(keySet, path);

        var allFiles = new Dictionary<string, IStorage>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in fs.EnumerateEntries("/", "*"))
        {
            var file = new UniqueRef<IFile>();
            if (fs.OpenFile(ref file.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).IsSuccess())
                allFiles[entry.Name] = file.Release().AsStream().AsStorage();
        }

        foreach (var cnmtNcaName in allFiles.Keys.Where(k =>
            k.EndsWith(".cnmt.nca", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var ncaFile = allFiles[cnmtNcaName].AsFile(OpenMode.Read);

                var cnmt = ncaFile.GetCnmtFromNca(keySet);
                if (cnmt == null) continue;

                string titleId = cnmt.TitleId.ToString("X16");
                uint version = cnmt.TitleVersion.Version;
                ContentMetaType type = cnmt.Type;

                var contentNcaIds = cnmt.ContentEntries
                    .Select(e => BitConverter.ToString(e.NcaId).Replace("-", "").ToLowerInvariant())
                    .ToList();

                string selfNcaId = System.IO.Path.GetFileNameWithoutExtension(System.IO.Path.GetFileNameWithoutExtension(cnmtNcaName));
                if (!string.IsNullOrEmpty(selfNcaId))
                    contentNcaIds.Add(selfNcaId.ToLowerInvariant());

                string krTitle = string.Empty;
                string enTitle = string.Empty;
                string displayVer = "1.0.0";

                var ctrlRecord = cnmt.ContentEntries
                    .FirstOrDefault(x => x.Type == LibHac.Ncm.ContentType.Control);

                if (ctrlRecord != null)
                {
                    string ctrlId = BitConverter.ToString(ctrlRecord.NcaId).Replace("-", string.Empty).ToLowerInvariant();

                    string? ctrlName = allFiles.Keys.FirstOrDefault(k =>
                        k.StartsWith(ctrlId, StringComparison.OrdinalIgnoreCase));

                    if (ctrlName != null)
                    {
                        var control = allFiles[ctrlName].GetControlProperty(keySet);
                        if (control != null)
                        {
                            var current = control.Value;

                            krTitle = current.Title[(int)Current].NameString.ToString().Trim('\0', ' ');
                            enTitle = current.Title[(int)Language.AmericanEnglish].NameString.ToString().Trim('\0', ' ');

                            if (string.IsNullOrWhiteSpace(krTitle))
                            {
                                foreach (ApplicationTitle t in current.Title)
                                {
                                    var name = t.NameString.ToString().Trim('\0', ' ');
                                    if (!string.IsNullOrWhiteSpace(name)) { krTitle = name; break; }
                                }
                            }
                            if (string.IsNullOrWhiteSpace(enTitle)) enTitle = krTitle;

                            displayVer = current.DisplayVersionString.ToString().Trim('\0', ' ');
                        }
                    }
                }

                results.Add(new MetadataResult(titleId, version, displayVer, krTitle, enTitle, 0, type, cnmtNcaName, path, contentNcaIds));
            }
            catch
            { throw; }
        }

        foreach (var s in allFiles.Values) s.Dispose();
        if (fs is IDisposable d) d.Dispose();

        return results;
    }

    public static Cnmt? GetCnmtFromNca(this IFile ncaFile, KeySet ks)
    {
        try
        {
            var nca = new Nca(ks, ncaFile.AsStorage());
            if (nca.Header.ContentType != NcaContentType.Meta) return null;

            using var metaFs = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
            var cnmtEntry = metaFs.EnumerateEntries("/", "*.cnmt").FirstOrDefault();
            if (cnmtEntry == null) return null;

            using var cnmtFile = new UniqueRef<IFile>();
            if (metaFs.OpenFile(ref cnmtFile.Ref, cnmtEntry.FullPath.ToU8Span(), OpenMode.Read).IsFailure())
                return null;

            return new Cnmt(cnmtFile.Get.AsStream());
        }
        catch { return null; }
    }

    public static Cnmt? GetCnmtFromNca(this IStorage ncaStorage, KeySet ks)
    {
        try
        {
            var nca = new Nca(ks, ncaStorage);
            if (nca.Header.ContentType != NcaContentType.Meta) return null;

            using var metaFs = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
            var cnmtEntry = metaFs.EnumerateEntries("/", "*.cnmt").FirstOrDefault();
            if (cnmtEntry == null) return null;

            using var cnmtFile = new UniqueRef<IFile>();
            if (metaFs.OpenFile(ref cnmtFile.Ref, cnmtEntry.FullPath.ToU8Span(), OpenMode.Read).IsFailure())
                return null;

            return new Cnmt(cnmtFile.Get.AsStream());
        }
        catch { return null; }
    }

    public static ApplicationControlProperty? GetControlProperty(this IStorage ncaStorage, KeySet ks)
    {
        try
        {
            var nca = new Nca(ks, ncaStorage);
            using var ctrlFs = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
            if (!ctrlFs.FileExists("/control.nacp")) return null;

            using var nFile = new UniqueRef<IFile>();
            ctrlFs.OpenFile(ref nFile.Ref, "/control.nacp".ToU8Span(), OpenMode.Read).ThrowIfFailure();

            var control = new ApplicationControlProperty();
            nFile.Get.Read(out _, 0, SpanHelpers.AsByteSpan(ref control)).ThrowIfFailure();
            return control;
        }
        catch { return null; }
    }

    public static byte[]? GetIconData(this IFileSystem cFs, Language lang)
    {
        string iconPath = $"/icon_{lang}.dat";
        if (!cFs.FileExists(iconPath)) return null;

        using var logoFile = new UniqueRef<IFile>();
        if (cFs.OpenFile(ref logoFile.Ref, iconPath.ToU8Span(), OpenMode.Read).IsFailure()) return null;

        logoFile.Get.GetSize(out long size).ThrowIfFailure();
        byte[] iconBuf = new byte[size];
        logoFile.Get.Read(out _, 0, iconBuf).ThrowIfFailure();
        return iconBuf;
    }

    public static string GetTitleIdFromRom(string path)
    {
        var keySet = KeySetProvider.Instance.KeySet;
        using var storage = new LocalStorage(path, FileAccess.Read);
        IFileSystem fs = storage.OpenFileSystem(keySet, path);
        var entries = fs.EnumerateEntries("/", "*.nca")
            .Concat(fs.EnumerateEntries("/", "*.ncz"))
            .Where(e => e.Type == DirectoryEntryType.File);

        foreach (var entry in entries)
        {
            using var ncaFile = new UniqueRef<IFile>();
            if (fs.OpenFile(ref ncaFile.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).IsFailure()) continue;
            try
            {
                bool isNcz = entry.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase);
                Nca nca = isNcz
                    ? new Ncz(keySet, ncaFile.Release().AsStream(), NczReadMode.Fast)
                    : new Nca(keySet, ncaFile.Release().AsStorage());

                if (nca.Header.ContentType == NcaContentType.Program)
                    return nca.Header.TitleId.ToString("X16");
            }
            catch { continue; }
        }

        throw new Exception("Title ID를 찾을 수 없습니다.");
    }

    public static (byte KeyGeneration, uint SdkVersion) ReadControlNcaInfo(string ncaPath)
    {
        if (!File.Exists(ncaPath))
            throw new FileNotFoundException("control.nca 파일이 없습니다.", ncaPath);

        using var controlFs = new FileStream(ncaPath, FileMode.Open, FileAccess.Read);
        var storage = new StreamStorage(controlFs, false);
        var nca = new Nca(KeySetProvider.Instance.KeySet, storage);

        return (nca.Header.KeyGeneration, nca.Header.SdkVersion.Version);
    }

    public static (string KrTitle, string EnTitle, string DisplayVersion, ulong TitleId) ReadNacpInfo(string nacpPath)
    {
        if (!File.Exists(nacpPath))
            throw new FileNotFoundException("control.nacp 파일이 없습니다.", nacpPath);

        byte[] nacpData = File.ReadAllBytes(nacpPath);
        var control = new ApplicationControlProperty();
        nacpData.AsSpan().CopyTo(SpanHelpers.AsByteSpan(ref control));

        var titles = control.Title;
        int langCount = control.TitleCompression == TitleCompressionValue.Enable
            ? Constants.ExtendedLanguageCount
            : Constants.LegacyLanguageCount;

        ulong titleId = BinaryPrimitives.ReadUInt64LittleEndian(nacpData.AsSpan(0x30B0));
        string displayVersion = Encoding.UTF8.GetString(nacpData, 0x3060, 0x10).TrimEnd('\0');

        string enTitle = titles[(int)Language.AmericanEnglish].NameString.ToString().Trim('\0', ' ');
        string krTitle = titles[(int)Language.Korean].NameString.ToString().Trim('\0', ' ');

        if (string.IsNullOrWhiteSpace(krTitle) && string.IsNullOrWhiteSpace(enTitle))
        {
            for (int i = 0; i < langCount; i++)
            {
                string fallback = titles[i].NameString.ToString().Trim('\0', ' ');
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    krTitle = enTitle = fallback;
                    break;
                }
            }
        }
        else if (string.IsNullOrWhiteSpace(krTitle)) krTitle = enTitle;
        else if (string.IsNullOrWhiteSpace(enTitle)) enTitle = krTitle;

        return (krTitle, enTitle, displayVersion, titleId);
    }

    private static (string Name, string Publisher, Language Language) GetTitleByLanguage(
        this ApplicationControlProperty control, Language preferred)
    {
        int langCount = control.TitleCompression == TitleCompressionValue.Enable
            ? Constants.ExtendedLanguageCount
            : Constants.LegacyLanguageCount;

        var titles = control.Title;

        var title = titles[(int)preferred];
        if (!string.IsNullOrWhiteSpace(title.NameString.ToString().Trim('\0')))
            return (title.NameString.ToString().Trim('\0'), title.PublisherString.ToString().Trim('\0'), preferred);

        title = titles[(int)Language.AmericanEnglish];
        if (!string.IsNullOrWhiteSpace(title.NameString.ToString().Trim('\0')))
            return (title.NameString.ToString().Trim('\0'), title.PublisherString.ToString().Trim('\0'), Language.AmericanEnglish);

        for (int i = 0; i < langCount; i++)
        {
            var t = titles[i];
            var name = t.NameString.ToString().Trim('\0');
            if (!string.IsNullOrWhiteSpace(name))
                return (name, t.PublisherString.ToString().Trim('\0'), (Language)i);
        }

        return ("Unknown", "Unknown", preferred);
    }
}