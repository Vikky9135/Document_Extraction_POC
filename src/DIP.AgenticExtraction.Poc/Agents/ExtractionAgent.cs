using System.Text.Json;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Prompts;
using DIP.AgenticExtraction.Poc.Schema;
using OpenAI.Chat;

namespace DIP.AgenticExtraction.Poc.Agents;

public interface IExtractionAgent
{
    // Phase 5 — one structured call for ALL fields. feedbackOverrides supports Phase 7 re-extraction.
    Task<(Dictionary<string, ExtractionFieldResult> Fields,
          Dictionary<string, List<Dictionary<string, object?>>> Tables,
          int LlmCallCount)>
        ExtractFieldsAsync(
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

    public async Task<(Dictionary<string, ExtractionFieldResult> Fields,
                       Dictionary<string, List<Dictionary<string, object?>>> Tables,
                       int LlmCallCount)>
        ExtractFieldsAsync(
            ExtractionSchema schema,
            string structuredText,
            IReadOnlyDictionary<string, string>? feedbackOverrides = null,
            CancellationToken ct = default)
    {
        // 1. Build the dynamic JSON Schema from field definitions.
        //    feedbackOverrides inject verifier feedback into field descriptions (re-extraction pass).
        var jsonSchema = DynamicSchemaGenerator.GenerateExtractionSchema(
            schema.Fields, schema.TableFields, feedbackOverrides);

        var responseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
            "extraction_result",
            BinaryData.FromString(jsonSchema.ToJsonString()),
            null,
            true);

        // 2. Build messages — system prompt + OCR structured text.
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompts.ExtractorSystemPrompt),
            new UserChatMessage(structuredText)
        };

        // 3. Call Azure OpenAI O3 with strict structured output.
        //    o3 reasons deeply before extracting ambiguous fields (uses its default reasoning effort).
        var response = await _o3Client.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            ResponseFormat = responseFormat
        }, ct);

        // 4. Parse the guaranteed-structured JSON response.
        var (fields, tables) = ParseExtractionResponse(response.Value.Content[0].Text, schema);
        return (fields, tables, 1);
    }

    private static (Dictionary<string, ExtractionFieldResult>,
                    Dictionary<string, List<Dictionary<string, object?>>>)
        ParseExtractionResponse(string json, ExtractionSchema schema)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var fields = new Dictionary<string, ExtractionFieldResult>();
        foreach (var field in schema.Fields)
        {
            if (!root.TryGetProperty(field.Name, out var el)) continue;

            var confidence = el.TryGetProperty("confidence", out var conf)
                ? conf.GetInt32() : 0;
            var rawStr = el.TryGetProperty("extraction_str", out var rs)
                ? rs.GetString() ?? "" : "";
            var extraction = el.GetProperty("extraction");

            fields[field.Name] = new ExtractionFieldResult
            {
                Value      = ConvertValue(extraction, field.Type),
                Confidence = confidence,
                RawStr     = rawStr,
                IsVerified = false   // Set later by VerificationAgent.
            };
        }

        var tables = new Dictionary<string, List<Dictionary<string, object?>>>();
        foreach (var table in schema.TableFields)
        {
            if (!root.TryGetProperty(table.Name, out var arr)
                || arr.ValueKind != JsonValueKind.Array) continue;

            var rows = arr.EnumerateArray().Select(row =>
            {
                var dict = new Dictionary<string, object?>();
                foreach (var sub in table.SubFields)
                {
                    if (!row.TryGetProperty(sub.Name, out var cell)) continue;
                    dict[sub.Name] = ConvertValue(cell, sub.Type);
                }
                return dict;
            }).ToList();

            tables[table.Name] = rows;
        }

        return (fields, tables);
    }

    private static object? ConvertValue(JsonElement el, FieldType type) => type switch
    {
        FieldType.Number  => el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null,
        FieldType.Integer => el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null,
        FieldType.Boolean => el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? el.GetBoolean() : null,
        _ => el.ValueKind == JsonValueKind.Null ? null : el.GetString()
    };
}
