using System.Text;
using System.Text.RegularExpressions;

namespace DIP.AgenticExtraction.Poc.Ocr;

/// <summary>
/// Splits OCR structured text into page-based chunks.
/// The text uses "=== PAGE N ===" markers produced by LayoutElementMapper.
/// </summary>
public static partial class PageChunker
{
    // Matches "=== PAGE 42 ===" markers in the structured OCR text.
    [GeneratedRegex(@"^=== PAGE (\d+) ===", RegexOptions.Multiline)]
    private static partial Regex PageMarkerRegex();

    /// <summary>
    /// Split structured OCR text into chunks of at most <paramref name="chunkPages"/> pages,
    /// with <paramref name="overlapPages"/> pages of overlap between consecutive chunks.
    /// Returns empty list if the text is empty.
    /// </summary>
    public static List<PageChunk> SplitIntoChunks(
        string structuredText,
        int chunkPages,
        int overlapPages = 0)
    {
        if (string.IsNullOrWhiteSpace(structuredText))
            return [];

        // Find all page boundary positions
        var matches = PageMarkerRegex().Matches(structuredText);
        if (matches.Count == 0)
            return [new PageChunk(structuredText, 1, 1)]; // No markers — treat as single page

        var pagePositions = matches
            .Select(m => (PageNumber: int.Parse(m.Groups[1].Value), StartIndex: m.Index))
            .OrderBy(p => p.PageNumber)
            .ToList();

        var totalPages = pagePositions.Count;
        var chunks = new List<PageChunk>();

        int startIdx = 0; // Index into pagePositions list
        while (startIdx < totalPages)
        {
            int endIdx = Math.Min(startIdx + chunkPages - 1, totalPages - 1);

            int textStart = pagePositions[startIdx].StartIndex;
            int textEnd = endIdx + 1 < totalPages
                ? pagePositions[endIdx + 1].StartIndex
                : structuredText.Length;

            var chunkText = structuredText[textStart..textEnd];
            var startPage = pagePositions[startIdx].PageNumber;
            var endPage = pagePositions[endIdx].PageNumber;

            chunks.Add(new PageChunk(chunkText, startPage, endPage));

            // Advance by (chunkPages - overlap) so next chunk overlaps
            int advance = Math.Max(1, chunkPages - overlapPages);
            startIdx += advance;
        }

        return chunks;
    }

    /// <summary>
    /// Determines whether chunking is needed based on page count and chunk size.
    /// </summary>
    public static bool NeedsChunking(int pageCount, int chunkPages)
        => pageCount > chunkPages;
}

/// <summary>A chunk of OCR structured text covering a range of pages.</summary>
public record PageChunk(string Text, int StartPage, int EndPage)
{
    public int PageCount => EndPage - StartPage + 1;
}
