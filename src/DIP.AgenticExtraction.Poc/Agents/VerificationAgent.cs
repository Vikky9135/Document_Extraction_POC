using System.Text.Json;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Prompts;
using DIP.AgenticExtraction.Poc.Schema;
using OpenAI.Chat;

namespace DIP.AgenticExtraction.Poc.Agents;

public interface IVerificationAgent
{
    // Phase 6 — independent, skeptical fact-check of every extracted value.
    // instanceContext provides instance metadata for cross-entity contamination checks.
    Task<VerificationResult> VerifyFieldsAsync(
        ExtractionSchema schema,
        IReadOnlyDictionary<string, ExtractionFieldResult> extracted,
        string structuredText,
        string? instanceContext = null,
        CancellationToken ct = default);
}

// Phase 6 — uses O3 (reasoning_effort: high) with a skeptical system prompt.
public class VerificationAgent : IVerificationAgent
{
    private readonly ChatClient _o3Client;

    public VerificationAgent(ChatClient o3Client) => _o3Client = o3Client;

    public async Task<VerificationResult> VerifyFieldsAsync(
        ExtractionSchema schema,
        IReadOnlyDictionary<string, ExtractionFieldResult> extracted,
        string structuredText,
        string? instanceContext = null,
        CancellationToken ct = default)
    {
        // Only verify flat fields — table rows are not fact-checked in this pass.
        if (schema.Fields.Count == 0)
            return new VerificationResult();

        // Build a verification schema where each field description carries the extracted value:
        // "Field: invoiceDate. Extracted value: '2024-01-15'. Verify this is correct."
        var verificationSchema = DynamicSchemaGenerator.GenerateVerificationSchema(
            schema.Fields, extracted);

        var responseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
            "verification_result",
            BinaryData.FromString(verificationSchema.ToJsonString()),
            null,
            true);

        // Build user message with optional instance context
        var userMessage = string.IsNullOrEmpty(instanceContext)
            ? structuredText
            : $"{instanceContext}\n\n{structuredText}";

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompts.VerifierSystemPrompt),
            new UserChatMessage(userMessage)
        };

        var response = await _o3Client.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            ResponseFormat = responseFormat
        }, ct);

        return ParseVerificationResponse(response.Value.Content[0].Text, schema.Fields);
    }

    private static VerificationResult ParseVerificationResponse(
        string json, IReadOnlyList<GenericField> fields)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var results = new Dictionary<string, VerificationFieldResult>();
        foreach (var field in fields)
        {
            if (!root.TryGetProperty(field.Name, out var el)) continue;
            results[field.Name] = new VerificationFieldResult
            {
                Correct  = el.TryGetProperty("correct", out var c) && c.GetBoolean(),
                Feedback = el.TryGetProperty("feedback", out var f) ? f.GetString() ?? "" : ""
            };
        }

        return new VerificationResult { Fields = results };
    }
}
