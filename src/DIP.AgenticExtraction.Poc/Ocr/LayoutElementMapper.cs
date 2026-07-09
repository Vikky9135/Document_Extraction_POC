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
        int globalTableIndex = 0;

        foreach (var page in result.Pages)
        {
            int pageNum = page.PageNumber;
            sb.AppendLine($"=== PAGE {pageNum} ===");

            // ── Paragraphs ────────────────────────────────────────────────────
            var paragraphs = result.Paragraphs?
                .Where(p => IsOnPage(p.BoundingRegions, pageNum))
                .ToList();

            if (paragraphs?.Count > 0)
            {
                sb.AppendLine("--- Paragraphs ---");
                for (int i = 0; i < paragraphs.Count; i++)
                {
                    var para = paragraphs[i];
                    var role = para.Role?.ToString().ToLowerInvariant() ?? "body";
                    sb.AppendLine($"[para_{i}]({role}): {para.Content}");
                }
            }

            // ── Tables ────────────────────────────────────────────────────────
            var tables = result.Tables?
                .Where(t => IsOnPage(t.BoundingRegions, pageNum))
                .ToList();

            if (tables?.Count > 0)
            {
                sb.AppendLine("--- Tables ---");
                foreach (var table in tables)
                {
                    sb.AppendLine($"[table_{globalTableIndex}]: {table.RowCount} rows x {table.ColumnCount} cols");

                    for (int row = 0; row < table.RowCount; row++)
                    {
                        var cells = table.Cells
                            .Where(c => c.RowIndex == row)
                            .OrderBy(c => c.ColumnIndex)
                            .Select(c =>
                            {
                                var tag = c.Kind == DocumentTableCellKind.ColumnHeader ? "(header)"
                                        : c.Kind == DocumentTableCellKind.RowHeader    ? "(rowheader)"
                                        : "";
                                return $"[r{row}c{c.ColumnIndex}]{tag}: {c.Content}";
                            });

                        sb.AppendLine("  " + string.Join(" | ", cells));
                    }

                    globalTableIndex++;
                }
            }

            // ── Selection Marks ───────────────────────────────────────────────
            if (page.SelectionMarks?.Count > 0)
            {
                sb.AppendLine("--- Selection Marks ---");
                for (int si = 0; si < page.SelectionMarks.Count; si++)
                {
                    var mark = page.SelectionMarks[si];
                    var state = mark.State == DocumentSelectionMarkState.Selected
                        ? "selected" : "unselected";
                    sb.AppendLine($"[selmark_{si}]({state})");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static bool IsOnPage(IReadOnlyList<BoundingRegion>? regions, int pageNumber)
        => regions?.Any(r => r.PageNumber == pageNumber) ?? false;
}
