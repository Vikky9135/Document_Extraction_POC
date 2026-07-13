using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Prompts;
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

    public async Task<FormatterResult> FormatAsync(
        ExtractionSchema schema,
        IReadOnlyDictionary<string, ExtractionFieldResult> fields,
        CancellationToken ct = default)
    {
        // Only fields with a FieldFormat instruction (and an extracted value) need formatting.
        var fieldsNeedingFormat = schema.Fields
            .Where(f => !string.IsNullOrWhiteSpace(f.FieldFormat) && fields.ContainsKey(f.Name))
            .ToList();

        // Start from a copy so unformatted fields pass through unchanged.
        var output = new Dictionary<string, ExtractionFieldResult>(fields);

        if (fieldsNeedingFormat.Count == 0)
            return new FormatterResult(output, 0);

        // Run all format calls in parallel — Task.WhenAll, no blocking.
        var formatTasks = fieldsNeedingFormat.Select(async field =>
        {
            var current = fields[field.Name];
            var rawValue = string.IsNullOrEmpty(current.RawStr)
                ? current.Value?.ToString() ?? "" : current.RawStr;

            // Nothing to format if the source value is empty.
            if (string.IsNullOrWhiteSpace(rawValue))
                return (field.Name, Result: current);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompts.FormatterSystemPrompt),
                new UserChatMessage(
                    $"Format the following value.\n" +
                    $"Value: \"{rawValue}\"\n" +
                    $"Required format: {field.FieldFormat}\n\n" +
                    "Return ONLY the formatted value as a plain string. No quotes, no explanation.")
            };

            var response = await _o4MiniClient.CompleteChatAsync(messages, cancellationToken: ct);
            var formatted = response.Value.Content[0].Text.Trim().Trim('"');

            return (field.Name, Result: current with { Value = formatted });
        });

        var results = await Task.WhenAll(formatTasks);

        foreach (var (name, result) in results)
            output[name] = result;

        return new FormatterResult(output, fieldsNeedingFormat.Count);
    }
}
