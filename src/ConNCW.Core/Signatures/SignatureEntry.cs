namespace ConNCW.Core.Signatures;

public sealed class SignatureEntry
{
    public string Label { get; set; } = string.Empty;
    public string HexBytes { get; set; } = string.Empty;   // ex: "01A89ED631010000"
    public string Source { get; set; } = "known";           // "known" | "learned"
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Notes { get; set; }

    public byte[] ToBytes() => Convert.FromHexString(HexBytes.Replace(" ", string.Empty));
}
