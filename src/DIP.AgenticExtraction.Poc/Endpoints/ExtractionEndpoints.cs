using System.Text.Json;
using System.Threading.Channels;
using DIP.AgenticExtraction.Poc.Agents;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Options;
using DIP.AgenticExtraction.Poc.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DIP.AgenticExtraction.Poc.Endpoints;

public static class ExtractionEndpoints
{
    public static void MapExtractionEndpoints(this WebApplication app)
    {
        // ── Health ────────────────────────────────────────────────────────────
        app.MapGet("/health", () => Results.Ok(new { status = "Healthy", utc = DateTime.UtcNow }));

        // ── POST /jobs/upload ─────────────────────────────────────────────────
        // Accepts multipart/form-data: file (PDF) + userPrompt (string)
        // Returns 202 Accepted with { jobId, status, pollUrl, resultUrl }
        app.MapPost("/jobs/upload", async (
            IFormFile file,
            [FromForm] string userPrompt,
            IBlobStorageService blobStorage,
            IJobStore jobStore,
            ChannelWriter<ExtractionJob> queue,
            IOptions<AgenticExtractionOptions> opts,
            CancellationToken ct) =>
        {
            // Validate extension
            if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Only PDF files are accepted." });

            // Validate size
            var maxBytes = (long)opts.Value.MaxFileSizeMb * 1024 * 1024;
            if (file.Length > maxBytes)
                return Results.BadRequest(new { error = $"File exceeds the {opts.Value.MaxFileSizeMb} MB limit." });

            var jobId = Guid.CreateVersion7().ToString();

            // Upload PDF to blob storage
            await using var stream = file.OpenReadStream();
            await blobStorage.UploadPdfAsync(jobId, stream, ct);

            // Track job status
            jobStore.Set(jobId, JobStatus.Queued);

            // Enqueue for background processing
            await queue.WriteAsync(new ExtractionJob
            {
                JobId    = jobId,
                BlobPath = $"jobs/{jobId}/source.pdf",
                UserPrompt = userPrompt
            }, ct);

            return Results.Accepted($"/jobs/{jobId}/status", new
            {
                jobId,
                status    = nameof(JobStatus.Queued),
                pollUrl   = $"/jobs/{jobId}/status",
                resultUrl = $"/jobs/{jobId}/result"
            });
        })
        .DisableAntiforgery();  // REST API — no browser form token needed

        // ── POST /schema/generate ─────────────────────────────────────────────
        // Accepts JSON: { userPrompt: string, documentId: string }
        // Returns generated schema
        app.MapPost("/schema/generate", async (
            [FromBody] SchemaGenerationRequest request,
            IGenerationAgent generationAgent,
            IBlobStorageService blobStorage,
            IOptions<AgenticExtractionOptions> opts,
            CancellationToken ct) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.UserPrompt))
                    return Results.BadRequest(new { error = "userPrompt is required." });

                if (string.IsNullOrWhiteSpace(request.DocumentId))
                    return Results.BadRequest(new { error = "documentId is required." });

                // Read OCR content from blob storage
                var jobsFolder = opts.Value.BlobJobsFolderName;
                var ocrContextPath = $"{jobsFolder}/{request.DocumentId}/ocr-context.json";

                var extractionSchemaJson = await blobStorage.ReadBlobAsJsonStringAsync(ocrContextPath, ct);

                if (extractionSchemaJson is null)
                    throw new InvalidOperationException($"OCR context blob not found at: {ocrContextPath}");

                var schemaResponse = await generationAgent.GenerateSchemaAsync(
                    extractionSchemaJson,
                    request.UserPrompt,
                    ct);

                var response = new
                {
                    documentId = request.DocumentId,
                    userPrompt = request.UserPrompt,
                    schema = schemaResponse
                };

                return Results.Ok(response);
            }
            catch (Exception ex)
            {
                var errorCode = ex.GetType().Name;
                var statusCode = ex switch
                {
                    InvalidOperationException => 404,
                    ArgumentException => 400,
                    _ => 500
                };

                return Results.Json(
                    new
                    {
                        error = ex.Message,
                        errorCode = errorCode,
                        exceptionType = ex.GetType().FullName,
                        details = ex.InnerException?.Message
                    },
                    statusCode: statusCode);
            }
        });

        // ── GET /jobs/{id}/status ─────────────────────────────────────────────
        app.MapGet("/jobs/{id}/status", (string id, IJobStore jobStore) =>
        {
            var (status, error) = jobStore.Get(id);
            return Results.Ok(new { jobId = id, status = status.ToString(), error });
        });

        // ── GET /jobs/{id}/result ─────────────────────────────────────────────
        app.MapGet("/jobs/{id}/result", async (
            string id,
            IJobStore jobStore,
            IBlobStorageService blobStorage,
            CancellationToken ct) =>
        {
            var (status, error) = jobStore.Get(id);

            return status switch
            {
                JobStatus.Failed    => Results.Problem(error ?? "Job failed.", statusCode: 500),
                JobStatus.Completed => await LoadResult(id, blobStorage, ct),
                _                   => Results.Accepted($"/jobs/{id}/status",
                                           new { jobId = id, status = status.ToString(), message = "Job is not yet complete. Poll /status." })
            };
        });
    }

    private static async Task<IResult> LoadResult(
        string jobId,
        IBlobStorageService blobStorage,
        CancellationToken ct)
    {
        var result = await blobStorage.LoadJsonAsync<ExtractionJobResult>(jobId, "result.json", ct);
        return result is null
            ? Results.NotFound(new { error = "Result blob not found." })
            : Results.Ok(result);
    }
}
