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

    public OcrPreprocessingService(
        DocumentIntelligenceClient client,
        IOptions<AgenticExtractionOptions> opts)
    {
        _client = client;
        _opts   = opts.Value;
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

        return new OcrContext
        {
            StructuredText   = LayoutElementMapper.ConvertToStructuredText(result),
            RawAnalyzeResult = result,
            PageCount        = result.Pages?.Count ?? 0
        };
    }
}
