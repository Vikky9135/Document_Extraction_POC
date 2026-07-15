using System.Threading.Channels;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Ocr;
using DIP.AgenticExtraction.Poc.Schema;
using DIP.AgenticExtraction.Poc.Services;

namespace DIP.AgenticExtraction.Poc.Orchestration;

public class ExtractionJobProcessor : BackgroundService
{
    private readonly ChannelReader<ExtractionJob> _queue;
    private readonly IBlobStorageService _blobStorage;
    private readonly IJobStore _jobStore;
    private readonly IOcrPreprocessingService? _ocrService;
    private readonly ISchemaGenerationService? _schemaService;
    private readonly IAgenticExtractionOrchestrator? _orchestrator;
    private readonly ILogger<ExtractionJobProcessor> _logger;

    public ExtractionJobProcessor(
        ChannelReader<ExtractionJob> queue,
        IBlobStorageService blobStorage,
        IJobStore jobStore,
        IServiceProvider services,
        ILogger<ExtractionJobProcessor> logger)
    {
        _queue         = queue;
        _blobStorage   = blobStorage;
        _jobStore      = jobStore;
        _ocrService    = services.GetService<IOcrPreprocessingService>();
        _schemaService = services.GetService<ISchemaGenerationService>();
        _orchestrator  = services.GetService<IAgenticExtractionOrchestrator>();
        _logger        = logger;
    }

    // Step 14 — dequeue jobs and run OCR -> schema gen -> agentic pipeline (Phases 5-9).
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
        if (_ocrService is null)
        {
            _logger.LogWarning("[Job {JobId}] Skipping OCR — DocumentIntelligence not configured.", job.JobId);
            _jobStore.Set(job.JobId, JobStatus.Failed, "OCR service not configured.");
            return;
        }

        _logger.LogInformation("[Job {JobId}] Phase 3: Downloading PDF...", job.JobId);
        await using var pdfStream = await _blobStorage.DownloadPdfAsync(job.JobId, ct);

        _logger.LogInformation("[Job {JobId}] Phase 3: Running OCR...", job.JobId);
        var ocrContext = await _ocrService.PrepareAsync(pdfStream, ct);

        await _blobStorage.SaveJsonAsync(job.JobId, "ocr-context.json", new
        {
            pageCount      = ocrContext.PageCount,
            structuredText = ocrContext.StructuredText
        }, ct);

        _logger.LogInformation(
            "[Job {JobId}] Phase 3 complete. Pages={Pages}, TextLength={Len}",
            job.JobId, ocrContext.PageCount, ocrContext.StructuredText.Length);

        // ── Phase 4 — Schema Generation ───────────────────────────────────────
        if (_schemaService is null)
        {
            _logger.LogWarning("[Job {JobId}] Skipping schema gen — AzureOpenAI not configured.", job.JobId);
            _jobStore.Set(job.JobId, JobStatus.Failed, "Schema generation service not configured.");
            return;
        }

        _logger.LogInformation("[Job {JobId}] Phase 4: Generating schema...", job.JobId);
        var schema = await _schemaService.GenerateFromSamplesAsync(
            ocrContext.StructuredText, job.UserPrompt, ct);

        await _blobStorage.SaveJsonAsync(job.JobId, "schema.json", schema, ct);

        _logger.LogInformation(
            "[Job {JobId}] Phase 4 complete. Fields={F}, Tables={T}, Generated={G}. " +
            "Saved to blob: jobs/{JobId}/schema.json",
            job.JobId, schema.Fields.Count, schema.TableFields.Count,
            schema.GenerationFields.Count, job.JobId);

        // ── Phases 5-9 — Agentic Extraction Pipeline ─────────────────────────
        if (_orchestrator is null)
        {
            _logger.LogWarning("[Job {JobId}] Skipping extraction — orchestrator not configured.", job.JobId);
            _jobStore.Set(job.JobId, JobStatus.Failed, "Extraction orchestrator not configured.");
            return;
        }

        _logger.LogInformation(
            "[Job {JobId}] Phases 5-9: Extract -> Verify -> Correct -> Format -> Generate...",
            job.JobId);

        var result = await _orchestrator.RunAsync(
            job.JobId, schema, ocrContext.StructuredText, job.UserPrompt, ocrContext.PageCount, ct);

        var finalResult = result with
        {
            Metadata = result.Metadata with { SchemaWasAutoGenerated = true }
        };

        await _blobStorage.SaveJsonAsync(job.JobId, "result.json", finalResult, ct);
        _jobStore.Set(job.JobId, JobStatus.Completed);

        _logger.LogInformation(
            "[Job {JobId}] Completed. LLM calls={Calls}, Iterations={Iter}, Time={Ms}ms",
            job.JobId, finalResult.Metadata.LlmCallCount,
            finalResult.Metadata.CorrectionIterations, finalResult.Metadata.ProcessingTimeMs);
    }
}
