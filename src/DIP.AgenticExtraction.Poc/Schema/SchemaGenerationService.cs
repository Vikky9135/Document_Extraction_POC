using System.Text.Json;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Prompts;
using OpenAI.Chat;

namespace DIP.AgenticExtraction.Poc.Schema;

public interface ISchemaGenerationService
{
    Task<ExtractionSchema> GenerateFromSamplesAsync(
        string structuredText,
        string userPrompt,
        CancellationToken ct = default);
}

// Phase 4 — GPT-5 reads OCR text + userPrompt and auto-discovers the full schema.
public class SchemaGenerationService : ISchemaGenerationService
{
    private readonly ChatClient _gpt5Client;
    private readonly ILogger<SchemaGenerationService> _logger;

    public SchemaGenerationService(ChatClient gpt5Client, ILogger<SchemaGenerationService> logger)
    {
        _gpt5Client = gpt5Client;
        _logger     = logger;
    }

    public async Task<ExtractionSchema> GenerateFromSamplesAsync(
        string structuredText,
        string userPrompt,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Phase 4: Calling GPT-5 for schema generation...");

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                "extraction_schema",
                BinaryData.FromString(SchemaJsonSchema),
                null,
                true)
        };

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompts.SchemaGenSystemPrompt),
            new UserChatMessage(
                $"User request: {userPrompt}\n\n" +
                $"Document OCR text:\n{structuredText}")
        };

        var response = await _gpt5Client.CompleteChatAsync(messages, options, ct);
        var json     = response.Value.Content[0].Text;

        _logger.LogInformation("Phase 4: Schema generation complete.");

        return ParseSchema(json);
    }

    // ── JSON Schema that GPT-5 must conform to ────────────────────────────────
    // additionalProperties:false + all properties in required[] is mandatory
    // for OpenAI strict structured output.
    private const string SchemaJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["fields", "tableFields", "generationFields"],
          "properties": {
            "fields": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name","type","description","synonyms","fieldFormat","useVisionExtraction"],
                "properties": {
                  "name":               { "type": "string" },
                  "type":               { "type": "string", "enum": ["String","Number","Date","Integer","Time","Boolean"] },
                  "description":        { "type": "string" },
                  "synonyms":           { "type": "array", "items": { "type": "string" } },
                  "fieldFormat":        { "anyOf": [{"type": "string"}, {"type": "null"}] },
                  "useVisionExtraction":{ "type": "boolean" }
                }
              }
            },
            "tableFields": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name","description","synonyms","subFields"],
                "properties": {
                  "name":        { "type": "string" },
                  "description": { "type": "string" },
                  "synonyms":    { "type": "array", "items": { "type": "string" } },
                  "subFields": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["name","type","description"],
                      "properties": {
                        "name":        { "type": "string" },
                        "type":        { "type": "string", "enum": ["String","Number","Date","Integer","Time","Boolean"] },
                        "description": { "type": "string" }
                      }
                    }
                  }
                }
              }
            },
            "generationFields": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name","instructions","type","fieldFormat"],
                "properties": {
                  "name":         { "type": "string" },
                  "instructions": { "type": "string" },
                  "type":         { "type": "string", "enum": ["String","Number","Date","Integer","Time","Boolean"] },
                  "fieldFormat":  { "anyOf": [{"type": "string"}, {"type": "null"}] }
                }
              }
            }
          }
        }
        """;

    // ── Parse the GPT-5 JSON response into ExtractionSchema ──────────────────
    private static ExtractionSchema ParseSchema(string json)
    {
        using var doc  = JsonDocument.Parse(json);
        var root       = doc.RootElement;

        var fields = root.GetProperty("fields")
            .EnumerateArray()
            .Select(f => new GenericField
            {
                Name                = f.GetProperty("name").GetString()!,
                Type                = Enum.Parse<FieldType>(f.GetProperty("type").GetString()!),
                Description         = f.GetProperty("description").GetString()!,
                Synonyms            = f.GetProperty("synonyms")
                                       .EnumerateArray()
                                       .Select(s => s.GetString()!)
                                       .ToList(),
                FieldFormat         = f.GetProperty("fieldFormat").ValueKind == JsonValueKind.Null
                                       ? null : f.GetProperty("fieldFormat").GetString(),
                UseVisionExtraction = f.GetProperty("useVisionExtraction").GetBoolean()
            })
            .ToList();

        var tableFields = root.GetProperty("tableFields")
            .EnumerateArray()
            .Select(t => new TableField
            {
                Name        = t.GetProperty("name").GetString()!,
                Description = t.GetProperty("description").GetString()!,
                Synonyms    = t.GetProperty("synonyms")
                               .EnumerateArray()
                               .Select(s => s.GetString()!)
                               .ToList(),
                SubFields   = t.GetProperty("subFields")
                               .EnumerateArray()
                               .Select(sf => new SubField
                               {
                                   Name        = sf.GetProperty("name").GetString()!,
                                   Type        = Enum.Parse<FieldType>(sf.GetProperty("type").GetString()!),
                                   Description = sf.GetProperty("description").GetString() ?? ""
                               })
                               .ToList()
            })
            .ToList();

        var generationFields = root.GetProperty("generationFields")
            .EnumerateArray()
            .Select(g => new GenerationField
            {
                Name         = g.GetProperty("name").GetString()!,
                Instructions = g.GetProperty("instructions").GetString()!,
                Type         = Enum.Parse<FieldType>(g.GetProperty("type").GetString()!),
                FieldFormat  = g.GetProperty("fieldFormat").ValueKind == JsonValueKind.Null
                                ? null : g.GetProperty("fieldFormat").GetString()
            })
            .ToList();

        return new ExtractionSchema
        {
            Fields           = fields,
            TableFields      = tableFields,
            GenerationFields = generationFields
        };
    }
}
