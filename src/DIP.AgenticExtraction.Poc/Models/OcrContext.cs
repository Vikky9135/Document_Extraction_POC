namespace DIP.AgenticExtraction.Poc.Models;

/// <summary>
/// Word-level OCR element with bounding polygon for coordinate lookup.
/// </summary>
public record OcrWord
{
    public required string Content { get; init; }
    public int PageNumber { get; init; }
    public List<PolygonPoint> Polygon { get; init; } = [];
    public double Confidence { get; init; }
}

/// <summary>
/// Line-level OCR element for broader text matching.
/// </summary>
public record OcrLine
{
    public required string Content { get; init; }
    public int PageNumber { get; init; }
    public List<PolygonPoint> Polygon { get; init; } = [];
}

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

    // Word-level elements with polygons — used for post-extraction coordinate lookup
    public List<OcrWord> Words { get; init; } = [];

    // Line-level elements with polygons — fallback for multi-word matches
    public List<OcrLine> Lines { get; init; } = [];

    // Page images — populated only when IncludeImages = true
    public List<BinaryData> PageImages { get; init; } = [];
}
