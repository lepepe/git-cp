namespace cp.Features.Update;

public record UpdateCache(DateTimeOffset LastCheckedUtc, string? LatestKnownVersion);
