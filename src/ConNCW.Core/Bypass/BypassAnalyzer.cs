using ConNCW.Core.Models;

namespace ConNCW.Core.Bypass;

/// <summary>
/// Analyse heuristique d'un flux PCM décodé en mode --bypass-signature,
/// pour estimer la plausibilité du décodage avant confirmation à l'oreille.
/// </summary>
public static class BypassAnalyzer
{
    public sealed record BypassReport(double ConfidenceScore, IReadOnlyList<string> Warnings);

    public static BypassReport Analyze(int[] interleavedSamples, AudioFormat format)
    {
        var warnings = new List<string>();
        double score = 1.0;

        if (interleavedSamples.Length == 0)
        {
            return new BypassReport(0.0, new[] { "Aucun échantillon décodé." });
        }

        long max = interleavedSamples[0], min = interleavedSamples[0];
        long silentCount = 0;
        long clippedCount = 0;
        long fullScale = (1L << (format.BitsPerSample - 1)) - 1;

        foreach (int s in interleavedSamples)
        {
            if (s > max) max = s;
            if (s < min) min = s;
            if (s == 0) silentCount++;
            if (Math.Abs(s) >= fullScale) clippedCount++;
        }

        double silentRatio = (double)silentCount / interleavedSamples.Length;
        double clippedRatio = (double)clippedCount / interleavedSamples.Length;

        if (silentRatio > 0.98)
        {
            warnings.Add($"Taux de silence anormal ({silentRatio:P1}) : possible désalignement de blocs.");
            score -= 0.5;
        }

        if (clippedRatio > 0.05)
        {
            warnings.Add($"Taux de clipping élevé ({clippedRatio:P1}) : possible désalignement bit-à-bit.");
            score -= 0.3;
        }

        long theoreticalMax = (1L << (format.BitsPerSample - 1));
        if (max > theoreticalMax || min < -theoreticalMax)
        {
            warnings.Add("Débordement de la plage attendue pour le bit-depth déclaré.");
            score -= 0.4;
        }

        if (!format.IsPlausible())
        {
            warnings.Add("Header jugé implausible (channels/bits/samplerate).");
            score -= 0.3;
        }

        score = Math.Clamp(score, 0.0, 1.0);
        return new BypassReport(score, warnings);
    }
}
