using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Prompts;
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

    // Generate schema using LLM based on extraction schema JSON and user query.
    Task<string> GenerateSchemaAsync(
        string extractionSchemaJsonStr,
        string userQuery,
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

    /// <summary>
    /// Generates schema using LLM based on extraction schema JSON and user query.
    /// </summary>
    public async Task<string> GenerateSchemaAsync(
        string extractionSchemaJsonStr,
        string userQuery,
        CancellationToken ct = default)
    {
        return await InvokeLlmAsync(extractionSchemaJsonStr, userQuery, ct);
    }

    /// <summary>
    /// Invokes the LLM with schema JSON and user query.
    /// Uses SchemaGenSystemPrompt from SystemPrompts.
    /// Follows the pattern: SystemMessage + UserMessage(schema\n + userQuery).
    /// </summary>
    /// <param name="extractionSchemaJsonStr">JSON string representation of the extraction schema.</param>
    /// <param name="userQuery">The user's query or instruction.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The LLM response content as a string.</returns>
    private async Task<string> InvokeLlmAsync(
        string extractionSchemaJsonStr,
        string userQuery,
        CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompts.SchemaGenSystemPrompt),
            new UserChatMessage($"{extractionSchemaJsonStr}\n{userQuery}")
        };

        var response = await _gpt5Client.CompleteChatAsync(messages, cancellationToken: ct);
        return response.Value.Content[0].Text;
    }
}
