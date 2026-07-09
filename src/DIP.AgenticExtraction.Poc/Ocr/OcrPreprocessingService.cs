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
        _opts = opts.Value;
        _logger = logger;
    }

    public async Task<OcrContext> PrepareAsync(Stream pdfStream, CancellationToken ct = default)
    {
        _logger.LogInformation("Reading PDF into memory for ADI analysis...");
        var binaryContent = await BinaryData.FromStreamAsync(pdfStream, ct);

        _logger.LogInformation("Calling Azure Document Intelligence (prebuilt-layout)...");
        var operation = await _client.AnalyzeDocumentAsync(
            Azure.WaitUntil.Completed,
            "prebuilt-layout",
            binaryContent,
            ct);

        var result = operation.Value;
        int pageCount = result.Pages?.Count ?? 0;

        if (pageCount > _opts.MaxOcrPagesPerChunk)
            _logger.LogWarning(
                "Document has {PageCount} pages which exceeds MaxOcrPagesPerChunk ({Max}). " +
                "Chunked OCR is not yet implemented — processing full document.",
                pageCount, _opts.MaxOcrPagesPerChunk);

        _logger.LogInformation("ADI analysis complete. Pages: {PageCount}", pageCount);

        var structuredText = LayoutElementMapper.ConvertToStructuredText(result);

        return new OcrContext
        {
            StructuredText    = structuredText,
            RawAnalyzeResult  = result,
            PageCount         = pageCount
        };
    }
}
