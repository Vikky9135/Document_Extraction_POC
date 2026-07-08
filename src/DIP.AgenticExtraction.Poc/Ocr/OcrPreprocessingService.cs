using Azure.AI.DocumentIntelligence;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Options;
using Microsoft.Extensions.Options;

namespace DIP.AgenticExtraction.Poc.Ocr;

public interface IOcrPreprocessingService
{
    Task<OcrContext> PrepareAsync(Stream pdfStream, CancellationToken ct = default);
}

// TODO (Step 5): call ADI prebuilt-layout + split large docs into 100-page chunks.
public class OcrPreprocessingService : IOcrPreprocessingService
{
    private readonly DocumentIntelligenceClient _client;
    private readonly AgenticExtractionOptions _opts;

    public OcrPreprocessingService(
        DocumentIntelligenceClient client,
        IOptions<AgenticExtractionOptions> opts)
    {
        _client = client;
        _opts = opts.Value;
    }

    public Task<OcrContext> PrepareAsync(Stream pdfStream, CancellationToken ct = default)
        => throw new NotImplementedException();
}
