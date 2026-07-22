using System.Text.Json.Nodes;
using DIP.AgenticExtraction.Poc.Models;

namespace DIP.AgenticExtraction.Poc.Schema;

// The single most critical class. Converts GenericField[] into a strict JSON Schema
// at runtime, passed to Azure OpenAI with strict: true.
public static class DynamicSchemaGenerator
{
    // Phase 5 — builds the JSON Schema for the ExtractionAgent structured output call.
    // feedbackOverrides injects verifier feedback into per-field descriptions (Phase 7).
    // Returns a schema with "instances" array — each element is one entity instance.
    public static JsonObject GenerateExtractionSchema(
        IReadOnlyList<GenericField> fields,
        IReadOnlyList<TableField> tableFields,
        IReadOnlyDictionary<string, string>? feedbackOverrides = null)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        // Flat fields
        foreach (var field in fields)
        {
            var feedback = feedbackOverrides?.GetValueOrDefault(field.Name);
            properties[field.Name] = BuildFieldSchema(field, feedback);
            required.Add(field.Name);
        }

        // Table fields (arrays of row objects)
        foreach (var table in tableFields)
        {
            properties[table.Name] = BuildTableSchema(table);
            required.Add(table.Name);
        }

        // Add sourcePages — forces the model to declare which pages each instance comes from
        properties["sourcePages"] = new JsonObject
        {
            ["type"]        = "array",
            ["description"] = "Page numbers (1-indexed) from which this instance's data was extracted. " +
                              "Each instance MUST have distinct source pages. Two instances must NOT share the same pages.",
            ["items"]       = new JsonObject { ["type"] = "integer" }
        };
        required.Add("sourcePages");

        // Wrap in an "instances" array to support multi-instance extraction
        var instanceSchema = new JsonObject
        {
            ["type"]                 = "object",
            ["additionalProperties"] = false,
            ["required"]             = required,
            ["properties"]           = properties
        };

        return new JsonObject
        {
            ["type"]                 = "object",
            ["additionalProperties"] = false,
            ["required"]             = new JsonArray("instances"),
            ["properties"]           = new JsonObject
            {
                ["instances"] = new JsonObject
                {
                    ["type"]        = "array",
                    ["description"] = "Array of all entity instances found in the document. Extract ALL instances (e.g., all invoices, all receipts). Always return at least one instance.",
                    ["items"]       = instanceSchema
                }
            }
        };
    }

    // Phase 6 — builds the JSON Schema for the VerificationAgent structured output call.
    // Each field gets: { correct: bool, feedback: string }. The extracted value is
    // embedded in the field description so the verifier fact-checks it against the document.
    public static JsonObject GenerateVerificationSchema(
        IReadOnlyList<GenericField> fields,
        IReadOnlyDictionary<string, ExtractionFieldResult> extractedValues)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var field in fields)
        {
            var extractedStr = extractedValues.TryGetValue(field.Name, out var val)
                ? (string.IsNullOrEmpty(val.RawStr) ? val.Value?.ToString() ?? "NOT_FOUND" : val.RawStr)
                : "NOT_FOUND";

            properties[field.Name] = new JsonObject
            {
                ["type"]                 = "object",
                ["description"]          = $"Field: {field.Name}. Extracted value: '{extractedStr}'. {field.Description} Verify this value is present and correct in the source document. Also verify the extracted value's unit matches what the field expects (e.g., do not accept a percentage where a monetary amount is expected, or vice versa).",
                ["additionalProperties"] = false,
                ["required"]             = new JsonArray("correct", "feedback"),
                ["properties"]           = new JsonObject
                {
                    ["correct"]  = new JsonObject { ["type"] = "boolean" },
                    ["feedback"] = new JsonObject
                    {
                        ["type"]        = "string",
                        ["description"] = "If incorrect, state the correct value and exactly where in the document it appears (page, paragraph, table row). Empty string if correct."
                    }
                }
            };
            required.Add(field.Name);
        }

        return new JsonObject
        {
            ["type"]                 = "object",
            ["additionalProperties"] = false,
            ["required"]             = required,
            ["properties"]           = properties
        };
    }

    private static JsonObject BuildFieldSchema(GenericField field, string? feedbackText)
    {
        return new JsonObject
        {
            ["type"]                 = "object",
            ["description"]          = field.GetDescription(feedbackText),
            ["additionalProperties"] = false,
            ["required"]             = new JsonArray("extraction", "confidence", "extraction_str"),
            ["properties"]           = new JsonObject
            {
                ["extraction"] = JsonTypeFor(field.Type),
                ["confidence"] = new JsonObject
                {
                    ["type"]        = "integer",
                    ["description"] = "Confidence score 0 to 100. 0 = not found. 100 = certain.",
                    ["minimum"]     = 0,
                    ["maximum"]     = 100
                },
                ["extraction_str"] = new JsonObject
                {
                    ["type"]        = "string",
                    ["description"] = "The exact raw string as it appears in the document"
                }
            }
        };
    }

    private static JsonObject BuildTableSchema(TableField table)
    {
        var rowProperties = new JsonObject();
        var rowRequired = new JsonArray();

        foreach (var sub in table.SubFields)
        {
            rowProperties[sub.Name] = JsonTypeFor(sub.Type);
            rowRequired.Add(sub.Name);
        }

        return new JsonObject
        {
            ["type"]        = "array",
            ["description"] = table.Description + (table.Synonyms.Count > 0
                ? $" Also known as: {string.Join(", ", table.Synonyms)}." : ""),
            ["items"] = new JsonObject
            {
                ["type"]                 = "object",
                ["additionalProperties"] = false,
                ["required"]             = rowRequired,
                ["properties"]           = rowProperties
            }
        };
    }

    // Maps FieldType enum to JSON Schema type node.
    private static JsonNode JsonTypeFor(FieldType type) => type switch
    {
        FieldType.Number  => new JsonObject { ["type"] = "number" },
        FieldType.Integer => new JsonObject { ["type"] = "integer" },
        FieldType.Boolean => new JsonObject { ["type"] = "boolean" },
        _                 => new JsonObject { ["type"] = "string" }   // String, Date, Time
    };
}
