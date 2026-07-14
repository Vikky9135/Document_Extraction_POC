using DIP.AgenticExtraction.Poc.Agents;
using DIP.AgenticExtraction.Poc.Models;

namespace DIP.AgenticExtraction.Poc.Orchestration;

public interface IAgenticExtractionOrchestrator
{
    // Phases 5-9: Extract -> Verify -> Correct (loop) -> Format -> Generate -> assemble result.
    Task<ExtractionJobResult> RunAsync(
        string jobId,
        ExtractionSchema schema,
        string structuredText,
        int ocrPageCount,
        CancellationToken ct = default);
}

public class AgenticExtractionOrchestrator : IAgenticExtractionOrchestrator
{
    private readonly IExtractionAgent _extractionAgent;
    private readonly IVerificationAgent _verificationAgent;
    private readonly IFormatterAgent _formatterAgent;
    private readonly IGenerationAgent _generationAgent;
    private readonly ILogger<AgenticExtractionOrchestrator> _logger;

    public AgenticExtractionOrchestrator(
        IExtractionAgent extractionAgent,
        IVerificationAgent verificationAgent,
        IFormatterAgent formatterAgent,
        IGenerationAgent generationAgent,
        ILogger<AgenticExtractionOrchestrator> logger)
    {
        _extractionAgent   = extractionAgent;
        _verificationAgent = verificationAgent;
        _formatterAgent    = formatterAgent;
        _generationAgent   = generationAgent;
        _logger            = logger;
    }

    public async Task<ExtractionJobResult> RunAsync(
        string jobId,
        ExtractionSchema schema,
        string structuredText,
        int ocrPageCount,
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
        if (schema.GenerationFields.Count > 0)
        {
            _logger.LogInformation("[{JobId}] Phase 9: Computing {Count} generation fields",
                jobId, schema.GenerationFields.Count);
            var genResult = await _generationAgent.ComputeAsync(schema, fields, ct);
            generatedFields = genResult.Results;
            totalLlmCalls += genResult.LlmCallCount;
        }

        sw.Stop();
        _logger.LogInformation("[{JobId}] Pipeline complete. LLM calls: {Calls}, Iterations: {Iter}, Time: {Ms}ms",
            jobId, totalLlmCalls, correctionIterations, sw.ElapsedMilliseconds);

        return new ExtractionJobResult
        {
            JobId           = jobId,
            Status          = JobStatus.Completed,
            Fields          = formatted.Fields,
            TableFields     = tables,
            GeneratedFields = generatedFields,
            Metadata        = new ExtractionMetadata
            {
                LlmCallCount         = totalLlmCalls,
                CorrectionIterations = correctionIterations,
                ProcessingTimeMs     = sw.ElapsedMilliseconds,
                OcrPageCount         = ocrPageCount
            }
        };
    }
}
