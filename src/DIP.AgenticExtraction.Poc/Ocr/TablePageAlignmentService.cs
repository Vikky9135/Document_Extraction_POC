using System.Text.RegularExpressions;
using DIP.AgenticExtraction.Poc.Models;

namespace DIP.AgenticExtraction.Poc.Ocr;

public record TableRowsByPage(int PageNumber, List<Dictionary<string, object?>> Rows);

public static partial class TablePageAlignmentService
{
    [GeneratedRegex("[^a-z0-9\\s]", RegexOptions.Compiled)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex("\\s+", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();

    public static Dictionary<string, List<TableRowsByPage>> GroupRowsByPage(
        IReadOnlyDictionary<string, List<Dictionary<string, object?>>> tables,
        IReadOnlyList<int> preferredPages,
        IReadOnlyList<OcrLine> ocrLines)
    {
        var linesByPage = ocrLines
            .GroupBy(l => l.PageNumber)
            .ToDictionary(g => g.Key, g => g.Select(l => Normalize(l.Content)).ToList());

        var fallbackPage = preferredPages.FirstOrDefault();
        if (fallbackPage <= 0)
            fallbackPage = linesByPage.Keys.OrderBy(k => k).FirstOrDefault(1);

        var result = new Dictionary<string, List<TableRowsByPage>>();

        foreach (var (tableName, rows) in tables)
        {
            var groupedRows = new Dictionary<int, List<Dictionary<string, object?>>>();

            foreach (var row in rows)
            {
                var page = ResolveRowPage(row, preferredPages, linesByPage, fallbackPage);
                if (!groupedRows.TryGetValue(page, out var list))
                {
                    list = [];
                    groupedRows[page] = list;
                }

                list.Add(row);
            }

            result[tableName] = groupedRows
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => new TableRowsByPage(kvp.Key, kvp.Value))
                .ToList();
        }

        return result;
    }

    private static int ResolveRowPage(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<int> preferredPages,
        IReadOnlyDictionary<int, List<string>> linesByPage,
        int fallbackPage)
    {
        var rowText = BuildRowText(row);
        if (string.IsNullOrWhiteSpace(rowText))
            return fallbackPage;

        var preferred = preferredPages.Where(p => linesByPage.ContainsKey(p)).Distinct().ToList();
        var candidates = preferred.Count > 0
            ? preferred
            : linesByPage.Keys.OrderBy(k => k).ToList();

        var bestPage = fallbackPage;
        var bestScore = 0.0;

        foreach (var page in candidates)
        {
            if (!linesByPage.TryGetValue(page, out var pageLines) || pageLines.Count == 0)
                continue;

            var score = ScoreRowAgainstPage(rowText, pageLines);
            if (score > bestScore)
            {
                bestScore = score;
                bestPage = page;
            }
        }

        return bestPage;
    }

    private static double ScoreRowAgainstPage(string normalizedRowText, IReadOnlyList<string> pageLines)
    {
        if (pageLines.Any(line => line.Contains(normalizedRowText, StringComparison.OrdinalIgnoreCase)))
            return 1.0;

        var tokens = normalizedRowText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (tokens.Length == 0)
            return 0;

        var bestTokenCoverage = 0.0;

        foreach (var line in pageLines)
        {
            var matchedTokens = tokens.Count(token => line.Contains(token, StringComparison.OrdinalIgnoreCase));
            var coverage = (double)matchedTokens / tokens.Length;
            if (coverage > bestTokenCoverage)
                bestTokenCoverage = coverage;
        }

        return bestTokenCoverage;
    }

    private static string BuildRowText(IReadOnlyDictionary<string, object?> row)
    {
        var parts = row.Values
            .Select(v => v?.ToString()?.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList();

        return Normalize(string.Join(' ', parts));
    }

    private static string Normalize(string input)
    {
        var lower = input.ToLowerInvariant();
        var stripped = NonAlphaNumericRegex().Replace(lower, " ");
        return MultiSpaceRegex().Replace(stripped, " ").Trim();
    }
}
