using System.Threading.Channels;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Ocr;
using DIP.AgenticExtraction.Poc.Services;

namespace DIP.AgenticExtraction.Poc.Orchestration;

// BackgroundService that dequeues ExtractionJob items and runs the pipeline.
// Each step is added here as it is implemented.
public class ExtractionJobProcessor : BackgroundService
{
    private readonly ChannelReader<ExtractionJob> _queue;
    private readonly IBlobStorageService _blobStorage;
    private readonly IJobStore _jobStore;
    private readonly IOcrPreprocessingService _ocrService;
    private readonly ILogger<ExtractionJobProcessor> _logger;

    public ExtractionJobProcessor(
        ChannelReader<ExtractionJob> queue,
        IBlobStorageService blobStorage,
        IJobStore jobStore,
        IOcrPreprocessingService ocrService,
        ILogger<ExtractionJobProcessor> logger)
    {
        _queue       = queue;
        _blobStorage = blobStorage;
        _jobStore    = jobStore;
        _ocrService  = ocrService;
        _logger      = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.ReadAllAsync(stoppingToken))
        {
            _logger.LogInformation("[Job {JobId}] Dequeued. UserPrompt: {Prompt}",
                job.JobId, job.UserPrompt);

            _jobStore.Set(job.JobId, JobStatus.Processing);

            try
            {
                await ProcessJobAsync(job, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Job {JobId}] Failed", job.JobId);
                _jobStore.Set(job.JobId, JobStatus.Failed, ex.Message);
            }
        }
    }

    private async Task ProcessJobAsync(ExtractionJob job, CancellationToken ct)
    {
        // ── Phase 3 — OCR ────────────────────────────────────────────────────
        _logger.LogInformation("[Job {JobId}] Phase 3: Downloading PDF from blob...", job.JobId);
        await using var pdfStream = await _blobStorage.DownloadPdfAsync(job.JobId, ct);

        _logger.LogInformation("[Job {JobId}] Phase 3: Running OCR...", job.JobId);
        var ocrContext = await _ocrService.PrepareAsync(pdfStream, ct);

        // Persist OCR output to blob so it can be inspected / reused
        await _blobStorage.SaveJsonAsync(job.JobId, "ocr-context.json", new
        {
            pageCount      = ocrContext.PageCount,
            structuredText = ocrContext.StructuredText
        }, ct);

        _logger.LogInformation(
            "[Job {JobId}] Phase 3 complete. Pages={Pages}, TextLength={Len}. " +
            "Saved to blob: jobs/{JobId}/ocr-context.json",
            job.JobId, ocrContext.PageCount, ocrContext.StructuredText.Length, job.JobId);

        // ── Phase 4-9 — TODO (next steps) ────────────────────────────────────
        // TODO (Step 6):  var schema = await _schemaService.GenerateFromSamplesAsync(...);
        // TODO (Step 10): var result = await _orchestrator.RunAsync(...);
        // TODO:           await _blobStorage.SaveJsonAsync(job.JobId, "result.json", result, ct);
        // TODO:           _jobStore.Set(job.JobId, JobStatus.Completed);

        _logger.LogInformation(
            "[Job {JobId}] OCR done. Pipeline paused — Phases 4-9 not yet implemented.",
            job.JobId);
    }
}
