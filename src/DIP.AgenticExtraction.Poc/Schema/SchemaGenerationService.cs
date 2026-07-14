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

// Phase 4 — mirrors DocuFlow's three schema agents:
//   Call 1  SchemaAgent equivalent          → extraction schema (fields + tableFields)
//   Call 2  SchemaGeneratorAgent equivalent → generation schema (generationFields)
//   Call 3  SchemaValidationAgent equivalent→ validation rules
// Calls 2 and 3 depend on Call 1 but run in parallel with each other.
public class SchemaGenerationService : ISchemaGenerationService
{
    private readonly ChatClient _gpt5Client;
    private readonly ILogger<SchemaGenerationService> _logger;

    public SchemaGenerationService(ChatClient gpt5Client, ILogger<SchemaGenerationService> logger)
    {
        _gpt5Client = gpt5Client;
        _logger     = logger;
    }

    // ── Public entry point ────────────────────────────────────────────────────
    public async Task<ExtractionSchema> GenerateFromSamplesAsync(
        string structuredText,
        string userPrompt,
        CancellationToken ct = default)
    {
        // ── Call 1: extraction schema ─────────────────────────────────────────
        _logger.LogInformation("Phase 4a: Generating extraction schema (fields + tables)...");
        var extractionSchema = await GenerateExtractionSchemaAsync(structuredText, userPrompt, ct);
        _logger.LogInformation(
            "Phase 4a complete. Fields={F}, Tables={T}. Starting 4b + 4c in parallel...",
            extractionSchema.Fields.Count, extractionSchema.TableFields.Count);

        // ── Calls 2 + 3 in parallel ───────────────────────────────────────────
        // Both calls depend on:
        //   (a) userPrompt         — what the user wants to extract
        //   (b) extractionSchema   — Call 1 output: field names, types, descriptions, tables
        // They run in parallel because neither depends on the other's output.
        var schemaSummary = BuildSchemaSummary(extractionSchema);

        var genTask = GenerateGenerationFieldsAsync(schemaSummary, userPrompt, ct);
        var valTask = GenerateValidationRulesAsync(schemaSummary, userPrompt, ct);

        await Task.WhenAll(genTask, valTask);

        _logger.LogInformation(
            "Phase 4 complete. GenerationFields={G}, ValidationRules={V}",
            genTask.Result.Count, valTask.Result.Count);

        return extractionSchema with
        {
            GenerationFields = genTask.Result,
            ValidationRules  = valTask.Result
        };
    }

    // ── Call 1 — extraction schema (fields + tableFields) ────────────────────
    private async Task<ExtractionSchema> GenerateExtractionSchemaAsync(
        string structuredText, string userPrompt, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompts.SchemaGenSystemPrompt),
            new UserChatMessage(
                $"User request: {userPrompt}\n\n" +
                $"Document OCR text:\n{structuredText}")
        };

        var response = await _gpt5Client.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                "extraction_schema",
                BinaryData.FromString(ExtractionSchemaJsonSchema),
                null,
                true)
        }, ct);

        return ParseExtractionSchema(response.Value.Content[0].Text);
    }

    // ── Call 2 — generation schema (generationFields) ────────────────────────
    // Depends on: userPrompt + full extraction schema (field names, types, descriptions, tables)
    private async Task<List<GenerationField>> GenerateGenerationFieldsAsync(
        string schemaSummary, string userPrompt, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompts.SchemaGenGenerationFieldsSystemPrompt),
            new UserChatMessage(
                $"User request: {userPrompt}\n\n" +
                $"Extraction schema from document:\n{schemaSummary}\n\n" +
                $"Which derived or computed fields should be added?")
        };

        var response = await _gpt5Client.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                "generation_fields",
                BinaryData.FromString(GenerationFieldsJsonSchema),
                null,
                true)
        }, ct);

        return ParseGenerationFields(response.Value.Content[0].Text);
    }

    // ── Call 3 — validation schema (validationRules) ─────────────────────────
    // Depends on: userPrompt + full extraction schema (field names, types, descriptions, tables)
    private async Task<List<ValidationRule>> GenerateValidationRulesAsync(
        string schemaSummary, string userPrompt, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompts.SchemaValidationSystemPrompt),
            new UserChatMessage(
                $"User request: {userPrompt}\n\n" +
                $"Extraction schema from document:\n{schemaSummary}")
        };

        var response = await _gpt5Client.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                "validation_rules",
                BinaryData.FromString(ValidationRulesJsonSchema),
                null,
                true)
        }, ct);

        return ParseValidationRules(response.Value.Content[0].Text);
    }

    // ── Schema summary builder ────────────────────────────────────────────────
    // Produces a readable text block from Call 1's output.
    // Passed to both Call 2 and Call 3 so each has the full field context.
    private static string BuildSchemaSummary(ExtractionSchema schema)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("Fields:");
        foreach (var f in schema.Fields)
            sb.AppendLine($"  - {f.Name} ({f.Type}): {f.Description}");

        if (schema.TableFields.Count > 0)
        {
            sb.AppendLine("Tables:");
            foreach (var t in schema.TableFields)
            {
                var cols = string.Join(", ", t.SubFields.Select(s => $"{s.Name} ({s.Type})"));
                sb.AppendLine($"  - {t.Name}: {t.Description} | columns: {cols}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ── JSON Schemas (strict mode — additionalProperties:false everywhere) ────

    // Call 1: fields + tableFields only
    private const string ExtractionSchemaJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["fields", "tableFields"],
          "properties": {
            "fields": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name","type","description","synonyms","fieldFormat","useVisionExtraction"],
                "properties": {
                  "name":                { "type": "string" },
                  "type":                { "type": "string", "enum": ["String","Number","Date","Integer","Time","Boolean"] },
                  "description":         { "type": "string" },
                  "synonyms":            { "type": "array", "items": { "type": "string" } },
                  "fieldFormat":         { "anyOf": [{"type": "string"}, {"type": "null"}] },
                  "useVisionExtraction": { "type": "boolean" }
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
            }
          }
        }
        """;

    // Call 2: generationFields only
    private const string GenerationFieldsJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["generationFields"],
          "properties": {
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

    // Call 3: validationRules only
    private const string ValidationRulesJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["validationRules"],
          "properties": {
            "validationRules": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["fieldName","condition","errorMessage","severity"],
                "properties": {
                  "fieldName":    { "type": "string" },
                  "condition":    { "type": "string" },
                  "errorMessage": { "type": "string" },
                  "severity":     { "type": "string", "enum": ["Warning","Error"] }
                }
              }
            }
          }
        }
        """;

    // ── Parsers ───────────────────────────────────────────────────────────────

    private static ExtractionSchema ParseExtractionSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var fields = root.GetProperty("fields").EnumerateArray()
            .Select(f => new GenericField
            {
                Name                = f.GetProperty("name").GetString()!,
                Type                = Enum.Parse<FieldType>(f.GetProperty("type").GetString()!),
                Description         = f.GetProperty("description").GetString()!,
                Synonyms            = f.GetProperty("synonyms").EnumerateArray()
                                       .Select(s => s.GetString()!).ToList(),
                FieldFormat         = f.GetProperty("fieldFormat").ValueKind == JsonValueKind.Null
                                       ? null : f.GetProperty("fieldFormat").GetString(),
                UseVisionExtraction = f.GetProperty("useVisionExtraction").GetBoolean()
            }).ToList();

        var tableFields = root.GetProperty("tableFields").EnumerateArray()
            .Select(t => new TableField
            {
                Name        = t.GetProperty("name").GetString()!,
                Description = t.GetProperty("description").GetString()!,
                Synonyms    = t.GetProperty("synonyms").EnumerateArray()
                               .Select(s => s.GetString()!).ToList(),
                SubFields   = t.GetProperty("subFields").EnumerateArray()
                               .Select(sf => new SubField
                               {
                                   Name        = sf.GetProperty("name").GetString()!,
                                   Type        = Enum.Parse<FieldType>(sf.GetProperty("type").GetString()!),
                                   Description = sf.GetProperty("description").GetString() ?? ""
                               }).ToList()
            }).ToList();

        return new ExtractionSchema { Fields = fields, TableFields = tableFields };
    }

    private static List<GenerationField> ParseGenerationFields(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("generationFields").EnumerateArray()
            .Select(g => new GenerationField
            {
                Name         = g.GetProperty("name").GetString()!,
                Instructions = g.GetProperty("instructions").GetString()!,
                Type         = Enum.Parse<FieldType>(g.GetProperty("type").GetString()!),
                FieldFormat  = g.GetProperty("fieldFormat").ValueKind == JsonValueKind.Null
                                ? null : g.GetProperty("fieldFormat").GetString()
            }).ToList();
    }

    private static List<ValidationRule> ParseValidationRules(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("validationRules").EnumerateArray()
            .Select(v => new ValidationRule
            {
                FieldName    = v.GetProperty("fieldName").GetString()!,
                Condition    = v.GetProperty("condition").GetString()!,
                ErrorMessage = v.GetProperty("errorMessage").GetString()!,
                Severity     = Enum.Parse<ValidationSeverity>(v.GetProperty("severity").GetString()!)
            }).ToList();
    }
}
