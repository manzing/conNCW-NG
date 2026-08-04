using System.Text;
using ConNCW.Core.Models;

namespace ConNCW.Core.Logging;

/// <summary>
/// Console logging with a two-row live display:
///   Row 1 (printed once per folder change, not per file — negligible cost):
///     "Folder: <current folder being converted>"
///   Row 2 (overwritten in place for every file, via \r, no cursor repositioning):
///     "[HH:mm:ss] STATUS   <file name>"
/// Plus a persistent log file limited to failures (full detail, untruncated),
/// to avoid saturating the log file on batches of several thousand
/// mostly-successful files.
/// </summary>
public sealed class ConversionLogger : IDisposable
{
    private readonly StreamWriter _fileWriter;
    private readonly List<ConversionResult> _failures = new();
    private int _okCount;
    private int _toleratedCount;
    private int _totalCount;

    private int _lastConsoleLineLength;
    private string? _lastFolder;
    private readonly bool _interactive = !Console.IsOutputRedirected;

    public ConversionLogger(string logFilePath)
    {
        string? dir = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _fileWriter = new StreamWriter(logFilePath, append: false, Encoding.UTF8) { AutoFlush = true };
        _fileWriter.WriteLine($"=== conNCW log — started on {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} ===");
        _fileWriter.WriteLine("(only failures are logged line by line; the final summary recaps the totals)");
    }

    public void Report(ConversionResult result)
    {
        _totalCount++;
        string fullLine = FormatLine(result);

        bool isFailure = result.Status is
            ConversionStatus.FailUnknownSignature or
            ConversionStatus.FailCorruptData or
            ConversionStatus.FailIoError;

        // Log file: failures only, full untruncated line with full path.
        if (isFailure)
        {
            _fileWriter.WriteLine(fullLine);
        }

        if (_interactive)
        {
            string? folder = Path.GetDirectoryName(result.SourcePath);
            folder ??= string.Empty;

            // Row 1: only reprinted when the folder actually changes.
            // This is a normal WriteLine (scrolls, doesn't overwrite), but it
            // fires once per folder, not once per file, so the cost is
            // negligible even across thousands of files.
            if (folder != _lastFolder)
            {
                if (_lastConsoleLineLength > 0)
                {
                    Console.WriteLine(); // close the previous file-row overwrite cleanly
                    _lastConsoleLineLength = 0;
                }

                Console.WriteLine(TruncateForConsole($"Folder: {folder}"));
                _lastFolder = folder;
            }

            // Row 2: overwritten in place for every file (short content:
            // file name only, not the full path, so it fits without needing
            // to resize the window).
            string status = StatusLabel(result.Status);
            string sig = result.SignatureHex is not null ? $" [sig: {result.SignatureHex}]" : string.Empty;
            string fileLine = $"[{DateTimeOffset.Now:HH:mm:ss}] {status,-28} {Path.GetFileName(result.SourcePath)}{sig}";
            string consoleLine = TruncateForConsole(fileLine);

            int pad = _lastConsoleLineLength - consoleLine.Length;
            Console.Write('\r' + consoleLine + (pad > 0 ? new string(' ', pad) : string.Empty));
            _lastConsoleLineLength = consoleLine.Length;
        }
        else
        {
            Console.WriteLine(fullLine);
        }

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

    /// <summary>
    /// Shortens a line so it never exceeds the terminal width, preventing
    /// the auto-wrap that would otherwise break the \r overwrite trick.
    /// </summary>
    private static string TruncateForConsole(string line)
    {
        int width;
        try
        {
            width = Math.Max(Console.WindowWidth - 1, 20);
        }
        catch
        {
            width = 119;
        }

        if (line.Length <= width)
        {
            return line;
        }

        const string ellipsis = "...";
        int keepEnd = Math.Min(40, width / 3);
        int keepStart = width - keepEnd - ellipsis.Length;
        if (keepStart < 0) keepStart = 0;

        return string.Concat(line.AsSpan(0, keepStart), ellipsis, line.AsSpan(line.Length - keepEnd, keepEnd));
    }

    private static string StatusLabel(ConversionStatus status) => status switch
    {
        ConversionStatus.Ok => "OK",
        ConversionStatus.OkToleratedSignature => "OK (tolerated signature)",
        ConversionStatus.OkBypassTest => "OK (bypass test)",
        ConversionStatus.FailUnknownSignature => "FAILED (unknown signature)",
        ConversionStatus.FailCorruptData => "FAILED (corrupt data)",
        ConversionStatus.FailIoError => "FAILED (I/O error)",
        _ => "?"
    };

    private static string FormatLine(ConversionResult r)
    {
        string status = StatusLabel(r.Status);
        string sig = r.SignatureHex is not null ? $" [sig: {r.SignatureHex}]" : string.Empty;
        string msg = r.Message is not null ? $" — {r.Message}" : string.Empty;
        return $"[{DateTimeOffset.Now:HH:mm:ss}] {status,-28} {r.SourcePath}{sig}{msg}";
    }

    /// <summary>
    /// Displays the final summary: kept on screen (new behavior) instead of
    /// simply scrolling away and being lost, plus written to the log file.
    /// </summary>
    public void WriteSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("=== Summary ===");
        sb.AppendLine($"Total processed : {_totalCount}");
        sb.AppendLine($"Succeeded : {_okCount} (including {_toleratedCount} in tolerated/bypass mode)");
        sb.AppendLine($"Failed : {_failures.Count}");

        if (_failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failed files:");
            foreach (var f in _failures)
            {
                sb.AppendLine($" - {f.SourcePath} ({f.Status}){(f.SignatureHex is not null ? $" [sig: {f.SignatureHex}]" : string.Empty)}");
            }
        }

        string summary = sb.ToString();

        if (_interactive && _lastConsoleLineLength > 0)
        {
            Console.WriteLine();
            _lastConsoleLineLength = 0;
        }

        Console.WriteLine(summary);
        _fileWriter.WriteLine(summary);
    }

    public void Dispose()
    {
        _fileWriter.Flush();
        _fileWriter.Dispose();
    }
}
