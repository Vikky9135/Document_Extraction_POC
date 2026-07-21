using System.Text.Json;
using DIP.AgenticExtraction.Poc.Agents;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Ocr;
using OpenAI.Chat;

namespace DIP.AgenticExtraction.Poc.Orchestration;

public interface IAgenticExtractionOrchestrator
{
    // Phases 5-9: Extract -> Verify -> Correct (loop) -> Format -> Generate -> assemble result.
    Task<ExtractionJobResult> RunAsync(
        string jobId,
        ExtractionSchema schema,
        string structuredText,
        string userPrompt,
        int ocrPageCount,
        IReadOnlyList<OcrLine> ocrLines,
        IReadOnlyList<OcrWord> ocrWords,
        CancellationToken ct = default);
}

public class AgenticExtractionOrchestrator : IAgenticExtractionOrchestrator
{
    private readonly IExtractionAgent _extractionAgent;
    private readonly IVerificationAgent _verificationAgent;
    private readonly IFormatterAgent _formatterAgent;
    private readonly IGenerationAgent _generationAgent;
    private readonly ChatClient _gpt5MiniClient;
    private readonly ILogger<AgenticExtractionOrchestrator> _logger;

    public AgenticExtractionOrchestrator(
        IExtractionAgent extractionAgent,
        IVerificationAgent verificationAgent,
        IFormatterAgent formatterAgent,
        IGenerationAgent generationAgent,
        ChatClient gpt5MiniClient,
        ILogger<AgenticExtractionOrchestrator> logger)
    {
        _extractionAgent   = extractionAgent;
        _verificationAgent = verificationAgent;
        _formatterAgent    = formatterAgent;
        _generationAgent   = generationAgent;
        _gpt5MiniClient    = gpt5MiniClient;
        _logger            = logger;
    }

    public async Task<ExtractionJobResult> RunAsync(
        string jobId,
        ExtractionSchema schema,
        string structuredText,
        string userPrompt,
        int ocrPageCount,
        IReadOnlyList<OcrLine> ocrLines,
        IReadOnlyList<OcrWord> ocrWords,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int totalLlmCalls = 0;
        int correctionIterations = 0;

        // ── PHASE 5: Initial Extraction ───────────────────────────────────────
        _logger.LogInformation("[{JobId}] Phase 5: Extracting {N} fields, {T} tables",
            jobId, schema.Fields.Count, schema.TableFields.Count);
        var (fields, tables, extractCalls) =
            await _extractionAgent.ExtractFieldsAsync(schema, structuredText, null, ct);
        totalLlmCalls += extractCalls;

        // ── PHASE 6: Initial Verification ─────────────────────────────────────
        _logger.LogInformation("[{JobId}] Phase 6: Verifying all fields", jobId);
        var verification = await _verificationAgent.VerifyFieldsAsync(schema, fields, structuredText, ct);
        totalLlmCalls++;

        foreach (var (name, verdict) in verification.Fields)
            if (fields.TryGetValue(name, out var f))
                fields[name] = f with { IsVerified = verdict.Correct };

        // ── PHASE 7: Correction Loop (up to schema.Options.MaxIter) ────────────
        var maxIter = schema.Options.MaxIter;
        var iter = 0;

        while (!verification.AllCorrect && iter < maxIter)
        {
            iter++;
            correctionIterations++;

            var failedFieldNames = verification.FailedFieldNames.ToHashSet();
            _logger.LogInformation("[{JobId}] Phase 7 iteration {Iter}: re-extracting {Count} failed fields: {Fields}",
                jobId, iter, failedFieldNames.Count, string.Join(", ", failedFieldNames));

            // Inject verifier feedback into the JSON Schema field descriptions.
            var feedbackOverrides = verification.Fields
                .Where(kvp => !kvp.Value.Correct && !string.IsNullOrWhiteSpace(kvp.Value.Feedback))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Feedback);

            var failedFields = schema.Fields.Where(f => failedFieldNames.Contains(f.Name)).ToList();
            var failedSchema = schema with { Fields = failedFields, TableFields = [] };

            // Re-extract only the failed fields.
            var (correctedFields, _, reExtractCalls) =
                await _extractionAgent.ExtractFieldsAsync(failedSchema, structuredText, feedbackOverrides, ct);
            totalLlmCalls += reExtractCalls;

            foreach (var (name, result) in correctedFields)
                fields[name] = result;

            // Re-verify only the corrected fields.
            var reVerification =
                await _verificationAgent.VerifyFieldsAsync(failedSchema, correctedFields, structuredText, ct);
            totalLlmCalls++;

            foreach (var (name, verdict) in reVerification.Fields)
            {
                verification.Fields[name] = verdict;
                if (fields.TryGetValue(name, out var f))
                    fields[name] = f with { IsVerified = verdict.Correct };
            }

            // On the final iteration, penalise confidence for fields still failing.
            if (iter == maxIter)
            {
                foreach (var name in verification.FailedFieldNames.ToList())
                    if (fields.TryGetValue(name, out var f))
                    {
                        fields[name] = f with
                        {
                            Confidence = Math.Max(0, f.Confidence - 30),
                            IsVerified = false
                        };
                        _logger.LogWarning("[{JobId}] Field '{Field}' failed verification after {MaxIter} iterations. Accepted with reduced confidence.",
                            jobId, name, maxIter);
                    }
            }
        }

        // ── PHASE 8: Formatter Agent ──────────────────────────────────────────
        _logger.LogInformation("[{JobId}] Phase 8: Formatting fields", jobId);
        var formatted = await _formatterAgent.FormatAsync(schema, fields, ct);
        totalLlmCalls += formatted.FormatterCallCount;

        // ── PHASE 9: Generation Fields ────────────────────────────────────────
        // Use pre-formatted fields for computation — formatted values may contain
        // locale symbols (commas, currency) that break numeric parsing in Roslyn.
        Dictionary<string, object?> generatedFields = [];
        Dictionary<string, string> generationScripts = [];
        if (schema.GenerationFields.Count > 0)
        {
            _logger.LogInformation("[{JobId}] Phase 9: Computing {Count} generation fields",
                jobId, schema.GenerationFields.Count);
            var genResult = await _generationAgent.ComputeAsync(schema, fields, ct);
            generatedFields = genResult.Results;
            generationScripts = genResult.GeneratedScripts;
            totalLlmCalls += genResult.LlmCallCount;
        }

        sw.Stop();
        _logger.LogInformation("[{JobId}] Pipeline complete. LLM calls: {Calls}, Iterations: {Iter}, Time: {Ms}ms",
            jobId, totalLlmCalls, correctionIterations, sw.ElapsedMilliseconds);

        // ── PHASE 10: Bounding Box Resolution ─────────────────────────────────
        // Match extracted rawStr values back to OCR coordinates for polygon data.
        _logger.LogInformation("[{JobId}] Phase 10: Resolving bounding boxes...", jobId);
        var fieldsWithBounds = ResolveBoundingBoxes(formatted.Fields, ocrLines, ocrWords);

        // ── Resolve Requested Fields ──────────────────────────────────────────
        // Use schema role to determine which fields the user explicitly requested.
        // Fields with role "extract" are user-requested; "source" are intermediate.
        var requestedFields = ResolveRequestedFieldsFromRole(
            schema, fieldsWithBounds, generatedFields);

        return new ExtractionJobResult
        {
            JobId             = jobId,
            Status            = JobStatus.Completed,
            RequestedFields   = requestedFields,
            Fields            = fieldsWithBounds,
            TableFields       = tables,
            GeneratedFields   = generatedFields,
            GenerationScripts = generationScripts,
            Metadata          = new ExtractionMetadata
            {
                LlmCallCount         = totalLlmCalls,
                CorrectionIterations = correctionIterations,
                ProcessingTimeMs     = sw.ElapsedMilliseconds,
                OcrPageCount         = ocrPageCount
            }
        };
    }

    /// <summary>
    /// Uses a lightweight LLM call to identify which fields from the full result
    /// correspond to what the user explicitly asked for in their prompt.
    /// </summary>
    private async Task<Dictionary<string, RequestedFieldResult>> ResolveRequestedFieldsAsync(
        string userPrompt,
        IReadOnlyDictionary<string, ExtractionFieldResult> fields,
        Dictionary<string, object?> generatedFields,
        CancellationToken ct)
    {
        // Build a combined list of all available field names
        var allFieldNames = fields.Keys
            .Concat(generatedFields.Keys)
            .ToList();

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("""
                You are a field matcher. Given a user's extraction request and a list of
                available field names, return ONLY the field names that directly answer
                what the user asked for. Do NOT include auto-discovered fields that the
                user did not request.

                CRITICAL: You MUST return field names EXACTLY as they appear in the provided list.
                Do NOT modify, rename, or invent field names. Copy them character-for-character.

                Return a JSON array of field name strings. No explanation, no markdown.
                Example: ["returnToInvoiceAmount","totalPremiumCollectedWithoutNcb"]
                """),
            new UserChatMessage(
                $"User request: {userPrompt}\n\n" +
                $"Available field names (use EXACTLY these strings):\n{JsonSerializer.Serialize(allFieldNames)}")
        };

        try
        {
            var response = await _gpt5MiniClient.CompleteChatAsync(messages, cancellationToken: ct);
            var raw = response.Value.Content[0].Text.Trim();
            var matchedNames = JsonSerializer.Deserialize<List<string>>(raw) ?? [];

            // Build case-insensitive lookup maps for resilience against LLM casing drift
            var fieldLookup = fields.ToDictionary(kvp => kvp.Key, kvp => kvp, StringComparer.OrdinalIgnoreCase);
            var genLookup = generatedFields.ToDictionary(kvp => kvp.Key, kvp => kvp, StringComparer.OrdinalIgnoreCase);

            var result = new Dictionary<string, RequestedFieldResult>();
            foreach (var name in matchedNames)
            {
                if (fieldLookup.TryGetValue(name, out var fieldKvp))
                {
                    result[fieldKvp.Key] = new RequestedFieldResult
                    {
                        Value      = fieldKvp.Value.Value,
                        Confidence = fieldKvp.Value.Confidence,
                        IsVerified = fieldKvp.Value.IsVerified,
                        Source     = "extracted"
                    };
                }
                else if (genLookup.TryGetValue(name, out var genKvp))
                {
                    result[genKvp.Key] = new RequestedFieldResult
                    {
                        Value      = genKvp.Value,
                        Confidence = null,
                        IsVerified = false,
                        Source     = "generated"
                    };
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve requested fields — returning empty.");
            return [];
        }
    }

    /// <summary>
    /// Deterministically resolves requested fields using schema role.
    /// Fields with role "extract" are user-requested; generated fields are always included.
    /// No LLM call needed — saves one call vs the old approach.
    /// </summary>
    private static Dictionary<string, RequestedFieldResult> ResolveRequestedFieldsFromRole(
        ExtractionSchema schema,
        IReadOnlyDictionary<string, ExtractionFieldResult> fields,
        Dictionary<string, object?> generatedFields)
    {
        var result = new Dictionary<string, RequestedFieldResult>();

        // Include all "extract" role fields (user-requested)
        foreach (var fieldDef in schema.Fields.Where(f => f.Role == FieldRole.Extract))
        {
            if (fields.TryGetValue(fieldDef.Name, out var extracted))
            {
                result[fieldDef.Name] = new RequestedFieldResult
                {
                    Value           = extracted.Value,
                    Confidence      = extracted.Confidence,
                    IsVerified      = extracted.IsVerified,
                    Source          = "extracted",
                    RawStr          = extracted.RawStr,
                    BoundingRegions = extracted.BoundingRegions
                };
            }
        }

        // Include all generated fields (always user-requested by definition)
        foreach (var (name, value) in generatedFields)
        {
            result[name] = new RequestedFieldResult
            {
                Value      = value,
                Confidence = null,
                IsVerified = false,
                Source     = "generated"
            };
        }

        return result;
    }

    /// <summary>
    /// Resolves bounding boxes for all extracted fields by matching their rawStr
    /// against OCR word/line coordinates.
    /// </summary>
    private static Dictionary<string, ExtractionFieldResult> ResolveBoundingBoxes(
        IReadOnlyDictionary<string, ExtractionFieldResult> fields,
        IReadOnlyList<OcrLine> ocrLines,
        IReadOnlyList<OcrWord> ocrWords)
    {
        var result = new Dictionary<string, ExtractionFieldResult>();

        foreach (var (name, field) in fields)
        {
            // Use rawStr if available, fall back to string value
            var searchText = !string.IsNullOrEmpty(field.RawStr)
                ? field.RawStr
                : field.Value?.ToString() ?? "";

            var regions = BoundingBoxLookupService.FindBoundingRegions(searchText, ocrLines, ocrWords);
            result[name] = field with { BoundingRegions = regions };
        }

        return result;
    }
}
