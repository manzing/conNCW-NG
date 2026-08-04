using System.Text;
using ConNCW.Core.Models;

namespace ConNCW.Core.Logging;

/// <summary>
/// Console logging (a single line overwritten continuously, to avoid
/// spamming large batches) + a persistent log file limited to failures
/// (to avoid saturating the log file on batches of several thousand
/// mostly-successful files).
/// </summary>
public sealed class ConversionLogger : IDisposable
{
    private readonly StreamWriter _fileWriter;
    private readonly List<ConversionResult> _failures = new();
    private int _okCount;
    private int _toleratedCount;
    private int _totalCount;

    // Console line overwrite: no additional cost compared to the previous
    // Console.WriteLine (a single Write per file, no SetCursorPosition, no
    // window-size recalculation). Disabled if output is redirected
    // (file/CI), since \r has no meaning there.
    private int _lastConsoleLineLength;
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
        string line = FormatLine(result);

        bool isFailure = result.Status is
            ConversionStatus.FailUnknownSignature or
            ConversionStatus.FailCorruptData or
            ConversionStatus.FailIoError;

        // Log file: failures only, to avoid saturating the file on batches
        // of several thousand successful files.
        if (isFailure)
        {
            _fileWriter.WriteLine(line);
        }

        // Console: overwrite the previous line instead of stacking a new
        // one, regardless of status (success or failure).
        if (_interactive)
        {
            int pad = _lastConsoleLineLength - line.Length;
            Console.Write('\r' + line + (pad > 0 ? new string(' ', pad) : string.Empty));
            _lastConsoleLineLength = line.Length;
        }
        else
        {
            Console.WriteLine(line);
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

    private static string FormatLine(ConversionResult r)
    {
        string status = r.Status switch
        {
            ConversionStatus.Ok => "OK",
            ConversionStatus.OkToleratedSignature => "OK (tolerated signature)",
            ConversionStatus.OkBypassTest => "OK (bypass test)",
            ConversionStatus.FailUnknownSignature => "FAILED (unknown signature)",
            ConversionStatus.FailCorruptData => "FAILED (corrupt data)",
            ConversionStatus.FailIoError => "FAILED (I/O error)",
            _ => "?"
        };

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

        // End the current overwritten line before printing the summary, so
        // the last progress line doesn't visually merge with the recap.
        if (_interactive && _lastConsoleLineLength > 0)
        {
            Console.WriteLine();
            _lastConsoleLineLength = 0;
        }

        // Kept on screen at the end of the run (new behavior), not just
        // scrolled away.
        Console.WriteLine(summary);

        // The full summary (including the list of failures) always goes to
        // the log file
        _fileWriter.WriteLine(summary);
    }

    public void Dispose()
    {
        _fileWriter.Flush();
        _fileWriter.Dispose();
    }
}