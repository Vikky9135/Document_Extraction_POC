namespace DIP.AgenticExtraction.Poc.Models;

public record OcrContext
{
    // Structured tagged text output from LayoutElementMapper
    // === PAGE 1 ===
    // --- Paragraphs ---
    // [para_0](title): INVOICE
    // --- Tables ---
    // [table_0]: 3 rows x 3 cols ...
    public required string StructuredText { get; init; }

    // Raw ADI result — kept for bounding box lookup
    public required object RawAnalyzeResult { get; init; }
    public int PageCount { get; init; }

    // Page images — populated only when IncludeImages = true
    public List<BinaryData> PageImages { get; init; } = [];
}
