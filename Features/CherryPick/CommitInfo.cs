namespace cp.Features.CherryPick;

public record CommitInfo(string Hash, string ShortHash, string Author, string Date, string Message)
{
    public override string ToString() => $"{ShortHash} {Date} {Author,-20} {Message}";
}
