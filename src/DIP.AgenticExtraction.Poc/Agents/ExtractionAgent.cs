using DIP.AgenticExtraction.Poc.Models;
using OpenAI.Chat;

namespace DIP.AgenticExtraction.Poc.Agents;

public interface IExtractionAgent
{
    // Phase 5 — one structured call for ALL fields. feedbackOverrides supports Phase 7 re-extraction.
    Task<(Dictionary<string, ExtractionFieldResult> Fields, int LlmCallCount)> ExtractFieldsAsync(
        ExtractionSchema schema,
        string structuredText,
        IReadOnlyDictionary<string, string>? feedbackOverrides = null,
        CancellationToken ct = default);
}

// Phase 5 — uses O3 (reasoning_effort: high).
public class ExtractionAgent : IExtractionAgent
{
    private readonly ChatClient _o3Client;

    public ExtractionAgent(ChatClient o3Client) => _o3Client = o3Client;

    // TODO (Step 8): build strict JSON Schema + call O3 for structured extraction.
    public Task<(Dictionary<string, ExtractionFieldResult> Fields, int LlmCallCount)> ExtractFieldsAsync(
        ExtractionSchema schema,
        string structuredText,
        IReadOnlyDictionary<string, string>? feedbackOverrides = null,
        CancellationToken ct = default)
        => throw new NotImplementedException();
}
