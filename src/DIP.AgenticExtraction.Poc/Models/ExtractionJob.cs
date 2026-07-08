namespace DIP.AgenticExtraction.Poc.Models;

public record ExtractionJob
{
    public required string JobId { get; init; }
    public required string BlobPath { get; init; }    // jobs/{jobId}/source.pdf

    // Schema generation ALWAYS runs in the POC — UserPrompt drives what the LLM discovers.
    // e.g. "Extract all invoice fields including vendor, dates, amounts and line items"
    public required string UserPrompt { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
