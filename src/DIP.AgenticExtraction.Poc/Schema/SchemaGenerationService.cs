using System.Text.Json;
using System.Text.RegularExpressions;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Prompts;
using OpenAI.Chat;

namespace DIP.AgenticExtraction.Poc.Schema;

public interface ISchemaGenerationService
{
    /// <summary>Single document schema generation — ONE LLM call produces full schema.</summary>
    Task<ExtractionSchema> GenerateFromSamplesAsync(
        string structuredText,
        string userPrompt,
        CancellationToken ct = default);

    /// <summary>Multi-document schema generation: parallel per-doc → merge → normalize → concatenate+gen+val (one LLM call).</summary>
    Task<ExtractionSchema> GenerateFromMultipleSamplesAsync(
        IReadOnlyList<string> trainingDocTexts,
        string userPrompt,
        CancellationToken ct = default);
}

// Phase 4 — Restructured pipeline:
//   Single-doc:  1 LLM call → full schema (fields + gen + val)
//   Multi-doc:   N parallel per-doc calls → code merge → normalize → 1 LLM call (dedup + gen + val)
// Followed by programmatic validation to catch field-name drift.
public class SchemaGenerationService : ISchemaGenerationService
{
    private readonly ChatClient _gpt5Client;
    private readonly ILogger<SchemaGenerationService> _logger;

    public SchemaGenerationService(ChatClient gpt5Client, ILogger<SchemaGenerationService> logger)
    {
        _gpt5Client = gpt5Client;
        _logger     = logger;
    }

    // ── Public entry point (single doc) ───────────────────────────────────────
    // ONE LLM call produces fields + tableFields + generationFields + validationRules.
    public async Task<ExtractionSchema> GenerateFromSamplesAsync(
        string structuredText,
        string userPrompt,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Phase 4 (single-doc): Generating full schema in one LLM call...");

        var schema = await GenerateFullSchemaAsync(structuredText, userPrompt, ct);

        _logger.LogInformation(
            "Phase 4 complete. Fields={F}, Tables={T}, Gen={G}, Val={V}",
            schema.Fields.Count, schema.TableFields.Count,
            schema.GenerationFields.Count, schema.ValidationRules.Count);

        return ValidateSchemaConsistency(schema);
    }

    // ── Public entry point (multi-doc) ────────────────────────────────────────
    // Step 1: Per-doc extraction schema (parallel, fields + tableFields only)
    // Step 2: Code merge + field name normalization
    // Step 3: Concatenate Agent (dedup + gen + val) — ONE LLM call
    // Step 4: Programmatic validation
    public async Task<ExtractionSchema> GenerateFromMultipleSamplesAsync(
        IReadOnlyList<string> trainingDocTexts,
        string userPrompt,
        CancellationToken ct = default)
    {
        if (trainingDocTexts.Count == 0)
            throw new ArgumentException("At least one training document is required.", nameof(trainingDocTexts));

        // Fast path: single doc uses the combined prompt
        if (trainingDocTexts.Count == 1)
            return await GenerateFromSamplesAsync(trainingDocTexts[0], userPrompt, ct);

        // ── STEP 1: Generate extraction schema per file in parallel ───────────
        _logger.LogInformation(
            "Phase 4 Step 1: Generating extraction schemas from {Count} training doc(s) in parallel...",
            trainingDocTexts.Count);

        var perDocTasks = trainingDocTexts.Select(
            docText => GenerateExtractionSchemaAsync(docText, userPrompt, ct));
        var perDocSchemas = await Task.WhenAll(perDocTasks);

        _logger.LogInformation("Phase 4 Step 1 complete: {Count} per-doc schemas generated.", perDocSchemas.Length);

        // ── STEP 2: Code merge + field name normalization ─────────────────────
        _logger.LogInformation("Phase 4 Step 2: Merging and normalizing {Count} schemas...", perDocSchemas.Length);
        var mergedSchema = MergeSchemas(perDocSchemas);
        mergedSchema = NormalizeFieldNames(mergedSchema);

        _logger.LogInformation(
            "Phase 4 Step 2 complete. Merged fields={F}, tables={T}",
            mergedSchema.Fields.Count, mergedSchema.TableFields.Count);

        // ── STEP 3: Concatenate Agent — dedup + gen + val in ONE LLM call ─────
        _logger.LogInformation("Phase 4 Step 3: Dedup + generation + validation via LLM...");
        var finalSchema = await DeduplicateAndCompleteSchemaAsync(mergedSchema, userPrompt, ct);

        _logger.LogInformation(
            "Phase 4 complete. Fields={F}, Tables={T}, Gen={G}, Val={V}",
            finalSchema.Fields.Count, finalSchema.TableFields.Count,
            finalSchema.GenerationFields.Count, finalSchema.ValidationRules.Count);

        // ── STEP 4: Programmatic validation ───────────────────────────────────
        return ValidateSchemaConsistency(finalSchema);
    }

    // ── Step 2: Pure-code merge — union fields by name, merge synonyms ─────────
    // Groups by exact name (case-insensitive). For each group, all variant names
    // from the group members (including the name itself, if different casing) are
    // added to synonyms so the extraction agent can locate values in any doc format.
    private static ExtractionSchema MergeSchemas(ExtractionSchema[] schemas)
    {
        var mergedFields = schemas
            .SelectMany(s => s.Fields)
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                // Collect all synonyms + all variant field names from the group
                var allSynonyms = g.SelectMany(f => f.Synonyms)
                    .Concat(g.Select(f => f.Name))        // include all name variants
                    .Where(s => !s.Equals(first.Name, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var longestDescription = g.OrderByDescending(f => f.Description.Length).First().Description;
                // If any instance has role "extract", the merged field is "extract"
                var role = g.Any(f => f.Role == FieldRole.Extract) ? FieldRole.Extract : FieldRole.Source;
                return first with { Synonyms = allSynonyms, Description = longestDescription, Role = role };
            })
            .ToList();

        var mergedTables = schemas
            .SelectMany(s => s.TableFields)
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                var allSynonyms = g.SelectMany(t => t.Synonyms)
                    .Concat(g.Select(t => t.Name))
                    .Where(s => !s.Equals(first.Name, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var longestDescription = g.OrderByDescending(t => t.Description.Length).First().Description;
                var mergedSubFields = g
                    .SelectMany(t => t.SubFields)
                    .GroupBy(sf => sf.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(sfg => sfg.OrderByDescending(sf => sf.Description.Length).First())
                    .ToList();
                return new TableField
                {
                    Name = first.Name,
                    Description = longestDescription,
                    Synonyms = allSynonyms,
                    SubFields = mergedSubFields
                };
            })
            .ToList();

        return new ExtractionSchema { Fields = mergedFields, TableFields = mergedTables };
    }

    // ── Step 3: Concatenate Agent — dedup + gen + val in ONE LLM call ───────
    // For multi-doc: takes the merged+normalized schema, deduplicates, and produces
    // generationFields + validationRules in the same call.
    private async Task<ExtractionSchema> DeduplicateAndCompleteSchemaAsync(
        ExtractionSchema mergedSchema, string userPrompt, CancellationToken ct)
    {
        var mergedJson = JsonSerializer.Serialize(new
        {
            fields = mergedSchema.Fields.Select(f => new
            {
                name = f.Name,
                type = f.Type.ToString(),
                description = f.Description,
                synonyms = f.Synonyms,
                fieldFormat = f.FieldFormat,
                role = f.Role == FieldRole.Source ? "source" : "extract"
            }),
            tableFields = mergedSchema.TableFields.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                synonyms = t.Synonyms,
                subFields = t.SubFields.Select(sf => new
                {
                    name = sf.Name,
                    type = sf.Type.ToString(),
                    description = sf.Description
                })
            })
        }, new JsonSerializerOptions { WriteIndented = false });

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompts.SchemaConcatenateSystemPrompt),
            new UserChatMessage(
                $"User request: {userPrompt}\n\n" +
                $"Merged schema from multiple documents:\n{mergedJson}")
        };

        var response = await _gpt5Client.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                "full_schema",
                BinaryData.FromString(FullSchemaJsonSchema),
                null,
                true)
        }, ct);

        return ParseFullSchema(response.Value.Content[0].Text);
    }

    // ── Single-doc full schema call ──────────────────────────────────────────
    // Produces fields + tableFields + generationFields + validationRules in one shot.
    private async Task<ExtractionSchema> GenerateFullSchemaAsync(
        string structuredText, string userPrompt, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompts.SchemaGenFullSystemPrompt),
            new UserChatMessage(
                $"User request: {userPrompt}\n\n" +
                $"Document OCR text:\n{structuredText}")
        };

        var response = await _gpt5Client.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                "full_schema",
                BinaryData.FromString(FullSchemaJsonSchema),
                null,
                true)
        }, ct);

        return ParseFullSchema(response.Value.Content[0].Text);
    }

    // ── Per-doc extraction schema (multi-doc Step 1 only — fields + tables) ──
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

    // ── Field name normalization (Step 2b) ────────────────────────────────────
    // Converts all field names to consistent camelCase before sending to the LLM.
    // This pins the names so the LLM cannot arbitrarily rename them.
    private static ExtractionSchema NormalizeFieldNames(ExtractionSchema schema)
    {
        var normalizedFields = schema.Fields.Select(f => f with
        {
            Name = NormalizeToCamelCase(f.Name),
            Synonyms = f.Synonyms
                .Concat([f.Name])   // preserve original name as synonym if different
                .Select(s => s.Trim())
                .Where(s => !s.Equals(NormalizeToCamelCase(f.Name), StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        }).ToList();

        var normalizedTables = schema.TableFields.Select(t => new TableField
        {
            Name = NormalizeToCamelCase(t.Name),
            Description = t.Description,
            Synonyms = t.Synonyms
                .Concat([t.Name])
                .Select(s => s.Trim())
                .Where(s => !s.Equals(NormalizeToCamelCase(t.Name), StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            SubFields = t.SubFields.Select(sf => new SubField
            {
                Name = NormalizeToCamelCase(sf.Name),
                Type = sf.Type,
                Description = sf.Description
            }).ToList()
        }).ToList();

        return schema with { Fields = normalizedFields, TableFields = normalizedTables };
    }

    // Converts "total_amount", "Total Amount", "TotalAmount" → "totalAmount"
    private static string NormalizeToCamelCase(string name)
    {
        // Split on underscores, hyphens, spaces, or camelCase boundaries
        var words = Regex.Split(name, @"[_\-\s]+|(?<=[a-z])(?=[A-Z])")
            .Where(w => w.Length > 0)
            .ToArray();

        if (words.Length == 0) return name;

        return words[0].ToLowerInvariant() +
               string.Concat(words.Skip(1).Select(w =>
                   char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    // ── Programmatic validation (Step 4) ──────────────────────────────────────
    // Ensures generationFields and validationRules only reference fields that exist.
    private ExtractionSchema ValidateSchemaConsistency(ExtractionSchema schema)
    {
        var validFieldNames = schema.Fields.Select(f => f.Name)
            .Concat(schema.TableFields.Select(t => t.Name))
            .Concat(schema.TableFields.SelectMany(t => t.SubFields.Select(sf => sf.Name)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Filter out validation rules referencing non-existent fields
        var validRules = schema.ValidationRules
            .Where(v => validFieldNames.Contains(v.FieldName))
            .ToList();

        var droppedRules = schema.ValidationRules.Count - validRules.Count;
        if (droppedRules > 0)
            _logger.LogWarning(
                "Schema validation: Dropped {Count} validation rule(s) referencing non-existent fields.",
                droppedRules);

        // Filter out generation fields referencing non-existent fields in instructions
        var validGenFields = schema.GenerationFields
            .Where(g =>
            {
                // Check if at least one known field name appears in the instructions
                var referencesKnownField = validFieldNames.Any(fn =>
                    g.Instructions.Contains(fn, StringComparison.OrdinalIgnoreCase));
                var isNotApplicable = g.Instructions.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
                return referencesKnownField && !isNotApplicable;
            })
            .ToList();

        var droppedGen = schema.GenerationFields.Count - validGenFields.Count;
        if (droppedGen > 0)
            _logger.LogWarning(
                "Schema validation: Dropped {Count} generation field(s) with invalid field references.",
                droppedGen);

        return schema with
        {
            GenerationFields = validGenFields,
            ValidationRules  = validRules
        };
    }

    // ── JSON Schemas (strict mode — additionalProperties:false everywhere) ────

    // Per-doc extraction only (multi-doc Step 1): fields + tableFields
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
                "required": ["name","type","description","synonyms","fieldFormat","useVisionExtraction","role"],
                "properties": {
                  "name":                { "type": "string" },
                  "type":                { "type": "string", "enum": ["String","Number","Date","Integer","Time","Boolean"] },
                  "description":         { "type": "string" },
                  "synonyms":            { "type": "array", "items": { "type": "string" } },
                  "fieldFormat":         { "anyOf": [{"type": "string"}, {"type": "null"}] },
                  "useVisionExtraction": { "type": "boolean" },
                  "role":                { "type": "string", "enum": ["extract","source"] }
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

    // Full schema (single-doc + multi-doc Step 3): fields + tables + gen + val
    private const string FullSchemaJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["fields", "tableFields", "generationFields", "validationRules"],
          "properties": {
            "fields": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name","type","description","synonyms","fieldFormat","useVisionExtraction","role"],
                "properties": {
                  "name":                { "type": "string" },
                  "type":                { "type": "string", "enum": ["String","Number","Date","Integer","Time","Boolean"] },
                  "description":         { "type": "string" },
                  "synonyms":            { "type": "array", "items": { "type": "string" } },
                  "fieldFormat":         { "anyOf": [{"type": "string"}, {"type": "null"}] },
                  "useVisionExtraction": { "type": "boolean" },
                  "role":                { "type": "string", "enum": ["extract","source"] }
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
            },
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

    // Parses extraction-only schema (per-doc Step 1 output)
    private static ExtractionSchema ParseExtractionSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var fields = ParseFields(root);
        var tableFields = ParseTableFields(root);

        return new ExtractionSchema { Fields = fields, TableFields = tableFields };
    }

    // Parses full schema (single-doc or concatenate agent output)
    private static ExtractionSchema ParseFullSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var fields = ParseFields(root);
        var tableFields = ParseTableFields(root);

        var generationFields = root.GetProperty("generationFields").EnumerateArray()
            .Select(g => new GenerationField
            {
                Name         = g.GetProperty("name").GetString()!,
                Instructions = g.GetProperty("instructions").GetString()!,
                Type         = Enum.Parse<FieldType>(g.GetProperty("type").GetString()!),
                FieldFormat  = g.GetProperty("fieldFormat").ValueKind == JsonValueKind.Null
                                ? null : g.GetProperty("fieldFormat").GetString()
            }).ToList();

        var validationRules = root.GetProperty("validationRules").EnumerateArray()
            .Select(v => new ValidationRule
            {
                FieldName    = v.GetProperty("fieldName").GetString()!,
                Condition    = v.GetProperty("condition").GetString()!,
                ErrorMessage = v.GetProperty("errorMessage").GetString()!,
                Severity     = Enum.Parse<ValidationSeverity>(v.GetProperty("severity").GetString()!)
            }).ToList();

        return new ExtractionSchema
        {
            Fields           = fields,
            TableFields      = tableFields,
            GenerationFields = generationFields,
            ValidationRules  = validationRules
        };
    }

    private static List<GenericField> ParseFields(JsonElement root)
    {
        return root.GetProperty("fields").EnumerateArray()
            .Select(f => new GenericField
            {
                Name                = f.GetProperty("name").GetString()!,
                Type                = Enum.Parse<FieldType>(f.GetProperty("type").GetString()!),
                Description         = f.GetProperty("description").GetString()!,
                Synonyms            = f.GetProperty("synonyms").EnumerateArray()
                                       .Select(s => s.GetString()!).ToList(),
                FieldFormat         = f.GetProperty("fieldFormat").ValueKind == JsonValueKind.Null
                                       ? null : f.GetProperty("fieldFormat").GetString(),
                UseVisionExtraction = f.GetProperty("useVisionExtraction").GetBoolean(),
                Role                = f.TryGetProperty("role", out var r) && r.GetString() == "source"
                                       ? FieldRole.Source : FieldRole.Extract
            }).ToList();
    }

    private static List<TableField> ParseTableFields(JsonElement root)
    {
        return root.GetProperty("tableFields").EnumerateArray()
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
    }
}
