using System.Text.Json;
using System.Text.RegularExpressions;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Ocr;
using DIP.AgenticExtraction.Poc.Orchestration;
using DIP.AgenticExtraction.Poc.Schema;
using DIP.AgenticExtraction.Poc.Services;
using Microsoft.AspNetCore.Mvc;

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
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Extraction.Run");

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
                    RawAnalyzeResult = new object(), // Not stored — not needed for extraction
                    PageCount = ocrData.PageCount,
                    Words = ocrData.Words ?? [],
                    Lines = ocrData.Lines ?? []
                };
                ocrDocuments.Add((ocrData.FileName, ocrContext));
            }

            if (ocrDocuments.Count == 0)
                return Results.NotFound(new { error = "Could not load any OCR data for this classification." });

            // ── Step 1: Schema Generation from all documents ──────────────────
            logger.LogInformation("┌─ STEP 1: Generating schema from {Count} document(s)...", ocrDocuments.Count);

            var trainingTexts = ocrDocuments.Select(d => d.Ocr.StructuredText).ToList();
            var schema = await schemaService.GenerateFromMultipleSamplesAsync(trainingTexts, userPrompt, ct);

            logger.LogInformation("│  Schema: Fields={F}, Tables={T}, Generation={G}, Validation={V}",
                schema.Fields.Count, schema.TableFields.Count,
                schema.GenerationFields.Count, schema.ValidationRules.Count);
            logger.LogInformation("└─ STEP 1 COMPLETE.\n");

            // Save the final schema
            await blobStorage.SaveJsonAsync(classificationId, "final-schema.json", schema, ct);

            // ── Step 2: Run extraction pipeline on each document ───────────────
            logger.LogInformation("┌─ STEP 2: Running extraction on {Count} document(s)...", ocrDocuments.Count);

            var extractionResults = new Dictionary<string, object>();
            Dictionary<string, string> generationScripts = [];

            for (int i = 0; i < ocrDocuments.Count; i++)
            {
                var (fileName, ocr) = ocrDocuments[i];
                var docId = $"doc-{i + 1}";
                var baseFileName = Path.GetFileNameWithoutExtension(fileName);
                var extractionBlobName = $"{baseFileName}.extraction.json";

                logger.LogInformation("│");
                logger.LogInformation("│  ┌─ Document [{Index}/{Total}]: {FileName}", i + 1, ocrDocuments.Count, fileName);

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
                logger.LogInformation("│  └─ Document [{Index}/{Total}]: DONE", i + 1, ocrDocuments.Count);

                // Save per-document extraction result (only requested fields)
                var docResult = new
                {
                    pageCount = ocr.PageCount,
                    fields = result.RequestedFields,
                    metadata = result.Metadata with { OcrPageCount = ocr.PageCount }
                };
                await blobStorage.SaveJsonAsync(classificationId, extractionBlobName, docResult, ct);

                // Capture generation scripts from first doc
                if (i == 0 && result.GenerationScripts.Count > 0)
                    generationScripts = result.GenerationScripts;

                extractionResults[fileName] = docResult;
            }

            logger.LogInformation("└─ STEP 2 COMPLETE.\n");

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
