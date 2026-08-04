using ConNCW.Core.Ncw;
using ConNCW.Core.Signatures;

namespace ConNCW.Cli;

/// <summary>
/// Nouvelle sous-commande CLI : scanne récursivement une arborescence de fichiers .ncw
/// et exporte un CSV (chemin, channels, bits, sample rate, nombre d'échantillons, durée,
/// signature reconnue, statut). Ne décode aucun échantillon audio : lecture du header
/// seul via NcwFile.ReadHeaderOnly(), donc très rapide même sur de très grosses librairies.
///
/// Usage prévu (à brancher dans le point d'entrée principal du CLI, ex. Program.cs) :
///   conncw inventory --path "D:\Libraries\MaLib" --output "inventaire.csv"
/// </summary>
public static class NcwInventoryCommand
{
    public static void Run(string rootPath, string outputCsvPath, SignatureStore signatures)
    {
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"Dossier introuvable : {rootPath}");
        }

        using var writer = new StreamWriter(outputCsvPath, append: false);
        writer.WriteLine("Path,Channels,Bits,SampleRate,NumSamples,DurationSec,Signature,Status");

        int ok = 0, errors = 0;

        foreach (var file in Directory.EnumerateFiles(rootPath, "*.ncw", SearchOption.AllDirectories))
        {
            try
            {
                // bypassSignature=true : on veut inventorier même les fichiers dont la
                // signature n'est pas (encore) répertoriée dans le SignatureStore, tant
                // que le header reste structurellement plausible.
                var header = NcwFile.ReadHeaderOnly(file, signatures, bypassSignature: true);

                double durationSec = header.SampleRate > 0
                    ? (double)header.NumSamples / header.SampleRate
                    : 0d;

                writer.WriteLine(string.Join(",",
                    EscapeCsv(file),
                    header.Channels,
                    header.Bits,
                    header.SampleRate,
                    header.NumSamples,
                    durationSec.ToString("F3"),
                    EscapeCsv(header.MatchedSignatureLabel ?? "unknown"),
                    "OK"));

                ok++;
            }
            catch (Exception ex)
            {
                writer.WriteLine(string.Join(",",
                    EscapeCsv(file),
                    "", "", "", "", "",
                    EscapeCsv(ex.GetType().Name),
                    "ERROR"));

                errors++;
            }
        }

        writer.Flush();
        Console.WriteLine($"Inventaire terminé : {ok} fichier(s) OK, {errors} erreur(s). Résultat : {outputCsvPath}");
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }
}
