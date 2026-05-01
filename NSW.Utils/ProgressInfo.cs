namespace NSW.Utils;

public record struct ProgressInfo(
    int Percent,
    string Label,
    string TitleId,
    string Speed,
    string TimeInfo
);