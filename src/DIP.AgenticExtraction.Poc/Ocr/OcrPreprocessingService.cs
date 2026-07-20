using Azure;
using Azure.AI.DocumentIntelligence;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Options;
using Microsoft.Extensions.Options;

namespace DIP.AgenticExtraction.Poc.Ocr;

public interface IOcrPreprocessingService
{
    Task<OcrContext> PrepareAsync(Stream pdfStream, CancellationToken ct = default);
}

public class OcrPreprocessingService : IOcrPreprocessingService
{
    private readonly DocumentIntelligenceClient _client;
    private readonly AgenticExtractionOptions _opts;
    private readonly ILogger<OcrPreprocessingService> _logger;

    public OcrPreprocessingService(
        DocumentIntelligenceClient client,
        IOptions<AgenticExtractionOptions> opts,
        ILogger<OcrPreprocessingService> logger)
    {
        _client = client;
        _opts   = opts.Value;
        _logger = logger;
    }

    public async Task<OcrContext> PrepareAsync(Stream pdfStream, CancellationToken ct = default)
    {
        // Read the full stream into memory — ADI requires bytes or a URL
        var pdfBytes = await BinaryData.FromStreamAsync(pdfStream, ct);

        var options = new AnalyzeDocumentOptions("prebuilt-layout", pdfBytes);

        var operation = await _client.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            options,
            cancellationToken: ct);

        var result = operation.Value;

        // Extract word-level and line-level elements with polygon coordinates
        var words = ExtractWords(result);
        var lines = ExtractLines(result);

        return new OcrContext
        {
            StructuredText   = LayoutElementMapper.ConvertToStructuredText(result),
            RawAnalyzeResult = result,
            PageCount        = result.Pages?.Count ?? 0,
            Words            = words,
            Lines            = lines
        };
    }

    /// <summary>
    /// Extracts word-level elements with their bounding polygons from ADI result.
    /// Polygons are normalized (0-1) relative to page dimensions.
    /// </summary>
    private static List<OcrWord> ExtractWords(AnalyzeResult result)
    {
        var words = new List<OcrWord>();
        if (result.Pages is null) return words;

        foreach (var page in result.Pages)
        {
            var pageWidth = page.Width ?? 1;
            var pageHeight = page.Height ?? 1;

            if (page.Words is null) continue;
            foreach (var word in page.Words)
            {
                words.Add(new OcrWord
                {
                    Content    = word.Content,
                    PageNumber = page.PageNumber,
                    Confidence = word.Confidence,
                    Polygon    = NormalizePolygon(word.Polygon, pageWidth, pageHeight)
                });
            }
        }
        return words;
    }

    /// <summary>
    /// Extracts line-level elements with their bounding polygons from ADI result.
    /// </summary>
    private static List<OcrLine> ExtractLines(AnalyzeResult result)
    {
        var lines = new List<OcrLine>();
        if (result.Pages is null) return lines;

        foreach (var page in result.Pages)
        {
            var pageWidth = page.Width ?? 1;
            var pageHeight = page.Height ?? 1;

            if (page.Lines is null) continue;
            foreach (var line in page.Lines)
            {
                lines.Add(new OcrLine
                {
                    Content    = line.Content,
                    PageNumber = page.PageNumber,
                    Polygon    = NormalizePolygon(line.Polygon, pageWidth, pageHeight)
                });
            }
        }
        return lines;
    }

    /// <summary>
    /// Converts ADI polygon (flat list of x,y pairs in page units) to normalized PolygonPoints.
    /// </summary>
    private static List<PolygonPoint> NormalizePolygon(IReadOnlyList<float>? polygon, float pageWidth, float pageHeight)
    {
        if (polygon is null || polygon.Count < 2) return [];

        var points = new List<PolygonPoint>();
        for (int i = 0; i < polygon.Count - 1; i += 2)
        {
            points.Add(new PolygonPoint(
                X: Math.Round(polygon[i] / pageWidth, 6),
                Y: Math.Round(polygon[i + 1] / pageHeight, 6)
            ));
        }
        return points;
    }
}
