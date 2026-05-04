using System.Collections.Generic;
using NSW.Core.Models;

namespace NSW.Avalonia.Services;

public sealed record BuildRequest(string BaseFilePath, string UpdateFilePath, IReadOnlyList<string> DlcFilePaths, string OutputDir)
{
    public bool UseCompression { get; set; } = true;

    public int CompressionLevel { get; set; } = 18;

    public bool UseBlockMode { get; set; }

    public IReadOnlyList<string>? AllSourcePaths { get; set; }

    public HashSet<string>? AllowedNcaIds { get; set; }

    public string? TargetBaseTitleId { get; set; }

    public string? TargetBaseTitleName { get; set; }

    public MetadataResult? ResolvedMeta { get; set; }
}