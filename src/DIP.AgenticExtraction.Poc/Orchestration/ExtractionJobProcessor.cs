using System.Threading.Channels;
using DIP.AgenticExtraction.Poc.Models;

namespace DIP.AgenticExtraction.Poc.Orchestration;

// IHostedService that continuously reads ExtractionJob items from the Channel
// and runs the full Phase 3-9 pipeline for each job.
public class ExtractionJobProcessor : BackgroundService
{
    private readonly ChannelReader<ExtractionJob> _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExtractionJobProcessor> _logger;

    public ExtractionJobProcessor(
        ChannelReader<ExtractionJob> queue,
        IServiceScopeFactory scopeFactory,
        ILogger<ExtractionJobProcessor> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // TODO (Step 14): dequeue jobs and run OCR -> schema gen -> orchestrator pipeline.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.ReadAllAsync(stoppingToken))
        {
            _logger.LogInformation("Dequeued job {JobId} (not yet implemented)", job.JobId);
            // TODO: process job.
        }
    }
}
