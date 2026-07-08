using DIP.AgenticExtraction.Poc.Models;
using OpenAI.Chat;

namespace DIP.AgenticExtraction.Poc.Schema;

public interface ISchemaGenerationService
{
    Task<ExtractionSchema> GenerateFromSamplesAsync(
        string structuredText,
        string userPrompt,
        CancellationToken ct = default);
}

// Phase 4 — GPT-5 reads the OCR text + userPrompt and auto-discovers the schema.
public class SchemaGenerationService : ISchemaGenerationService
{
    private readonly ChatClient _gpt5Client;

    public SchemaGenerationService(ChatClient gpt5Client) => _gpt5Client = gpt5Client;

    // TODO (Step 6): call GPT-5 to discover fields, types, synonyms, formats,
    // and generation (computed) fields.
    public Task<ExtractionSchema> GenerateFromSamplesAsync(
        string structuredText,
        string userPrompt,
        CancellationToken ct = default)
        => throw new NotImplementedException();
}
