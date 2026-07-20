using DIP.AgenticExtraction.Poc.Models;

namespace DIP.AgenticExtraction.Poc.Ocr;

/// <summary>
/// Looks up bounding box coordinates for extracted values by matching the raw extraction
/// string against OCR words/lines.
/// </summary>
public static class BoundingBoxLookupService
{
    /// <summary>
    /// Finds the bounding regions for a given extracted text by searching OCR lines and words.
    /// Returns the polygon(s) where the text was found in the document.
    /// </summary>
    public static List<BoundingRegion> FindBoundingRegions(
        string extractedText,
        IReadOnlyList<OcrLine> lines,
        IReadOnlyList<OcrWord> words)
    {
        if (string.IsNullOrWhiteSpace(extractedText))
            return [];

        var searchText = extractedText.Trim();

        // Strategy 1: Exact match in lines (best for multi-word values)
        var matchedLines = lines
            .Where(l => l.Content.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .Take(1) // Take first match only
            .Select(l => new BoundingRegion
            {
                PageNumber = l.PageNumber,
                Polygon    = l.Polygon
            })
            .ToList();

        if (matchedLines.Count > 0)
            return matchedLines;

        // Strategy 2: Exact match in words (for single-word values)
        var matchedWords = words
            .Where(w => w.Content.Equals(searchText, StringComparison.OrdinalIgnoreCase))
            .Take(1)
            .Select(w => new BoundingRegion
            {
                PageNumber = w.PageNumber,
                Polygon    = w.Polygon
            })
            .ToList();

        if (matchedWords.Count > 0)
            return matchedWords;

        // Strategy 3: Partial match — find consecutive words that form the text
        var regions = FindConsecutiveWords(searchText, words);
        if (regions.Count > 0)
            return regions;

        // Strategy 4: Normalized match (strip currency, commas, etc.)
        var normalizedSearch = NormalizeForSearch(searchText);
        var normalizedLine = lines
            .Where(l => NormalizeForSearch(l.Content).Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase))
            .Take(1)
            .Select(l => new BoundingRegion
            {
                PageNumber = l.PageNumber,
                Polygon    = l.Polygon
            })
            .ToList();

        return normalizedLine;
    }

    /// <summary>
    /// Finds consecutive words in the OCR that together form the search text,
    /// and returns a bounding region that spans all matched words.
    /// </summary>
    private static List<BoundingRegion> FindConsecutiveWords(string searchText, IReadOnlyList<OcrWord> words)
    {
        var searchWords = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (searchWords.Length < 2) return [];

        for (int i = 0; i <= words.Count - searchWords.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < searchWords.Length; j++)
            {
                if (!words[i + j].Content.Equals(searchWords[j], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                // Merge polygons from first to last matched word into a bounding box
                var matchedWords = words.Skip(i).Take(searchWords.Length).ToList();
                var pageNumber = matchedWords[0].PageNumber;
                var allPoints = matchedWords.SelectMany(w => w.Polygon).ToList();

                if (allPoints.Count == 0) continue;

                // Compute bounding rectangle from all polygon points
                var minX = allPoints.Min(p => p.X);
                var minY = allPoints.Min(p => p.Y);
                var maxX = allPoints.Max(p => p.X);
                var maxY = allPoints.Max(p => p.Y);

                return [new BoundingRegion
                {
                    PageNumber = pageNumber,
                    Polygon =
                    [
                        new PolygonPoint(minX, minY),
                        new PolygonPoint(maxX, minY),
                        new PolygonPoint(maxX, maxY),
                        new PolygonPoint(minX, maxY)
                    ]
                }];
            }
        }

        return [];
    }

    private static string NormalizeForSearch(string text)
        => text.Replace(",", "").Replace("$", "").Replace("€", "")
               .Replace("£", "").Replace("%", "").Trim();
}
