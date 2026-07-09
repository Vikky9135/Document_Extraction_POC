using System.Threading.Channels;
using Azure.AI.DocumentIntelligence;
using DIP.AgenticExtraction.Poc.Endpoints;
using DIP.AgenticExtraction.Poc.Models;
using DIP.AgenticExtraction.Poc.Ocr;
using DIP.AgenticExtraction.Poc.Options;
using DIP.AgenticExtraction.Poc.Orchestration;
using DIP.AgenticExtraction.Poc.Schema;
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

// ── Step 6 — Azure OpenAI + Schema Generation ──────────────────────────────
if (!string.IsNullOrWhiteSpace(extractionOpts.AzureOpenAIEndpoint)
    && !string.IsNullOrWhiteSpace(extractionOpts.AzureOpenAIKey))
{
    builder.Services.AddSingleton<IAzureOpenAIClientFactory, AzureOpenAIClientFactory>();
    // GPT-5 client for Phase 4 (schema gen) and Phase 9 (code gen)
    builder.Services.AddSingleton(sp =>
        sp.GetRequiredService<IAzureOpenAIClientFactory>().CreateGpt5Client());
    builder.Services.AddSingleton<ISchemaGenerationService, SchemaGenerationService>();
}
else
{
    Log.Warning("AzureOpenAIEndpoint/Key not configured — Schema generation will not run.");
}

// ── Step 14 — Background worker ──────────────────────────────────────────────
builder.Services.AddHostedService<ExtractionJobProcessor>();

// ── OpenAPI (Scalar UI) ─────────────────────────────────────────────────────
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseHttpsRedirection();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapExtractionEndpoints();

app.Run();