using System.Text.Json;
using DIP.AgenticExtraction.Poc.Agents;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Ocr;
using OpenAI.Chat;

namespace DIP.AgenticExtraction.Poc.Orchestration;

public interface IAgenticExtractionOrchestrator
{
    // Phases 5-9: Extract -> Verify -> Correct (loop) -> Format -> Generate -> assemble result.
    // Returns a list of instance results (one per entity found in the document).
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

        // ── PHASE 5: Initial Extraction (multi-instance) ──────────────────────
        _logger.LogInformation("[{JobId}] Phase 5: Extracting {N} fields, {T} tables (multi-instance)",
            jobId, schema.Fields.Count, schema.TableFields.Count);
        var (instances, extractCalls) =
            await _extractionAgent.ExtractFieldsAsync(schema, structuredText, null, ct);
        totalLlmCalls += extractCalls;

        _logger.LogInformation("[{JobId}] Phase 5: Found {Count} instance(s) in document",
            jobId, instances.Count);

        // ── PHASE 5b: Deduplicate instances with identical sourcePages ────────
        instances = DeduplicateInstances(instances, jobId);
        _logger.LogInformation("[{JobId}] Phase 5b: {Count} instance(s) after dedup",
            jobId, instances.Count);

        // Process each instance through verification → correction → formatting → generation
        var instanceResults = new List<InstanceResult>();
        Dictionary<string, string> generationScripts = [];

        for (int idx = 0; idx < instances.Count; idx++)
        {
            var instance = instances[idx];
            var instanceId = $"{jobId}-inst{idx + 1}";
            var fields = instance.Fields;
            var tables = instance.Tables;

            _logger.LogInformation("[{JobId}] Processing instance {Idx}/{Total}",
                jobId, idx + 1, instances.Count);

            // Build instance context for the verifier — tells it which instance this is
            // and which pages the values should come from.
            var instanceContext = BuildInstanceContext(instances, idx);

            // ── PHASE 6: Verification ─────────────────────────────────────────
            var verification = await _verificationAgent.VerifyFieldsAsync(
                schema, fields, structuredText, instanceContext, ct);
            totalLlmCalls++;

            foreach (var (name, verdict) in verification.Fields)
                if (fields.TryGetValue(name, out var f))
                    fields[name] = f with { IsVerified = verdict.Correct };

            // ── PHASE 7: Correction Loop ──────────────────────────────────────
            var maxIter = schema.Options.MaxIter;
            var iter = 0;

            while (!verification.AllCorrect && iter < maxIter)
            {
                iter++;
                correctionIterations++;

                var failedFieldNames = verification.FailedFieldNames.ToHashSet();
                _logger.LogInformation("[{JobId}] Instance {Idx} correction iter {Iter}: {Count} failed fields",
                    jobId, idx + 1, iter, failedFieldNames.Count);

                var feedbackOverrides = verification.Fields
                    .Where(kvp => !kvp.Value.Correct && !string.IsNullOrWhiteSpace(kvp.Value.Feedback))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Feedback);

                var failedFields = schema.Fields.Where(f => failedFieldNames.Contains(f.Name)).ToList();
                var failedSchema = schema with { Fields = failedFields, TableFields = [] };

                var (correctedInstances, reExtractCalls) =
                    await _extractionAgent.ExtractFieldsAsync(failedSchema, structuredText, feedbackOverrides, ct);
                totalLlmCalls += reExtractCalls;

                // Use first instance from correction (correction is for specific fields)
                if (correctedInstances.Count > 0)
                {
                    foreach (var (name, result) in correctedInstances[0].Fields)
                        fields[name] = result;
                }

                var reVerification =
                    await _verificationAgent.VerifyFieldsAsync(failedSchema,
                        correctedInstances.Count > 0 ? correctedInstances[0].Fields : fields,
                        structuredText, instanceContext, ct);
                totalLlmCalls++;

                foreach (var (name, verdict) in reVerification.Fields)
                {
                    verification.Fields[name] = verdict;
                    if (fields.TryGetValue(name, out var f))
                        fields[name] = f with { IsVerified = verdict.Correct };
                }

                if (iter == maxIter)
                {
                    foreach (var name in verification.FailedFieldNames.ToList())
                        if (fields.TryGetValue(name, out var f))
                        {
                            // Null out the value — showing a wrong value from another
                            // entity is worse than showing nothing.
                            fields[name] = f with
                            {
                                Value = null,
                                Confidence = 0,
                                IsVerified = false,
                                RawStr = ""
                            };
                        }
                }
            }

            // ── PHASE 8: Formatter ────────────────────────────────────────────
            var formatted = await _formatterAgent.FormatAsync(schema, fields, ct);
            totalLlmCalls += formatted.FormatterCallCount;

            // ── PHASE 9: Generation Fields ────────────────────────────────────
            Dictionary<string, object?> generatedFields = [];
            if (schema.GenerationFields.Count > 0)
            {
                var genResult = await _generationAgent.ComputeAsync(schema, fields, ct);
                generatedFields = genResult.Results;
                if (idx == 0) generationScripts = genResult.GeneratedScripts;
                totalLlmCalls += genResult.LlmCallCount;
            }

            // ── PHASE 10: Bounding Box Resolution ─────────────────────────────
            var fieldsWithBounds = ResolveBoundingBoxes(formatted.Fields, ocrLines, ocrWords);

            // ── Resolve Requested Fields ──────────────────────────────────────
            var requestedFields = ResolveRequestedFieldsFromRole(
                schema, fieldsWithBounds, generatedFields);

            instanceResults.Add(new InstanceResult
            {
                RequestedFields = requestedFields,
                Fields          = fieldsWithBounds,
                TableFields     = tables,
                GeneratedFields = generatedFields,
                SourcePages     = instance.SourcePages
            });
        }

        sw.Stop();
        _logger.LogInformation("[{JobId}] Pipeline complete. {InstCount} instances, LLM calls: {Calls}, Corrections: {Iter}, Time: {Ms}ms",
            jobId, instanceResults.Count, totalLlmCalls, correctionIterations, sw.ElapsedMilliseconds);

        return new ExtractionJobResult
        {
            JobId             = jobId,
            Status            = JobStatus.Completed,
            Instances         = instanceResults,
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

    /// <summary>
    /// Builds context string for the verifier about which instance is being verified,
    /// its source pages, and what other instances extracted (for duplicate detection).
    /// </summary>
    private static string BuildInstanceContext(List<ExtractionInstance> instances, int currentIdx)
    {
        var current = instances[currentIdx];
        var pages = current.SourcePages.Count > 0
            ? string.Join(", ", current.SourcePages)
            : "unknown";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== INSTANCE CONTEXT ===");
        sb.AppendLine($"You are verifying instance {currentIdx + 1} of {instances.Count}.");
        sb.AppendLine($"This instance's data should come from page(s): {pages}.");
        sb.AppendLine($"ONLY verify values against the content on page(s) {pages}.");
        sb.AppendLine($"If a value is found on a DIFFERENT page, mark it as INCORRECT and specify the correct page.");

        if (instances.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine("Other instances in this document:");
            for (int i = 0; i < instances.Count; i++)
            {
                if (i == currentIdx) continue;
                var otherPages = instances[i].SourcePages.Count > 0
                    ? string.Join(", ", instances[i].SourcePages)
                    : "unknown";
                sb.AppendLine($"  - Instance {i + 1}: pages {otherPages}");
            }
        }

        sb.AppendLine("=== END INSTANCE CONTEXT ===");
        return sb.ToString();
    }

    /// <summary>
    /// Removes duplicate instances that share the same sourcePages.
    /// When duplicates are found, keeps the first one.
    /// </summary>
    private List<ExtractionInstance> DeduplicateInstances(List<ExtractionInstance> instances, string jobId)
    {
        if (instances.Count <= 1) return instances;

        var deduplicated = new List<ExtractionInstance>();
        var seenPageSets = new HashSet<string>();

        foreach (var instance in instances)
        {
            // Build a key from sorted sourcePages
            var pageKey = instance.SourcePages.Count > 0
                ? string.Join(",", instance.SourcePages.OrderBy(p => p))
                : $"__unknown_{deduplicated.Count}"; // unique key for instances with no pages

            if (seenPageSets.Add(pageKey))
            {
                deduplicated.Add(instance);
            }
            else
            {
                _logger.LogWarning("[{JobId}] Dropping duplicate instance with sourcePages [{Pages}]",
                    jobId, pageKey);
            }
        }

        return deduplicated;
    }
}
