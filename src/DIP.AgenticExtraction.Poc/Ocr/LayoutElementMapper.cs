using Azure.AI.DocumentIntelligence;

namespace DIP.AgenticExtraction.Poc.Ocr;

// Mirrors DIP Core's LayoutElementMapper.ConvertLayoutDataToStructuredText().
// Output format MUST be IDENTICAL to DIP Core — same tags, structure, page numbering —
// so OCR text fed to the agents is byte-for-byte compatible with DIP Core production.
public static class LayoutElementMapper
{
    // TODO (Step 5): convert ADI AnalyzeResult -> structured tagged text.
    public static string ConvertToStructuredText(AnalyzeResult result)
        => throw new NotImplementedException();
}
