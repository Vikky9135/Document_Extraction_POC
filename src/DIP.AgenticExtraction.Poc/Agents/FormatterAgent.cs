using DIP.AgenticExtraction.Poc.Models;
using OpenAI.Chat;

namespace DIP.AgenticExtraction.Poc.Agents;

public record FormatterResult(
    Dictionary<string, ExtractionFieldResult> Fields,
    int FormatterCallCount);

public interface IFormatterAgent
{
    // Phase 8 — enforce field_format for all fields that define one, in parallel.
    Task<FormatterResult> FormatAsync(
        ExtractionSchema schema,
        IReadOnlyDictionary<string, ExtractionFieldResult> fields,
        CancellationToken ct = default);
}

// Phase 8 — uses o4-mini. Formatting is simple instruction-following, not reasoning.
public class FormatterAgent : IFormatterAgent
{
    private readonly ChatClient _o4MiniClient;

    public FormatterAgent(ChatClient o4MiniClient) => _o4MiniClient = o4MiniClient;

    // TODO (Step 11): run parallel format calls via Task.WhenAll.
    public Task<FormatterResult> FormatAsync(
        ExtractionSchema schema,
        IReadOnlyDictionary<string, ExtractionFieldResult> fields,
        CancellationToken ct = default)
        => throw new NotImplementedException();
}
