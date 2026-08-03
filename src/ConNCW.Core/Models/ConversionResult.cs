namespace ConNCW.Core.Models;

public enum ConversionStatus
{
    Ok,
    OkToleratedSignature,
    OkBypassTest,
    FailUnknownSignature,
    FailCorruptData,
    FailIoError
}

public sealed record ConversionResult
{
    public required string SourcePath { get; init; }
    public string? DestinationPath { get; init; }
    public required ConversionStatus Status { get; init; }
    public string? Message { get; init; }
    public string? SignatureHex { get; init; }
    public double? BypassConfidenceScore { get; init; }

    public bool IsFailure => Status is ConversionStatus.FailUnknownSignature
        or ConversionStatus.FailCorruptData
        or ConversionStatus.FailIoError;
}
