namespace ConNCW.Core.Models;

/// <summary>
/// Représente les caractéristiques audio communes à un fichier NCW ou WAV
/// (portage de TMyWAVHeader / header NCW de conNCW05).
/// </summary>
public sealed record AudioFormat
{
    public required ushort Channels { get; init; }
    public required ushort BitsPerSample { get; init; }
    public required uint SampleRate { get; init; }
    public required uint SampleCount { get; init; }

    /// <summary>Validation structurelle générique, utilisée en mode tolérant / bypass.</summary>
    public bool IsPlausible()
    {
        bool channelsOk = Channels is >= 1 and <= 8;
        bool bitsOk = BitsPerSample is 8 or 16 or 24 or 32;
        bool rateOk = SampleRate is >= 4000 and <= 384000;
        bool countOk = SampleCount > 0;
        return channelsOk && bitsOk && rateOk && countOk;
    }
}
