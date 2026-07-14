using System.Text;
using Azure.AI.DocumentIntelligence;

namespace DIP.AgenticExtraction.Poc.Ocr;

// Mirrors DIP Core's LayoutElementMapper.ConvertLayoutDataToStructuredText().
// Output format is IDENTICAL — same tags, structure, page numbering — so the text
// fed to agents is compatible with DIP Core production.
public static class LayoutElementMapper
{
    public static string ConvertToStructuredText(AnalyzeResult result)
    {
        var sb = new StringBuilder();
        int pageCount = result.Pages?.Count ?? 0;

        for (int pageNum = 1; pageNum <= pageCount; pageNum++)
        {
            sb.AppendLine($"=== PAGE {pageNum} ===");

            // ── Paragraphs on this page ──────────────────────────────────────
            var pageParagraphs = result.Paragraphs?
                .Where(p => p.BoundingRegions?.Any(r => r.PageNumber == pageNum) == true)
                .ToList();

            if (pageParagraphs is { Count: > 0 })
            {
                sb.AppendLine("--- Paragraphs ---");
                for (int i = 0; i < pageParagraphs.Count; i++)
                {
                    var para = pageParagraphs[i];
                    var role = para.Role?.ToString() ?? "body";
                    sb.AppendLine($"[para_{i}]({role}): {para.Content}");
                }
                sb.AppendLine();
            }

            // ── Tables on this page ──────────────────────────────────────────
            var pageTables = result.Tables?
                .Where(t => t.BoundingRegions?.Any(r => r.PageNumber == pageNum) == true)
                .ToList();

            if (pageTables is { Count: > 0 })
            {
                sb.AppendLine("--- Tables ---");
                for (int ti = 0; ti < pageTables.Count; ti++)
                {
                    var table = pageTables[ti];
                    sb.AppendLine($"[table_{ti}]: {table.RowCount} rows x {table.ColumnCount} cols");

                    for (int r = 0; r < table.RowCount; r++)
                    {
                        var cells = table.Cells
                            .Where(c => c.RowIndex == r)
                            .OrderBy(c => c.ColumnIndex)
                            .Select(c => c.Content?.Trim() ?? string.Empty);

                        sb.AppendLine($"  [row_{r}]: {string.Join(" | ", cells)}");
                    }
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString().TrimEnd();
    }
}
