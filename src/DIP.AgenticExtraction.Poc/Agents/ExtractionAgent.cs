using System.Text.Json;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Prompts;
using DIP.AgenticExtraction.Poc.Schema;
using OpenAI.Chat;

namespace DIP.AgenticExtraction.Poc.Agents;

public interface IExtractionAgent
{
    // Phase 5 — one structured call for ALL fields across ALL instances in the document.
    // Returns a list of instances, each with its own set of fields and tables.
    Task<(List<ExtractionInstance> Instances, int LlmCallCount)>
        ExtractFieldsAsync(
            ExtractionSchema schema,
            string structuredText,
            IReadOnlyDictionary<string, string>? feedbackOverrides = null,
            CancellationToken ct = default);
}

/// <summary>One extracted entity instance (e.g., one invoice within a multi-invoice document).</summary>
public record ExtractionInstance(
    Dictionary<string, ExtractionFieldResult> Fields,
    Dictionary<string, List<Dictionary<string, object?>>> Tables,
    List<int> SourcePages);

// Phase 5 — uses gpt-5.4 (accuracy-critical).
public class ExtractionAgent : IExtractionAgent
{
    private readonly ChatClient _client;

    public ExtractionAgent(ChatClient client) => _client = client;

    public async Task<(List<ExtractionInstance> Instances, int LlmCallCount)>
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

        // 3. Call Azure OpenAI with strict structured output.
        var response = await _client.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            ResponseFormat = responseFormat
        }, ct);

        // 4. Parse the guaranteed-structured JSON response (array of instances).
        var instances = ParseExtractionResponse(response.Value.Content[0].Text, schema);
        return (instances, 1);
    }

    private static List<ExtractionInstance> ParseExtractionResponse(string json, ExtractionSchema schema)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var instances = new List<ExtractionInstance>();

        // Response shape: { "instances": [ { field1: {...}, field2: {...}, ... }, ... ] }
        if (root.TryGetProperty("instances", out var instancesArray)
            && instancesArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var instanceEl in instancesArray.EnumerateArray())
            {
                var (fields, tables, sourcePages) = ParseSingleInstance(instanceEl, schema);
                instances.Add(new ExtractionInstance(fields, tables, sourcePages));
            }
        }

        // Fallback: if no "instances" wrapper, treat root as a single instance (backward compat)
        if (instances.Count == 0)
        {
            var (fields, tables, sourcePages) = ParseSingleInstance(root, schema);
            instances.Add(new ExtractionInstance(fields, tables, sourcePages));
        }

        return instances;
    }

    private static (Dictionary<string, ExtractionFieldResult>, Dictionary<string, List<Dictionary<string, object?>>>, List<int>)
        ParseSingleInstance(JsonElement root, ExtractionSchema schema)
    {
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
                IsVerified = false
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

        // Parse sourcePages (which pages this instance's data comes from)
        var sourcePages = new List<int>();
        if (root.TryGetProperty("sourcePages", out var sp) && sp.ValueKind == JsonValueKind.Array)
        {
            foreach (var page in sp.EnumerateArray())
            {
                if (page.ValueKind == JsonValueKind.Number)
                    sourcePages.Add(page.GetInt32());
            }
        }

        return (fields, tables, sourcePages);
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
