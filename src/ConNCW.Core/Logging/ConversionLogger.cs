using System.Text;
using ConNCW.Core.Models;

namespace ConNCW.Core.Logging;

/// <summary>
/// Logging console (défilement en direct, comme l'original) + fichier de log persistant
/// (nouveauté demandée : trace de tous les fichiers, notamment les échecs).
/// </summary>
public sealed class ConversionLogger : IDisposable
{
    private readonly StreamWriter _fileWriter;
    private readonly List<ConversionResult> _failures = new();
    private int _okCount;
    private int _toleratedCount;
    private int _totalCount;

    public ConversionLogger(string logFilePath)
    {
        string? dir = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        _fileWriter = new StreamWriter(logFilePath, append: false, Encoding.UTF8) { AutoFlush = true };
        _fileWriter.WriteLine($"=== conNCW log — démarré le {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} ===");
    }

    public void Report(ConversionResult result)
    {
        _totalCount++;
        string line = FormatLine(result);

        // Console : défilement en direct, comme le comportement original.
        Console.WriteLine(line);

        _fileWriter.WriteLine(line);

        switch (result.Status)
        {
            case ConversionStatus.Ok:
                _okCount++;
                break;
            case ConversionStatus.OkToleratedSignature:
            case ConversionStatus.OkBypassTest:
                _okCount++;
                _toleratedCount++;
                break;
            default:
                _failures.Add(result);
                break;
        }
    }

    private static string FormatLine(ConversionResult r)
    {
        string status = r.Status switch
        {
            ConversionStatus.Ok => "OK",
            ConversionStatus.OkToleratedSignature => "OK (signature tolérée)",
            ConversionStatus.OkBypassTest => "OK (test bypass)",
            ConversionStatus.FailUnknownSignature => "ECHEC (signature inconnue)",
            ConversionStatus.FailCorruptData => "ECHEC (données corrompues)",
            ConversionStatus.FailIoError => "ECHEC (erreur E/S)",
            _ => "?"
        };

        string sig = r.SignatureHex is not null ? $" [sig: {r.SignatureHex}]" : string.Empty;
        string msg = r.Message is not null ? $" — {r.Message}" : string.Empty;
        return $"[{DateTimeOffset.Now:HH:mm:ss}] {status,-28} {r.SourcePath}{sig}{msg}";
    }

    /// <summary>
    /// Affiche le récapitulatif final : conservé à l'écran (nouveauté) plutôt que
    /// simplement défilé et perdu, plus écrit dans le fichier de log.
    /// </summary>
    public void WriteSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("=== Résumé ===");
        sb.AppendLine($"Total traités   : {_totalCount}");
        sb.AppendLine($"Réussis         : {_okCount} (dont {_toleratedCount} en mode toléré/bypass)");
        sb.AppendLine($"Échecs          : {_failures.Count}");

        if (_failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Fichiers en échec :");
            foreach (var f in _failures)
            {
                sb.AppendLine($"  - {f.SourcePath} ({f.Status}){(f.SignatureHex is not null ? $" [sig: {f.SignatureHex}]" : string.Empty)}");
            }
        }

        string summary = sb.ToString();

        // Conservé à l'écran en fin d'exécution (nouveauté), pas juste défilé.
        Console.WriteLine(summary);
        _fileWriter.WriteLine(summary);
    }

    public void Dispose()
    {
        _fileWriter.Flush();
        _fileWriter.Dispose();
    }
}
