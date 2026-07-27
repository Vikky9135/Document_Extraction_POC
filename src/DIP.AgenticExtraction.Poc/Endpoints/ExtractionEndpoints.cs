using System.Text.Json;
using System.Text.RegularExpressions;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Ocr;
using DIP.AgenticExtraction.Poc.Options;
using DIP.AgenticExtraction.Poc.Orchestration;
using DIP.AgenticExtraction.Poc.Schema;
using DIP.AgenticExtraction.Poc.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DIP.AgenticExtraction.Poc.Endpoints;

/// <summary>Request for uploading documents and running OCR.</summary>
public class UploadRequest
{
    /// <summary>Classification name/identifier for grouping documents.</summary>
    public string ClassificationName { get; set; } = "";

    /// <summary>PDF files to upload and OCR.</summary>
    public List<IFormFile> Files { get; set; } = [];
}

/// <summary>Request for schema generation and extraction.</summary>
public class ExtractionRequest
{
    /// <summary>Classification name (must match what was used in upload).</summary>
    public string ClassificationName { get; set; } = "";

    /// <summary>High-level extraction goal — what you want to achieve.</summary>
    public string? ExtractionGoal { get; set; }

    /// <summary>Describe exactly what to extract — specific fields, tables, computed values.</summary>
    public string DescribeWhatToExtract { get; set; } = "";
}

public static class ExtractionEndpoints
{
    private static readonly Regex SafeNameRegex = new(@"[^a-zA-Z0-9_\-]", RegexOptions.Compiled);

    /// <summary>Sanitize classification name for use as a blob folder name.</summary>
    private static string ToFolderName(string name)
        => SafeNameRegex.Replace(name.Trim(), "_").ToLowerInvariant();

    public static void MapExtractionEndpoints(this WebApplication app)
    {
        // GET /health
        app.MapGet("/health", () => Results.Ok(new { status = "Healthy", utc = DateTime.UtcNow }))
           .WithName("Health")
           .WithSummary("Health check");

        // ─────────────────────────────────────────────────────────────────────
        // ENDPOINT 1: Upload documents + OCR
        // POST /documents/upload
        // ─────────────────────────────────────────────────────────────────────
        app.MapPost("/documents/upload", async (
            HttpContext httpContext,
            IOcrPreprocessingService ocrService,
            IBlobStorageService blobStorage,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Documents.Upload");
            var form = await httpContext.Request.ReadFormAsync(ct);

            // ── Parse inputs ──────────────────────────────────────────────────
            var classificationName = form["classificationName"].FirstOrDefault()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(classificationName))
                return Results.BadRequest(new { error = "Field 'classificationName' is required." });

            var files = form.Files.GetFiles("files");
            if (files.Count == 0)
                files = form.Files.GetFiles("file");
            if (files.Count == 0)
                files = form.Files.Where(f => f.ContentType == "application/pdf"
                    || f.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();

            if (files.Count == 0)
                return Results.BadRequest(new { error = "At least one PDF file is required." });

            var classificationId = ToFolderName(classificationName);

            // Check what already exists in this folder
            var existingBlobs = await blobStorage.ListBlobsAsync(classificationId, ".ocr.json", ct);

            logger.LogInformation("═══════════════════════════════════════════════════════════════");
            logger.LogInformation("UPLOAD: Classification={Classification}, Folder={Folder}, NewDocs={Count}, ExistingDocs={Existing}",
                classificationName, classificationId, files.Count, existingBlobs.Count);
            logger.LogInformation("═══════════════════════════════════════════════════════════════");

            // ── Upload PDFs and run OCR ───────────────────────────────────────
            var documents = new List<object>();

            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var baseFileName = Path.GetFileNameWithoutExtension(file.FileName);
                var pdfBlobName = $"{baseFileName}.pdf";
                var ocrBlobName = $"{baseFileName}.ocr.json";

                logger.LogInformation("┌─ [{Index}/{Total}] Processing: {FileName} ({Size} bytes)",
                    i + 1, files.Count, file.FileName, file.Length);

                // Upload original PDF
                await using var pdfStream = file.OpenReadStream();
                await blobStorage.UploadPdfAsync(classificationId, pdfBlobName, pdfStream, ct);
                logger.LogInformation("│  Uploaded PDF: {Path}", $"{classificationId}/{pdfBlobName}");

                // Run OCR
                await using var ocrStream = file.OpenReadStream();
                var ocrContext = await ocrService.PrepareAsync(ocrStream, ct);

                logger.LogInformation("│  OCR complete: Pages={Pages}, Words={Words}, Lines={Lines}",
                    ocrContext.PageCount, ocrContext.Words.Count, ocrContext.Lines.Count);

                // Save OCR result
                var ocrData = new
                {
                    fileName = file.FileName,
                    pageCount = ocrContext.PageCount,
                    structuredText = ocrContext.StructuredText,
                    words = ocrContext.Words,
                    lines = ocrContext.Lines
                };
                await blobStorage.SaveJsonAsync(classificationId, ocrBlobName, ocrData, ct);
                logger.LogInformation("└─ Saved OCR: {Path}", $"{classificationId}/{ocrBlobName}");

                documents.Add(new
                {
                    index = i + 1,
                    fileName = file.FileName,
                    pdfPath = $"{classificationId}/{pdfBlobName}",
                    ocrPath = $"{classificationId}/{ocrBlobName}",
                    pageCount = ocrContext.PageCount,
                    wordCount = ocrContext.Words.Count,
                    lineCount = ocrContext.Lines.Count
                });
            }

            logger.LogInformation("UPLOAD COMPLETE: {Count} document(s) stored under '{ClassificationId}'",
                files.Count, classificationId);

            return Results.Ok(new
            {
                classificationId,
                classificationName,
                documentsUploaded = files.Count,
                documents
            });
        })
        .DisableAntiforgery()
        .WithName("UploadDocuments")
        .WithSummary("Upload PDF document(s) and run OCR. Returns classification ID for later extraction.")
        .Accepts<UploadRequest>("multipart/form-data")
        .Produces(200)
        .Produces<ProblemDetails>(400);

        // ─────────────────────────────────────────────────────────────────────
        // ENDPOINT 2: Schema generation + Extraction
        // POST /extraction/run
        // ─────────────────────────────────────────────────────────────────────
        app.MapPost("/extraction/run", async (
            [FromBody] ExtractionRequest request,
            ISchemaGenerationService schemaService,
            IAgenticExtractionOrchestrator orchestrator,
            IBlobStorageService blobStorage,
            IOptions<AgenticExtractionOptions> opts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Extraction.Run");
            var options = opts.Value;

            // ── Validate inputs ───────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(request.ClassificationName))
                return Results.BadRequest(new { error = "Field 'classificationName' is required." });

            if (string.IsNullOrWhiteSpace(request.DescribeWhatToExtract))
                return Results.BadRequest(new { error = "Field 'describeWhatToExtract' is required." });

            var classificationId = ToFolderName(request.ClassificationName);

            // ── Load OCR results from stored documents ────────────────────────
            var ocrBlobNames = await blobStorage.ListBlobsAsync(classificationId, ".ocr.json", ct);
            if (ocrBlobNames.Count == 0)
                return Results.NotFound(new { error = $"No documents found for classification '{request.ClassificationName}'. Upload documents first." });

            logger.LogInformation("═══════════════════════════════════════════════════════════════");
            logger.LogInformation("EXTRACTION: Classification={Classification}, Documents={Count}",
                request.ClassificationName, ocrBlobNames.Count);
            logger.LogInformation("Goal: {Goal}", request.ExtractionGoal);
            logger.LogInformation("Describe: {Desc}", request.DescribeWhatToExtract);
            logger.LogInformation("═══════════════════════════════════════════════════════════════");

            var userPrompt = string.IsNullOrWhiteSpace(request.ExtractionGoal)
                ? request.DescribeWhatToExtract
                : $"Goal: {request.ExtractionGoal}\n\nFields to extract: {request.DescribeWhatToExtract}";

            // Load all OCR data
            var ocrDocuments = new List<(string FileName, OcrContext Ocr)>();
            foreach (var blobName in ocrBlobNames)
            {
                var fileName = Path.GetFileName(blobName);
                var ocrData = await blobStorage.LoadJsonAsync<OcrStoredData>(classificationId, fileName, ct);
                if (ocrData is null) continue;

                var ocrContext = new OcrContext
                {
                    StructuredText = ocrData.StructuredText,
                    RawAnalyzeResult = new object(),
                    PageCount = ocrData.PageCount,
                    Words = ocrData.Words ?? [],
                    Lines = ocrData.Lines ?? []
                };
                ocrDocuments.Add((ocrData.FileName, ocrContext));
            }

            if (ocrDocuments.Count == 0)
                return Results.NotFound(new { error = "Could not load any OCR data for this classification." });

            // ── Step 1: Chunked Schema Generation ─────────────────────────────
            logger.LogInformation("┌─ STEP 1: Generating schema from {Count} document(s)...", ocrDocuments.Count);

            var allSchemaChunks = new List<string>();
            foreach (var (fileName, ocr) in ocrDocuments)
            {
                if (PageChunker.NeedsChunking(ocr.PageCount, options.SchemaChunkPages))
                {
                    var chunks = PageChunker.SplitIntoChunks(ocr.StructuredText, options.SchemaChunkPages);
                    logger.LogInformation("│  {FileName}: {Pages} pages → {Chunks} schema chunk(s)",
                        fileName, ocr.PageCount, chunks.Count);
                    allSchemaChunks.AddRange(chunks.Select(c => c.Text));
                }
                else
                {
                    logger.LogInformation("│  {FileName}: {Pages} pages → single chunk (no splitting)",
                        fileName, ocr.PageCount);
                    allSchemaChunks.Add(ocr.StructuredText);
                }
            }

            var schema = await schemaService.GenerateFromMultipleSamplesAsync(allSchemaChunks, userPrompt, ct);

            logger.LogInformation("│  Schema: Fields={F}, Tables={T}, Generation={G}, Validation={V}",
                schema.Fields.Count, schema.TableFields.Count,
                schema.GenerationFields.Count, schema.ValidationRules.Count);
            logger.LogInformation("└─ STEP 1 COMPLETE.\n");

            await blobStorage.SaveJsonAsync(classificationId, "final-schema.json", schema, ct);

            // ── Step 2: Chunked Extraction per document ───────────────────────
            logger.LogInformation("┌─ STEP 2: Running extraction on {Count} document(s)...", ocrDocuments.Count);

            var extractionResults = new Dictionary<string, object>();
            Dictionary<string, string> generationScripts = [];
            var accumulatedFeedback = new Dictionary<string, List<string>>();

            for (int i = 0; i < ocrDocuments.Count; i++)
            {
                var (fileName, ocr) = ocrDocuments[i];
                var baseFileName = Path.GetFileNameWithoutExtension(fileName);
                var extractionBlobName = $"{baseFileName}.extraction.json";

                logger.LogInformation("│");
                logger.LogInformation("│  ┌─ Document [{Index}/{Total}]: {FileName} ({Pages} pages)",
                    i + 1, ocrDocuments.Count, fileName, ocr.PageCount);

                List<InstanceResult> allInstances;
                int totalLlmCalls = 0;
                int totalCorrections = 0;
                long totalTimeMs = 0;

                if (PageChunker.NeedsChunking(ocr.PageCount, options.ExtractionChunkPages))
                {
                    var chunks = PageChunker.SplitIntoChunks(
                        ocr.StructuredText, options.ExtractionChunkPages, options.ExtractionOverlapPages);
                    logger.LogInformation("│  │  Chunked: {Chunks} chunk(s), overlap={Overlap}pg",
                        chunks.Count, options.ExtractionOverlapPages);

                    allInstances = [];
                    for (int c = 0; c < chunks.Count; c++)
                    {
                        var chunk = chunks[c];
                        var chunkId = $"doc-{i + 1}-chunk-{c + 1}";

                        logger.LogInformation("│  │  ┌─ Chunk {C}/{Total}: pages {Start}-{End}",
                            c + 1, chunks.Count, chunk.StartPage, chunk.EndPage);

                        var result = await orchestrator.RunAsync(
                            chunkId, schema, chunk.Text, userPrompt, chunk.PageCount,
                            ocr.Lines, ocr.Words, ct);

                        totalLlmCalls += result.Metadata.LlmCallCount;
                        totalCorrections += result.Metadata.CorrectionIterations;
                        totalTimeMs += result.Metadata.ProcessingTimeMs;
                        AccumulateFeedback(accumulatedFeedback, result.SchemaFeedback);

                        logger.LogInformation("│  │  └─ Chunk {C}: {Inst} instance(s), {Calls} LLM calls",
                            c + 1, result.Instances.Count, result.Metadata.LlmCallCount);

                        allInstances.AddRange(result.Instances);

                        if (c == 0 && result.GenerationScripts.Count > 0 && generationScripts.Count == 0)
                            generationScripts = result.GenerationScripts;
                    }

                    // TODO: Deduplication of overlapping instances (by key field matching)
                    logger.LogInformation("│  │  Total instances before dedup: {Count}", allInstances.Count);
                }
                else
                {
                    var docId = $"doc-{i + 1}";
                    var result = await orchestrator.RunAsync(
                        docId, schema, ocr.StructuredText, userPrompt, ocr.PageCount,
                        ocr.Lines, ocr.Words, ct);

                    allInstances = result.Instances;
                    totalLlmCalls = result.Metadata.LlmCallCount;
                    totalCorrections = result.Metadata.CorrectionIterations;
                    totalTimeMs = result.Metadata.ProcessingTimeMs;
                    AccumulateFeedback(accumulatedFeedback, result.SchemaFeedback);

                    if (i == 0 && result.GenerationScripts.Count > 0)
                        generationScripts = result.GenerationScripts;
                }

                logger.LogInformation("│  │  Result: {InstCount} instance(s), LLM calls: {Calls}, Time: {Ms}ms",
                    allInstances.Count, totalLlmCalls, totalTimeMs);
                logger.LogInformation("│  └─ Document [{Index}/{Total}]: DONE", i + 1, ocrDocuments.Count);

                // Pivot from instance-centric to field-centric:
                // { "fieldName": { count, values: [ { value, confidence, ... }, ... ] } }
                var fieldCentric = new Dictionary<string, List<object>>();
                for (int idx = 0; idx < allInstances.Count; idx++)
                {
                    foreach (var (fieldName, fieldResult) in allInstances[idx].RequestedFields)
                    {
                        if (!fieldCentric.TryGetValue(fieldName, out var list))
                        {
                            list = [];
                            fieldCentric[fieldName] = list;
                        }
                        list.Add(new
                        {
                            fieldResult.Value,
                            fieldResult.Confidence,
                            fieldResult.IsVerified,
                            fieldResult.Source,
                            fieldResult.RawStr,
                            fieldResult.BoundingRegions,
                            instanceIndex = idx,
                            sourcePages = allInstances[idx].SourcePages
                        });
                    }
                }

                // Wrap each field with its own instanceCount
                var fieldsWithCount = fieldCentric.ToDictionary(
                    kvp => kvp.Key,
                    kvp => (object)new { instanceCount = kvp.Value.Count, values = kvp.Value });

                var tableFields = allInstances.Select((inst, idx) =>
                {
                    var tablesByPage = TablePageAlignmentService.GroupRowsByPage(
                        inst.TableFields,
                        inst.SourcePages,
                        ocr.Lines);

                    return new
                    {
                        instanceIndex = idx,
                        sourcePages = inst.SourcePages,
                        tables = inst.TableFields,
                        tablesByPage
                    };
                }).ToList();

                var docResult = new
                {
                    pageCount = ocr.PageCount,
                    fields = fieldsWithCount,
                    tableFields,
                    metadata = new ExtractionMetadata
                    {
                        LlmCallCount = totalLlmCalls,
                        CorrectionIterations = totalCorrections,
                        ProcessingTimeMs = totalTimeMs,
                        OcrPageCount = ocr.PageCount
                    }
                };
                await blobStorage.SaveJsonAsync(classificationId, extractionBlobName, docResult, ct);
                extractionResults[fileName] = docResult;
            }

            logger.LogInformation("└─ STEP 2 COMPLETE.\n");

            // ── Schema Self-Improvement: persist correction hints ─────────────
            if (accumulatedFeedback.Count > 0)
            {
                var improvedFields = schema.Fields.Select(f =>
                {
                    if (!accumulatedFeedback.TryGetValue(f.Name, out var newHints))
                        return f;

                    // Merge new hints with existing, avoiding duplicates
                    var mergedHints = f.Hints.ToList();
                    foreach (var hint in newHints)
                        if (!mergedHints.Contains(hint))
                            mergedHints.Add(hint);

                    // Cap at 5 hints per field to avoid prompt bloat
                    if (mergedHints.Count > 5)
                        mergedHints = mergedHints.TakeLast(5).ToList();

                    return f with { Hints = mergedHints };
                }).ToList();

                schema = schema with { Fields = improvedFields };
                await blobStorage.SaveJsonAsync(classificationId, "final-schema.json", schema, ct);

                logger.LogInformation("Schema self-improvement: updated {Count} field(s) with correction hints",
                    accumulatedFeedback.Count);
            }

            // Save generation scripts if any
            if (generationScripts.Count > 0)
            {
                await blobStorage.SaveJsonAsync(classificationId, "generation-scripts.json", generationScripts, ct);
                logger.LogInformation("Generation scripts saved: {Fields}", string.Join(", ", generationScripts.Keys));
            }

            logger.LogInformation("═══════════════════════════════════════════════════════════════");
            logger.LogInformation("EXTRACTION COMPLETE: {Count} document(s) processed.", ocrDocuments.Count);
            logger.LogInformation("═══════════════════════════════════════════════════════════════");

            var pipelineResult = new
            {
                classificationName = request.ClassificationName,
                classificationFolder = classificationId,
                extractionGoal = request.ExtractionGoal,
                describeWhatToExtract = request.DescribeWhatToExtract,
                documentsProcessed = ocrDocuments.Count,
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

            return Results.Ok(pipelineResult);
        })
        .WithName("RunExtraction")
        .WithSummary("Generate schema and run extraction on all uploaded documents for a classification.")
        .Produces(200)
        .Produces<ProblemDetails>(400)
        .Produces(404);
    }

    /// <summary>
    /// Merges per-run schema feedback into the accumulated dictionary.
    /// </summary>
    private static void AccumulateFeedback(
        Dictionary<string, List<string>> accumulated,
        Dictionary<string, List<string>> runFeedback)
    {
        foreach (var (fieldName, hints) in runFeedback)
        {
            if (!accumulated.TryGetValue(fieldName, out var existing))
            {
                existing = [];
                accumulated[fieldName] = existing;
            }
            foreach (var hint in hints)
                if (!existing.Contains(hint))
                    existing.Add(hint);
        }
    }
}

/// <summary>Stored OCR data shape for deserialization from blob.</summary>
internal class OcrStoredData
{
    public string FileName { get; set; } = "";
    public int PageCount { get; set; }
    public string StructuredText { get; set; } = "";
    public List<OcrWord>? Words { get; set; }
    public List<OcrLine>? Lines { get; set; }
}
