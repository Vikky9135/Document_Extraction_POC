using DIP.AgenticExtraction.Poc.Models;
using OpenAI.Chat;

namespace DIP.AgenticExtraction.Poc.Agents;

public interface IVerificationAgent
{
    // Phase 6 — independent, skeptical fact-check of every extracted value.
    Task<VerificationResult> VerifyFieldsAsync(
        ExtractionSchema schema,
        IReadOnlyDictionary<string, ExtractionFieldResult> extracted,
        string structuredText,
        CancellationToken ct = default);
}

// Phase 6 — uses O3 (reasoning_effort: high) with a skeptical system prompt.
public class VerificationAgent : IVerificationAgent
{
    private readonly ChatClient _o3Client;

    public VerificationAgent(ChatClient o3Client) => _o3Client = o3Client;

    // TODO (Step 9): build verification JSON Schema + call O3 for independent verdicts.
    public Task<VerificationResult> VerifyFieldsAsync(
        ExtractionSchema schema,
        IReadOnlyDictionary<string, ExtractionFieldResult> extracted,
        string structuredText,
        CancellationToken ct = default)
        => throw new NotImplementedException();
}
