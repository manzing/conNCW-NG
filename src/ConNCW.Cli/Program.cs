using System.CommandLine;
using ConNCW.Core.Bypass;
using ConNCW.Core.Engine;
using ConNCW.Core.Logging;
using ConNCW.Core.Ncw;
using ConNCW.Core.Signatures;

namespace ConNCW.Cli;

/// <summary>
/// conNCW-NG CLI, port of conNCW / main.inc.
/// Syntax parity with the original: -w2n, -n2w, -r, -rw, -l, -whs/-whe/-wha,
/// plus new flags --learn-signature, --bypass-signature and --inventory.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        args = CommandLineFix.GetCorrectedArgs();
        string exeDir = AppContext.BaseDirectory;
        string signaturesPath = Path.Combine(exeDir, "Signatures", "signatures.json");
        if (!File.Exists(signaturesPath))
        {
            signaturesPath = Path.Combine(exeDir, "signatures.json");
        }

        var rootCommand = new RootCommand("conNCW - NCW <-> WAV conversion (C#/.NET 10 port)");
        var sourceArg = new Argument<string>("source") { Description = "Source file or folder" };
        var destArg = new Argument<string?>("destination") { Description = "Destination file or folder", DefaultValueFactory = _ => null };
        var w2nOption = new Option<bool>("-w2n") { Description = "Convert WAV -> NCW (folder mode)" };
        var n2wOption = new Option<bool>("-n2w") { Description = "Convert NCW -> WAV (folder mode)" };
        var recOption = new Option<bool>("-r", "-rec") { Description = "Recursive processing of subdirectories" };
        var rewriteOption = new Option<bool>("-rw", "-rewrite") { Description = "Overwrite existing destination files" };
        var listOption = new Option<string?>("-l") { Description = "List mode: @conncwlist file" };
        var whsOption = new Option<bool>("-whs") { Description = "Standard WAV header" };
        var wheOption = new Option<bool>("-whe") { Description = "Extended WAV header" };
        var whaOption = new Option<bool>("-wha") { Description = "Auto WAV header" };
        var bypassOption = new Option<bool>("--bypass-signature") { Description = "Force NCW decoding while ignoring the signature (plausibility test)" };
        var logOption = new Option<string?>("--log") { Description = "Log file path (default: conncw_<timestamp>.log)" };
        var learnOption = new Option<string?>("--learn-signature") { Description = "Analyzes an NCW file and offers to add its signature to signatures.json" };
        var labelOption = new Option<string?>("--label") { Description = "Label to associate with the learned signature" };

        // --- Nouveau : mode inventaire (header-only, aucun décodage audio) ---
        var inventoryOption = new Option<bool>("--inventory")
        {
            Description = "Scan a folder of .ncw files and export a CSV inventory (channels, bits, sample rate, sample count, duration) without decoding audio. Fast, header-only scan. Use with 'source' as the root folder and --output for the CSV path."
        };
        var outputOption = new Option<string?>("--output")
        {
            Description = "CSV output path for --inventory mode (default: ncw_inventory_<timestamp>.csv)"
        };

        foreach (var opt in new Option[]
        {
            w2nOption, n2wOption, recOption, rewriteOption, listOption, whsOption, wheOption, whaOption,
            bypassOption, logOption, learnOption, labelOption, inventoryOption, outputOption
        })
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

            // --- Nouveau : interception du mode inventaire avant le flux de conversion normal ---
            bool inventory = parseResult.GetValue(inventoryOption);
            if (inventory)
            {
                string inventorySource = parseResult.GetValue(sourceArg)!;
                string outputCsv = parseResult.GetValue(outputOption)
                    ?? $"ncw_inventory_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                return RunInventory(inventorySource, outputCsv, signaturesPath);
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

    // --- Nouveau : point d'entrée du mode inventaire ---
    private static int RunInventory(string source, string outputCsv, string signaturesPath)
    {
        if (!Directory.Exists(source))
        {
            Console.Error.WriteLine($"--inventory requires 'source' to be an existing folder: {source}");
            return 1;
        }

        var signatures = new SignatureStore(signaturesPath);

        try
        {
            NcwInventoryCommand.Run(source, outputCsv, signatures);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Inventory failed: {ex.Message}");
            return 1;
        }
    }

    private static int RunLearnSignature(string ncwPath, string? label, string signaturesPath)
    {
        if (!File.Exists(ncwPath))
        {
            Console.Error.WriteLine($"File not found: {ncwPath}");
            return 1;
        }

        var preview = SignatureLearner.PreviewSignature(ncwPath);
        Console.WriteLine($"Detected signature: {preview.HexBytes}");
        Console.WriteLine();
        Console.WriteLine("Have you verified that this file loads correctly");
        Console.WriteLine("(in Kontakt, or via --bypass-signature + listening to the test WAV)?");
        Console.Write($"Add this signature to signatures.json? [y/N] ");
        var answer = Console.ReadLine();

        if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Cancelled, no changes made.");
            return 0;
        }

        string finalLabel = string.IsNullOrWhiteSpace(label)
            ? $"learned-{DateTime.Now:yyyyMMdd-HHmmss}"
            : label!;

        var store = new SignatureStore(signaturesPath);
        SignatureLearner.Confirm(store, preview.SignatureBytes, finalLabel, $"Learned from {Path.GetFileName(ncwPath)}");

        Console.WriteLine($"Signature added with label \"{finalLabel}\".");
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
                Console.Error.WriteLine("Folder mode: specify -w2n or -n2w.");
                return 1;
            }

            if (dest is null)
            {
                Console.Error.WriteLine("Folder mode: destination required.");
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
            Console.Error.WriteLine($"Source not found: {source}");
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
            Console.Error.WriteLine($"List file not found: {actualListPath}");
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
            Console.WriteLine($"--- Bypass test: {file} ---");
            try
            {
                var result = BypassRunner.Run(file, signatures, testDir);
                Console.WriteLine($"  Signature       : {result.SignatureHex}");
                Console.WriteLine($"  Confidence score: {result.ConfidenceScore:P0}");
                Console.WriteLine($"  Test WAV        : {result.TestWavPath}");

                if (result.Warnings.Count > 0)
                {
                    Console.WriteLine("  Warnings:");
                    foreach (var w in result.Warnings)
                    {
                        Console.WriteLine($"   - {w}");
                    }
                }
                else
                {
                    Console.WriteLine("  No warnings, plausible decoding.");
                }

                Console.WriteLine("  => Listen to this WAV, then run --learn-signature if valid.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  BYPASS TEST FAILED: {ex.Message}");
            }

            Console.WriteLine();
        }
    }
}
