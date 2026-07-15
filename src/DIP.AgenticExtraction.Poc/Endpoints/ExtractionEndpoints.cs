using System.Threading.Channels;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Ocr;
using DIP.AgenticExtraction.Poc.Services;
using Microsoft.AspNetCore.Mvc;

namespace DIP.AgenticExtraction.Poc.Endpoints;

// DTOs — give Scalar the correct request schemas

/// <summary>Multipart upload: PDF file only.</summary>
public class UploadJobRequest
{
    /// <summary>PDF file to extract data from.</summary>
    public IFormFile File { get; set; } = null!;
}

/// <summary>Extraction trigger: natural-language prompt (JSON body).</summary>
public record ExtractRequest(
    /// <summary>
    /// What to extract. Leave blank to auto-discover every field in the document.
    /// Examples:
    /// - "Extract vendor name, invoice date, total amount and all line items."
    /// - "Extract patient name, DOB, diagnosis codes and medications."
    /// </summary>
    string? UserPrompt
);

public static class ExtractionEndpoints
{
    public static void MapExtractionEndpoints(this WebApplication app)
    {
        // GET /health
        app.MapGet("/health", () => Results.Ok(new { status = "Healthy", utc = DateTime.UtcNow }))
           .WithName("Health")
           .WithSummary("Health check");

        // POST /jobs/upload  — multipart: PDF file only, no prompt needed
        app.MapPost("/jobs/upload", async (
            HttpContext httpContext,
            IBlobStorageService blobStorage,
            IJobStore jobStore,
            IOcrPreprocessingService ocrService,
            CancellationToken ct) =>
        {
            var form = await httpContext.Request.ReadFormAsync(ct);

            // Accept "file" field name OR the first file sent (Scalar may use its own name)
            var file = form.Files.GetFile("file")
                    ?? form.Files.GetFile("File")
                    ?? form.Files.FirstOrDefault();

            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "A PDF file is required." });

            if (!file.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
                && !file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Only PDF files are supported." });

            var jobId = Guid.NewGuid().ToString("N");

            // Store the source PDF
            await using var stream = file.OpenReadStream();
            await blobStorage.UploadPdfAsync(jobId, stream, ct);

            // Run OCR immediately during upload
            stream.Position = 0;
            var ocrContext = await ocrService.PrepareAsync(stream, ct);

            await blobStorage.SaveJsonAsync(jobId, "ocr-context.json", new
            {
                pageCount      = ocrContext.PageCount,
                structuredText = ocrContext.StructuredText
            }, ct);

            jobStore.Set(jobId, JobStatus.Uploaded);

            return Results.Accepted($"/jobs/{jobId}/status", new
            {
                jobId,
                ocrPageCount = ocrContext.PageCount,
                ocrTextLength = ocrContext.StructuredText.Length
            });
        })
        .DisableAntiforgery()
        .WithName("UploadJob")
        .WithSummary("Step 1 — Upload a PDF, runs OCR immediately")
        .Accepts<UploadJobRequest>("multipart/form-data")
        .Produces(202)
        .Produces<ProblemDetails>(400);

        // POST /jobs/{id}/extract  — JSON body with optional userPrompt, queues the job
        app.MapPost("/jobs/{id}/extract", async (
            string id,
            ExtractRequest body,
            IJobStore jobStore,
            ChannelWriter<ExtractionJob> queue,
            CancellationToken ct) =>
        {
            var (status, _) = jobStore.Get(id);
            if (status != JobStatus.Uploaded)
                return Results.BadRequest(new { error = $"Job must be in 'Uploaded' state before extraction. Current state: {status}." });

            var userPrompt = string.IsNullOrWhiteSpace(body?.UserPrompt)
                ? "Extract all fields, tables, dates, amounts, names, and identifiers found in this document. Discover every meaningful piece of structured data."
                : body.UserPrompt;

            var job = new ExtractionJob
            {
                JobId      = id,
                BlobPath   = $"jobs/{id}/source.pdf",
                UserPrompt = userPrompt
            };

            jobStore.Set(id, JobStatus.Queued);
            await queue.WriteAsync(job, ct);

            return Results.Accepted($"/jobs/{id}/status", new { jobId = id, userPrompt });
        })
        .WithName("ExtractJob")
        .WithSummary("Step 2 — Trigger schema generation + extraction (optional prompt)")
        .Produces(202)
        .Produces<ProblemDetails>(400);

        // GET /jobs/{id}/status
        app.MapGet("/jobs/{id}/status", (
            string id,
            IJobStore jobStore) =>
        {
            var (status, error) = jobStore.Get(id);
            return Results.Ok(new { jobId = id, status = status.ToString(), error });
        })
        .WithName("GetJobStatus");

        // GET /jobs/{id}/result
        app.MapGet("/jobs/{id}/result", async (
            string id,
            IBlobStorageService blobStorage,
            IJobStore jobStore,
            CancellationToken ct) =>
        {
            var (status, error) = jobStore.Get(id);

            if (status == JobStatus.Failed)
                return Results.UnprocessableEntity(new { jobId = id, status = status.ToString(), error });

            if (status != JobStatus.Completed)
                return Results.Accepted($"/jobs/{id}/status",
                    new { jobId = id, status = status.ToString(), message = "Processing — check back shortly." });

            var result = await blobStorage.LoadJsonAsync<ExtractionJobResult>(id, "result.json", ct);
            return result is null
                ? Results.NotFound(new { jobId = id, message = "Result file not found in blob storage." })
                : Results.Ok(result);
        })
        .WithName("GetJobResult");
    }
}
