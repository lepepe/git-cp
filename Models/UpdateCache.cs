namespace cp.Models;

public record UpdateCache(DateTimeOffset LastCheckedUtc, string? LatestKnownVersion);
