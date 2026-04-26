namespace MyPortfolio.Common;

public sealed class DiscoveryProgress
{
    public DiscoveryProgress(string stage, int current = 0, int total = 0, string? detail = null)
    {
        Stage = stage;
        Current = current;
        Total = total;
        Detail = detail;
    }

    public string Stage { get; }
    public int Current { get; }
    public int Total { get; }
    public string? Detail { get; }
    public bool IsDeterminate => Total > 0;

    public string Text
    {
        get
        {
            var count = IsDeterminate ? $" {Current:N0}/{Total:N0}" : string.Empty;
            var detail = string.IsNullOrWhiteSpace(Detail) ? string.Empty : $" - {Detail}";
            return $"{Stage}{count}{detail}";
        }
    }
}
