using System.Threading.Channels;
using Azure.AI.DocumentIntelligence;
using DIP.AgenticExtraction.Poc.Agents;
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

// Serilog
builder.Host.UseSerilog((ctx, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

// Options
builder.Services.Configure<AgenticExtractionOptions>(
    builder.Configuration.GetSection(AgenticExtractionOptions.Section));

// Infrastructure
builder.Services.AddMemoryCache();

// Job Queue (Channel<T> - bounded, async)
var jobChannel = Channel.CreateBounded<ExtractionJob>(new BoundedChannelOptions(100)
{
    FullMode = BoundedChannelFullMode.Wait
});
builder.Services.AddSingleton(jobChannel.Reader);
builder.Services.AddSingleton(jobChannel.Writer);

builder.Services.AddHttpClient("default").AddStandardResilienceHandler();

// Step 3 - Storage and Job Store
builder.Services.AddSingleton<IJobStore, JobStore>();
builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();

// Step 5 - Azure Document Intelligence + OCR
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
    Log.Warning("DocumentIntelligenceEndpoint/Key not configured - OCR will not run.");
}

// Step 6 - Azure OpenAI + Schema Generation + Agents
if (!string.IsNullOrWhiteSpace(extractionOpts.AzureOpenAIEndpoint)
    && !string.IsNullOrWhiteSpace(extractionOpts.AzureOpenAIKey))
{
    builder.Services.AddSingleton<IAzureOpenAIClientFactory, AzureOpenAIClientFactory>();

    // Phase 4 + 9 - GPT-5 (schema gen + code gen)
    builder.Services.AddSingleton<ISchemaGenerationService>(sp =>
        new SchemaGenerationService(
            sp.GetRequiredService<IAzureOpenAIClientFactory>().CreateGpt5Client(),
            sp.GetRequiredService<ILogger<SchemaGenerationService>>()));

    // Phase 5-7 - O3 (reasoning)
    builder.Services.AddSingleton<IExtractionAgent>(sp =>
        new ExtractionAgent(sp.GetRequiredService<IAzureOpenAIClientFactory>().CreateO3Client()));
    builder.Services.AddSingleton<IVerificationAgent>(sp =>
        new VerificationAgent(sp.GetRequiredService<IAzureOpenAIClientFactory>().CreateO3Client()));

    // Phase 8 - gpt-5-mini (formatting)
    builder.Services.AddSingleton<IFormatterAgent>(sp =>
        new FormatterAgent(sp.GetRequiredService<IAzureOpenAIClientFactory>().CreateGpt5MiniClient()));

    // Phase 9 - GPT-5 code gen + Roslyn execution
    builder.Services.AddSingleton<IGenerationAgent>(sp =>
        new GenerationAgent(sp.GetRequiredService<IAzureOpenAIClientFactory>().CreateGpt5Client()));

    // Phases 5-9 orchestrator
    builder.Services.AddSingleton<IAgenticExtractionOrchestrator>(sp =>
        new AgenticExtractionOrchestrator(
            sp.GetRequiredService<IExtractionAgent>(),
            sp.GetRequiredService<IVerificationAgent>(),
            sp.GetRequiredService<IFormatterAgent>(),
            sp.GetRequiredService<IGenerationAgent>(),
            sp.GetRequiredService<IAzureOpenAIClientFactory>().CreateGpt5MiniClient(),
            sp.GetRequiredService<ILogger<AgenticExtractionOrchestrator>>()));
}
else
{
    Log.Warning("AzureOpenAIEndpoint/Key not configured - Schema generation and extraction will not run.");
}

// Step 14 - Background worker
builder.Services.AddHostedService<ExtractionJobProcessor>();

// OpenAPI (Scalar UI)
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();                   // /openapi/v1.json
    app.MapScalarApiReference();        // /scalar/v1  ← open this in browser
}

app.UseHttpsRedirection();
app.UseSerilogRequestLogging();

app.MapExtractionEndpoints();

app.Run();
