using System.Globalization;

namespace DiTunnel.Core.Profiles;

public sealed record SubscriptionUsage(long? Upload, long? Download, long? Total, long? Expire)
{
    public decimal? RemainingBytes => Total is > 0 && Upload.HasValue && Download.HasValue
        ? Math.Max(0m, (decimal)Total.Value - Upload.Value - Download.Value) : null;
    public int? RemainingDays(DateTimeOffset now) => Expire is > 0
        ? (int)Math.Clamp(Math.Ceiling((Expire.Value - (decimal)now.ToUnixTimeSeconds()) / 86400m), 0, int.MaxValue) : null;
    public static SubscriptionUsage? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in header.Split(';'))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value)) values[pair[0]] = value;
        }
        long? Get(string key) => values.TryGetValue(key, out var n) ? n : null;
        return new(Get("upload"), Get("download"), Get("total"), Get("expire"));
    }
}
