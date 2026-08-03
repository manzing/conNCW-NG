using System.CommandLine;
using ConNCW.Core.Bypass;
using ConNCW.Core.Engine;
using ConNCW.Core.Logging;
using ConNCW.Core.Signatures;

namespace ConNCW.Cli;

/// <summary>
/// CLI conNCW, portage de conNCW05.dpr / main.inc.
/// Parité de syntaxe avec l'original : -w2n, -n2w, -r, -rw, -l, -whs/-whe/-wha,
/// plus nouveaux flags --learn-signature et --bypass-signature.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        string exeDir = AppContext.BaseDirectory;
        string signaturesPath = Path.Combine(exeDir, "Signatures", "signatures.json");
        if (!File.Exists(signaturesPath))
        {
            signaturesPath = Path.Combine(exeDir, "signatures.json");
        }

        var rootCommand = new RootCommand("conNCW - conversion NCW <-> WAV (portage C#/.NET 10)");

        var sourceArg = new Argument<string>("source") { Description = "Fichier ou dossier source" };
        var destArg = new Argument<string?>("destination") { Description = "Fichier ou dossier destination", DefaultValueFactory = _ => null };

        var w2nOption = new Option<bool>("-w2n") { Description = "Convertir WAV -> NCW (mode dossier)" };
        var n2wOption = new Option<bool>("-n2w") { Description = "Convertir NCW -> WAV (mode dossier)" };
        var recOption = new Option<bool>("-r", "-rec") { Description = "Traitement récursif des sous-répertoires" };
        var rewriteOption = new Option<bool>("-rw", "-rewrite") { Description = "Écraser les fichiers de destination existants" };
        var listOption = new Option<string?>("-l") { Description = "Mode liste : fichier @conncwlist" };
        var whsOption = new Option<bool>("-whs") { Description = "En-tête WAV standard" };
        var wheOption = new Option<bool>("-whe") { Description = "En-tête WAV étendu" };
        var whaOption = new Option<bool>("-wha") { Description = "En-tête WAV auto" };
        var bypassOption = new Option<bool>("--bypass-signature") { Description = "Forcer le décodage NCW en ignorant la signature (test de plausibilité)" };
        var logOption = new Option<string?>("--log") { Description = "Chemin du fichier de log (par défaut: conncw_<timestamp>.log)" };

        var learnOption = new Option<string?>("--learn-signature") { Description = "Analyse un fichier NCW et propose d'ajouter sa signature à signatures.json" };
        var labelOption = new Option<string?>("--label") { Description = "Label à associer à la signature apprise" };

        foreach (var opt in new Option[] { w2nOption, n2wOption, recOption, rewriteOption, listOption, whsOption, wheOption, whaOption, bypassOption, logOption, learnOption, labelOption })
        {
            rootCommand.Add(opt);
        }
        rootCommand.Add(sourceArg);
        rootCommand.Add(destArg);

        rootCommand.SetAction(parseResult =>
        {
            string? learnPath = parseResult.GetValue(learnOption);
            if (learnPath is not null)
            {
                return RunLearnSignature(learnPath, parseResult.GetValue(labelOption), signaturesPath);
            }

            string source = parseResult.GetValue(sourceArg)!;
            string? dest = parseResult.GetValue(destArg);
            bool w2n = parseResult.GetValue(w2nOption);
            bool n2w = parseResult.GetValue(n2wOption);
            bool recursive = parseResult.GetValue(recOption);
            bool rewrite = parseResult.GetValue(rewriteOption);
            string? listFile = parseResult.GetValue(listOption);
            bool bypass = parseResult.GetValue(bypassOption);
            string? logPath = parseResult.GetValue(logOption);

            return RunConversion(source, dest, w2n, n2w, recursive, rewrite, listFile, bypass, logPath, signaturesPath);
        });

        return rootCommand.Parse(args).Invoke();
    }

    private static int RunLearnSignature(string ncwPath, string? label, string signaturesPath)
    {
        if (!File.Exists(ncwPath))
        {
            Console.Error.WriteLine($"Fichier introuvable : {ncwPath}");
            return 1;
        }

        var preview = SignatureLearner.PreviewSignature(ncwPath);
        Console.WriteLine($"Signature détectée : {preview.HexBytes}");
        Console.WriteLine();
        Console.WriteLine("Avez-vous vérifié que ce fichier se charge correctement");
        Console.WriteLine("(dans Kontakt, ou via --bypass-signature + écoute du WAV de test) ?");
        Console.Write($"Ajouter cette signature à signatures.json ? [y/N] ");
        var answer = Console.ReadLine();

        if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Annulé, aucune modification apportée.");
            return 0;
        }

        string finalLabel = string.IsNullOrWhiteSpace(label)
            ? $"learned-{DateTime.Now:yyyyMMdd-HHmmss}"
            : label!;

        var store = new SignatureStore(signaturesPath);
        SignatureLearner.Confirm(store, preview.SignatureBytes, finalLabel, $"Appris depuis {Path.GetFileName(ncwPath)}");

        Console.WriteLine($"Signature ajoutée avec le label \"{finalLabel}\".");
        return 0;
    }

    private static int RunConversion(
        string source, string? dest, bool w2n, bool n2w, bool recursive, bool rewrite,
        string? listFile, bool bypass, string? logPath, string signaturesPath)
    {
        var signatures = new SignatureStore(signaturesPath);
        string effectiveLogPath = logPath ?? $"conncw_{DateTime.Now:yyyyMMdd_HHmmss}.log";

        using var logger = new ConversionLogger(effectiveLogPath);

        if (bypass)
        {
            RunBypassTest(source, signatures);
            logger.WriteSummary();
            return 0;
        }

        var engine = new ConversionEngine(signatures, logger);

        if (listFile is not null)
        {
            RunListMode(listFile, engine, recursive, rewrite);
        }
        else if (Directory.Exists(source))
        {
            if (!w2n && !n2w)
            {
                Console.Error.WriteLine("Mode dossier : préciser -w2n ou -n2w.");
                return 1;
            }
            if (dest is null)
            {
                Console.Error.WriteLine("Mode dossier : destination requise.");
                return 1;
            }

            var direction = n2w ? ConversionDirection.NcwToWav : ConversionDirection.WavToNcw;
            var options = new ConversionOptions { Direction = direction, Recursive = recursive, Rewrite = rewrite };
            engine.ConvertDirectory(source, dest, options);
        }
        else if (File.Exists(source))
        {
            string ext = Path.GetExtension(source).ToLowerInvariant();
            var direction = ext == ".ncw" ? ConversionDirection.NcwToWav : ConversionDirection.WavToNcw;
            string destPath = dest ?? Path.ChangeExtension(source, direction == ConversionDirection.NcwToWav ? ".wav" : ".ncw");

            var options = new ConversionOptions { Direction = direction, Rewrite = rewrite };
            engine.ConvertFile(source, destPath, options);
        }
        else
        {
            Console.Error.WriteLine($"Source introuvable : {source}");
            return 1;
        }

        logger.WriteSummary();
        return 0;
    }

    private static void RunListMode(string listFile, ConversionEngine engine, bool recursive, bool rewrite)
    {
        string actualListPath = listFile.StartsWith("@") ? listFile[1..] : listFile;
        if (!File.Exists(actualListPath))
        {
            Console.Error.WriteLine($"Fichier de liste introuvable : {actualListPath}");
            return;
        }

        foreach (var line in File.ReadLines(actualListPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#"))
            {
                continue;
            }

            var parts = trimmed.Split('|', 2);
            string src = parts[0].Trim();
            string? dst = parts.Length > 1 ? parts[1].Trim() : null;

            if (!File.Exists(src))
            {
                continue;
            }

            string ext = Path.GetExtension(src).ToLowerInvariant();
            var direction = ext == ".ncw" ? ConversionDirection.NcwToWav : ConversionDirection.WavToNcw;
            string destPath = dst ?? Path.ChangeExtension(src, direction == ConversionDirection.NcwToWav ? ".wav" : ".ncw");

            var options = new ConversionOptions { Direction = direction, Recursive = recursive, Rewrite = rewrite };
            engine.ConvertFile(src, destPath, options);
        }
    }

    private static void RunBypassTest(string source, SignatureStore signatures)
    {
        string testDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".", "_bypass_test");

        IEnumerable<string> files = File.Exists(source)
            ? new[] { source }
            : Directory.EnumerateFiles(source, "*.ncw", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            Console.WriteLine($"--- Test bypass : {file} ---");
            try
            {
                var result = BypassRunner.Run(file, signatures, testDir);
                Console.WriteLine($"  Signature      : {result.SignatureHex}");
                Console.WriteLine($"  Score confiance: {result.ConfidenceScore:P0}");
                Console.WriteLine($"  WAV de test    : {result.TestWavPath}");

                if (result.Warnings.Count > 0)
                {
                    Console.WriteLine("  Avertissements :");
                    foreach (var w in result.Warnings)
                    {
                        Console.WriteLine($"    - {w}");
                    }
                }
                else
                {
                    Console.WriteLine("  Aucun avertissement, décodage plausible.");
                }

                Console.WriteLine("  => Écoutez ce WAV, puis lancez --learn-signature si valide.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ECHEC test bypass : {ex.Message}");
            }
            Console.WriteLine();
        }
    }
}
