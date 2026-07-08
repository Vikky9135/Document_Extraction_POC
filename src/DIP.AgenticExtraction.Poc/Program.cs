using System.Threading.Channels;
using Azure.AI.DocumentIntelligence;
using DIP.AgenticExtraction.Poc.Endpoints;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Ocr;
using DIP.AgenticExtraction.Poc.Options;
using DIP.AgenticExtraction.Poc.Orchestration;
using DIP.AgenticExtraction.Poc.Services;
using Scalar.AspNetCore;
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

// ── Step 3 — Storage & Job Store ──────────────────────────────────────────────
builder.Services.AddSingleton<IJobStore, JobStore>();
builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();

// ── Step 5 — Azure Document Intelligence + OCR ────────────────────────────────
var extractionOpts = builder.Configuration
    .GetSection(AgenticExtractionOptions.Section)
    .Get<AgenticExtractionOptions>() ?? new AgenticExtractionOptions();

if (!string.IsNullOrWhiteSpace(extractionOpts.DocumentIntelligenceEndpoint)
    && !string.IsNullOrWhiteSpace(extractionOpts.DocumentIntelligenceKey))
{
    builder.Services.AddSingleton(new DocumentIntelligenceClient(
        new Uri(extractionOpts.DocumentIntelligenceEndpoint),
        new Azure.AzureKeyCredential(extractionOpts.DocumentIntelligenceKey)));
    builder.Services.AddSingleton<IOcrPreprocessingService, OcrPreprocessingService>();
}
else
{
    Log.Warning("DocumentIntelligenceEndpoint/Key not configured — OCR will not run.");
}

// ── Step 14 — Background worker ──────────────────────────────────────────────
builder.Services.AddHostedService<ExtractionJobProcessor>();

// TODO (Steps 6-13): uncomment as each component is implemented:
//   builder.Services.AddSingleton<IAzureOpenAIClientFactory, AzureOpenAIClientFactory>();
//   builder.Services.AddScoped<ISchemaGenerationService, SchemaGenerationService>();
//   builder.Services.AddScoped<IExtractionAgent, ExtractionAgent>();
//   builder.Services.AddScoped<IVerificationAgent, VerificationAgent>();
//   builder.Services.AddScoped<IFormatterAgent, FormatterAgent>();
//   builder.Services.AddScoped<IGenerationAgent, GenerationAgent>();
//   builder.Services.AddScoped<IAgenticExtractionOrchestrator, AgenticExtractionOrchestrator>();

// ── OpenAPI (Scalar UI) ─────────────────────────────────────────────────────
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();            // /openapi/v1.json
    app.MapScalarApiReference(); // /scalar/v1
}

app.UseHttpsRedirection();
app.UseSerilogRequestLogging();

app.MapExtractionEndpoints();

app.Run();

