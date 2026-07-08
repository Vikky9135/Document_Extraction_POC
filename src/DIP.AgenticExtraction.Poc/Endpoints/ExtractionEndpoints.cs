namespace DIP.AgenticExtraction.Poc.Endpoints;

public static class ExtractionEndpoints
{
    // TODO (Step 4): implement upload, status, and result endpoints.
    // POST /jobs/upload  — multipart PDF + userPrompt -> 202 Accepted { jobId }
    // GET  /jobs/{id}/status — { jobId, status, error? }
    // GET  /jobs/{id}/result — full ExtractionJobResult JSON
    public static void MapExtractionEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "Healthy", utc = DateTime.UtcNow }));

        // app.MapPost("/jobs/upload", ...);
        // app.MapGet("/jobs/{id}/status", ...);
        // app.MapGet("/jobs/{id}/result", ...);
    }
}
