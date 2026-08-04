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
    // window-size recalculation beyond a single width read). Disabled if
    // output is redirected (file/CI), since \r has no meaning there.
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
        string fullLine = FormatLine(result);

        bool isFailure = result.Status is
            ConversionStatus.FailUnknownSignature or
            ConversionStatus.FailCorruptData or
            ConversionStatus.FailIoError;

        // Log file: failures only, full untruncated line, to avoid
        // saturating the file on batches of several thousand successful
        // files while keeping full diagnostic detail for the ones that fail.
        if (isFailure)
        {
            _fileWriter.WriteLine(fullLine);
        }

        // Console: overwrite the previous line instead of stacking a new
        // one, regardless of status (success or failure).
        if (_interactive)
        {
            // Critical: \r only returns the cursor to the start of the
            // CURRENT visual row. If the line is longer than the terminal
            // width, the console auto-wraps it onto a second row, and \r
            // can no longer reach the true start of the line -> the overwrite
            // silently breaks and every file appears to print a new line.
            // Truncating to the window width prevents wrapping entirely.
            string consoleLine = TruncateForConsole(fullLine);

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
    /// the auto-wrap that breaks the \r overwrite trick. Truncates from the
    /// middle of the path (keeps the status/timestamp prefix and the file
    /// name, which are the most useful parts to see at a glance).
    /// </summary>
    private static string TruncateForConsole(string line)
    {
        int width;
        try
        {
            // -1 to always leave the very last column free: some terminals
            // auto-wrap as soon as a character is written into the last
            // column, even before a new one arrives.
            width = Math.Max(Console.WindowWidth - 1, 20);
        }
        catch
        {
            width = 119; // fallback if the console has no window (e.g. service)
        }

        if (line.Length <= width)
        {
            return line;
        }

        const string ellipsis = "...";
        int keepEnd = Math.Min(40, width / 3);       // keep the file name / tail
        int keepStart = width - keepEnd - ellipsis.Length;
        if (keepStart < 0) keepStart = 0;

        return string.Concat(line.AsSpan(0, keepStart), ellipsis, line.AsSpan(line.Length - keepEnd, keepEnd));
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
        // the log file.
        _fileWriter.WriteLine(summary);
    }

    public void Dispose()
    {
        _fileWriter.Flush();
        _fileWriter.Dispose();
    }
}