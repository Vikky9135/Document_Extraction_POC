using DIP.AgenticExtraction.Poc.Agents;
using DIP.AgenticExtraction.Poc.Models;

namespace DIP.AgenticExtraction.Poc.Orchestration;

public interface IAgenticExtractionOrchestrator
{
    // Phases 5-9: Extract -> Verify -> Correct (loop) -> Format -> Generate -> assemble result.
    Task<ExtractionJobResult> RunAsync(
        string jobId,
        ExtractionSchema schema,
        string structuredText,
        int ocrPageCount,
        CancellationToken ct = default);
}

public class AgenticExtractionOrchestrator : IAgenticExtractionOrchestrator
{
    private readonly IExtractionAgent _extractionAgent;
    private readonly IVerificationAgent _verificationAgent;
    private readonly IFormatterAgent _formatterAgent;
    private readonly IGenerationAgent _generationAgent;

    public AgenticExtractionOrchestrator(
        IExtractionAgent extractionAgent,
        IVerificationAgent verificationAgent,
        IFormatterAgent formatterAgent,
        IGenerationAgent generationAgent)
    {
        _extractionAgent = extractionAgent;
        _verificationAgent = verificationAgent;
        _formatterAgent = formatterAgent;
        _generationAgent = generationAgent;
    }

    // TODO (Step 10): implement the Extract -> Verify -> Correction loop -> Format -> Generate pipeline.
    public Task<ExtractionJobResult> RunAsync(
        string jobId,
        ExtractionSchema schema,
        string structuredText,
        int ocrPageCount,
        CancellationToken ct = default)
        => throw new NotImplementedException();
}
