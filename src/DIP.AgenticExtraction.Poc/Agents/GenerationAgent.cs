using DIP.AgenticExtraction.Poc.Models;
using OpenAI.Chat;

namespace DIP.AgenticExtraction.Poc.Agents;

public record GenerationResult(Dictionary<string, object?> Results, int LlmCallCount);

// ScriptGlobals: the data context available inside Roslyn scripts.
public class ScriptGlobals
{
    public Dictionary<string, ExtractionFieldResult> Data { get; set; } = [];
    public DateTime Today { get; set; } = DateTime.UtcNow.Date;
}

public interface IGenerationAgent
{
    // Phase 9 — GPT-5 writes a C# expression per computed field; Roslyn executes it.
    Task<GenerationResult> ComputeAsync(
        ExtractionSchema schema,
        IReadOnlyDictionary<string, ExtractionFieldResult> fields,
        CancellationToken ct = default);
}

// Phase 9 — uses GPT-5 for code gen + Roslyn (CSharpScript) for deterministic execution.
public class GenerationAgent : IGenerationAgent
{
    private readonly ChatClient _gpt5Client;

    public GenerationAgent(ChatClient gpt5Client) => _gpt5Client = gpt5Client;

    // TODO (Step 12): generate C# expression, run safety whitelist, execute via Roslyn.
    public Task<GenerationResult> ComputeAsync(
        ExtractionSchema schema,
        IReadOnlyDictionary<string, ExtractionFieldResult> fields,
        CancellationToken ct = default)
        => throw new NotImplementedException();
}
