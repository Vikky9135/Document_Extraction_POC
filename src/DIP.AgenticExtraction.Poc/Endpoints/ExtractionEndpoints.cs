using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Ocr;
using DIP.AgenticExtraction.Poc.Orchestration;
using DIP.AgenticExtraction.Poc.Schema;
using DIP.AgenticExtraction.Poc.Services;
using Microsoft.AspNetCore.Mvc;

namespace DIP.AgenticExtraction.Poc.Endpoints;

/// <summary>Unified pipeline request: multipart form with PDFs + prompts.</summary>
public class PipelineRequest
{
    /// <summary>
    /// High-level extraction goal — what you want to achieve.
    /// Example: "Process invoices for accounts payable"
    /// </summary>
    public string? ExtractionGoal { get; set; }

    /// <summary>
    /// Describe exactly what to extract — specific fields, tables, computed values.
    /// Example: "Extract vendor name, invoice number, date, line items (desc, qty, price, amount), subtotal, tax, total. Compute tax percentage."
    /// </summary>
    public string DescribeWhatToExtract { get; set; } = "";

    /// <summary>
    /// PDF files to use as training documents and to extract data from.
    /// </summary>
    public List<IFormFile> Files { get; set; } = [];
}

public static class ExtractionEndpoints
{
    public static void MapExtractionEndpoints(this WebApplication app)
    {
        // GET /health
        app.MapGet("/health", () => Results.Ok(new { status = "Healthy", utc = DateTime.UtcNow }))
           .WithName("Health")
           .WithSummary("Health check");

        // POST /pipeline/run — Unified single-shot pipeline: upload N docs → OCR → Schema Gen → Extract all → Results
        app.MapPost("/pipeline/run", async (
            HttpContext httpContext,
            IOcrPreprocessingService ocrService,
            ISchemaGenerationService schemaService,
            IAgenticExtractionOrchestrator orchestrator,
            IBlobStorageService blobStorage,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Pipeline");
            var form = await httpContext.Request.ReadFormAsync(ct);

            // ── Parse inputs ──────────────────────────────────────────────────
            var extractionGoal = form["extractionGoal"].FirstOrDefault()?.Trim() ?? "";
            var describeWhat = form["describeWhatToExtract"].FirstOrDefault()?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(describeWhat))
                return Results.BadRequest(new { error = "Field 'describeWhatToExtract' is required." });

            var files = form.Files.GetFiles("files");
            if (files.Count == 0)
                files = form.Files.GetFiles("file");
            if (files.Count == 0)
                files = form.Files.Where(f => f.ContentType == "application/pdf"
                    || f.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();

            if (files.Count == 0)
                return Results.BadRequest(new { error = "At least one PDF file is required." });

            var userPrompt = string.IsNullOrWhiteSpace(extractionGoal)
                ? describeWhat
                : $"Goal: {extractionGoal}\n\nFields to extract: {describeWhat}";

            var pipelineId = $"pipeline-{Guid.NewGuid():N}";

            logger.LogInformation("═══════════════════════════════════════════════════════════════");
            logger.LogInformation("PIPELINE ID: {PipelineId}", pipelineId);
            logger.LogInformation("PIPELINE START: {Count} document(s), Goal: {Goal}", files.Count, extractionGoal);
            logger.LogInformation("Describe: {Desc}", describeWhat);
            logger.LogInformation("═══════════════════════════════════════════════════════════════");

            // ── Step 1: OCR all documents in parallel ──────────────────────────
            logger.LogInformation("┌─ STEP 1: Running OCR on {Count} document(s)...", files.Count);

            var ocrTasks = files.Select(async (file, idx) =>
            {
                logger.LogInformation("│  OCR [{Index}/{Total}]: {FileName} ({Size} bytes)",
                    idx + 1, files.Count, file.FileName, file.Length);

                await using var stream = file.OpenReadStream();
                var ocrContext = await ocrService.PrepareAsync(stream, ct);

                logger.LogInformation("│  OCR [{Index}/{Total}]: Done. Pages={Pages}, Words={Words}, Lines={Lines}",
                    idx + 1, files.Count, ocrContext.PageCount, ocrContext.Words.Count, ocrContext.Lines.Count);

                return (FileName: file.FileName, Ocr: ocrContext);
            });

            var ocrResults = await Task.WhenAll(ocrTasks);
            logger.LogInformation("└─ STEP 1 COMPLETE: {Count} documents OCR'd.\n", ocrResults.Length);

            // ── Step 2: Schema Generation from all training docs ──────────────
            logger.LogInformation("┌─ STEP 2: Generating schema from {Count} training doc(s)...", ocrResults.Length);

            var trainingTexts = ocrResults.Select(r => r.Ocr.StructuredText).ToList();
            var schema = await schemaService.GenerateFromMultipleSamplesAsync(trainingTexts, userPrompt, ct);

            logger.LogInformation("│  Schema: Fields={F}, Tables={T}, Generation={G}, Validation={V}",
                schema.Fields.Count, schema.TableFields.Count,
                schema.GenerationFields.Count, schema.ValidationRules.Count);
            logger.LogInformation("│  Fields: {Names}",
                string.Join(", ", schema.Fields.Select(f => $"{f.Name}({f.Role})")));
            if (schema.TableFields.Count > 0)
                logger.LogInformation("│  Tables: {Names}",
                    string.Join(", ", schema.TableFields.Select(t => $"{t.Name}[{t.SubFields.Count} cols]")));
            if (schema.GenerationFields.Count > 0)
                logger.LogInformation("│  Generated: {Names}",
                    string.Join(", ", schema.GenerationFields.Select(g => g.Name)));
            logger.LogInformation("└─ STEP 2 COMPLETE.\n");

            // ── Step 3: Run extraction pipeline on each document ───────────────
            logger.LogInformation("┌─ STEP 3: Running extraction on {Count} document(s)...", ocrResults.Length);

            var extractionResults = new List<object>();
            Dictionary<string, string> generationScripts = [];

            for (int i = 0; i < ocrResults.Length; i++)
            {
                var (fileName, ocr) = ocrResults[i];
                var docId = $"doc-{i + 1}";

                logger.LogInformation("│");
                logger.LogInformation("│  ┌─ Document [{Index}/{Total}]: {FileName}", i + 1, ocrResults.Length, fileName);

                var result = await orchestrator.RunAsync(
                    docId, schema, ocr.StructuredText, userPrompt, ocr.PageCount,
                    ocr.Lines, ocr.Words, ct);

                logger.LogInformation("│  │  Result: {F} fields extracted, {V} verified, {G} generated",
                    result.Fields.Count,
                    result.Fields.Count(f => f.Value.IsVerified),
                    result.GeneratedFields.Count);
                logger.LogInformation("│  │  LLM calls: {Calls}, Corrections: {Iter}, Time: {Ms}ms",
                    result.Metadata.LlmCallCount, result.Metadata.CorrectionIterations,
                    result.Metadata.ProcessingTimeMs);
                logger.LogInformation("│  └─ Document [{Index}/{Total}]: DONE", i + 1, ocrResults.Length);

                // Capture generation scripts from first doc (same schema = same scripts for all)
                if (i == 0 && result.GenerationScripts.Count > 0)
                    generationScripts = result.GenerationScripts;

                extractionResults.Add(new
                {
                    documentIndex = i + 1,
                    fileName,
                    pageCount = ocr.PageCount,
                    result = result with { Metadata = result.Metadata with { OcrPageCount = ocr.PageCount } }
                });
            }

            logger.LogInformation("└─ STEP 3 COMPLETE.\n");
            logger.LogInformation("═══════════════════════════════════════════════════════════════");
            logger.LogInformation("PIPELINE COMPLETE: {Count} document(s) processed.", ocrResults.Length);
            logger.LogInformation("PIPELINE ID: {PipelineId}", pipelineId);
            logger.LogInformation("═══════════════════════════════════════════════════════════════");

            // ── Save to blob for later retrieval ──────────────────────────────
            var pipelineResult = new
            {
                pipelineId,
                extractionGoal,
                describeWhatToExtract = describeWhat,
                documentsProcessed = ocrResults.Length,
                schema = new
                {
                    fieldsCount = schema.Fields.Count,
                    tableFieldsCount = schema.TableFields.Count,
                    generationFieldsCount = schema.GenerationFields.Count,
                    validationRulesCount = schema.ValidationRules.Count,
                    fields = schema.Fields.Select(f => new { f.Name, type = f.Type.ToString(), role = f.Role.ToString() }),
                    tableFields = schema.TableFields.Select(t => new { t.Name, subFields = t.SubFields.Select(sf => sf.Name) }),
                    generationFields = schema.GenerationFields.Select(g => new { g.Name, g.Instructions }),
                    validationRules = schema.ValidationRules
                },
                results = extractionResults
            };

            await blobStorage.SaveJsonAsync(pipelineId, "pipeline-result.json", pipelineResult, ct);
            await blobStorage.SaveJsonAsync(pipelineId, "schema.json", schema, ct);

            // Save generation scripts (C# expressions executed by Roslyn) if any were produced
            if (generationScripts.Count > 0)
            {
                await blobStorage.SaveJsonAsync(pipelineId, "generation-scripts.json", generationScripts, ct);
                logger.LogInformation("Generation scripts saved: {Fields}", string.Join(", ", generationScripts.Keys));
            }

            logger.LogInformation("Results saved to blob: {Container}/{PipelineId}/pipeline-result.json", "agentic-poc-jobs", pipelineId);

            return Results.Ok(pipelineResult);
        })
        .DisableAntiforgery()
        .WithName("RunPipeline")
        .WithSummary("Unified pipeline: upload N training PDFs → OCR → Schema Gen → Extract all → Results")
        .Accepts<PipelineRequest>("multipart/form-data")
        .Produces(200)
        .Produces<ProblemDetails>(400);
    }
}
