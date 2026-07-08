using System.Threading.Channels;
using DIP.AgenticExtraction.Poc.Endpoints;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Serilog ──────────────────────────────────────────────────────────────────
builder.Host.UseSerilog((ctx, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

// ── Options ──────────────────────────────────────────────────────────────────
builder.Services.Configure<AgenticExtractionOptions>(
    builder.Configuration.GetSection(AgenticExtractionOptions.Section));

// ── Infrastructure ───────────────────────────────────────────────────────────
builder.Services.AddMemoryCache();

// ── Job Queue (Channel<T> — bounded, async) ──────────────────────────────────
var jobChannel = Channel.CreateBounded<ExtractionJob>(new BoundedChannelOptions(100)
{
    FullMode = BoundedChannelFullMode.Wait
});
builder.Services.AddSingleton(jobChannel.Reader);
builder.Services.AddSingleton(jobChannel.Writer);

builder.Services.AddHttpClient("default").AddStandardResilienceHandler();

// TODO: register services, agents, orchestrator, and hosted worker as they are implemented.
// See POC_IMPLEMENTATION_GUIDE.md Step 16 for the full wiring, e.g.:
//   builder.Services.AddSingleton<IJobStore, JobStore>();
//   builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();
//   builder.Services.AddSingleton<IAzureOpenAIClientFactory, AzureOpenAIClientFactory>();
//   builder.Services.AddSingleton(new DocumentIntelligenceClient(...));
//   builder.Services.AddScoped<IOcrPreprocessingService, OcrPreprocessingService>();
//   builder.Services.AddScoped<ISchemaGenerationService, SchemaGenerationService>();
//   builder.Services.AddScoped<IExtractionAgent, ExtractionAgent>();
//   builder.Services.AddScoped<IVerificationAgent, VerificationAgent>();
//   builder.Services.AddScoped<IFormatterAgent, FormatterAgent>();
//   builder.Services.AddScoped<IGenerationAgent, GenerationAgent>();
//   builder.Services.AddScoped<IAgenticExtractionOrchestrator, AgenticExtractionOrchestrator>();
//   builder.Services.AddHostedService<ExtractionJobProcessor>();

var app = builder.Build();

app.UseHttpsRedirection();
app.UseSerilogRequestLogging();

app.MapExtractionEndpoints();

app.Run();

