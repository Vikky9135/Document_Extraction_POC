using Azure.AI.DocumentIntelligence;
using DIP.AgenticExtraction.Poc.Agents;
using DIP.AgenticExtraction.Poc.Endpoints;
using DIP.AgenticExtraction.Poc.Ocr;
using DIP.AgenticExtraction.Poc.Options;
using DIP.AgenticExtraction.Poc.Orchestration;
using DIP.AgenticExtraction.Poc.Schema;
using DIP.AgenticExtraction.Poc.Services;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog — logs to terminal console
builder.Host.UseSerilog((ctx, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

// Options
builder.Services.Configure<AgenticExtractionOptions>(
    builder.Configuration.GetSection(AgenticExtractionOptions.Section));

// Infrastructure
builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();

// Azure Document Intelligence + OCR
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

// Azure OpenAI + Schema Generation + Agents
if (!string.IsNullOrWhiteSpace(extractionOpts.AzureOpenAIEndpoint)
    && !string.IsNullOrWhiteSpace(extractionOpts.AzureOpenAIKey))
{
    builder.Services.AddSingleton<IAzureOpenAIClientFactory, AzureOpenAIClientFactory>();

    // Schema generation (GPT-5)
    builder.Services.AddSingleton<ISchemaGenerationService>(sp =>
        new SchemaGenerationService(
            sp.GetRequiredService<IAzureOpenAIClientFactory>().CreateGpt5Client(),
            sp.GetRequiredService<ILogger<SchemaGenerationService>>()));

    // Extraction + Verification (O3)
    builder.Services.AddSingleton<IExtractionAgent>(sp =>
        new ExtractionAgent(sp.GetRequiredService<IAzureOpenAIClientFactory>().CreateO3Client()));
    builder.Services.AddSingleton<IVerificationAgent>(sp =>
        new VerificationAgent(sp.GetRequiredService<IAzureOpenAIClientFactory>().CreateO3Client()));

    // Formatting (GPT-5-mini)
    builder.Services.AddSingleton<IFormatterAgent>(sp =>
        new FormatterAgent(sp.GetRequiredService<IAzureOpenAIClientFactory>().CreateGpt5MiniClient()));

    // Generation (GPT-5 code gen + Roslyn)
    builder.Services.AddSingleton<IGenerationAgent>(sp =>
        new GenerationAgent(sp.GetRequiredService<IAzureOpenAIClientFactory>().CreateGpt5Client()));

    // Orchestrator (Phases 5-9)
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

// OpenAPI (Scalar UI)
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();                   // /openapi/v1.json
    app.MapScalarApiReference();        // /scalar/v1
}

app.UseHttpsRedirection();
app.UseSerilogRequestLogging();

app.MapExtractionEndpoints();

app.Run();
