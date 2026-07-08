# POC Infrastructure Setup — What You Need on Azure

> Just the resources and configuration. No CLI commands. Provision these through the Azure Portal and you are ready to run the POC locally.

---

## The 3 Resources You Need

```
┌─────────────────────────────────────────────────────────────────┐
│                    One Azure Resource Group                     │
│                    e.g. rg-agentic-poc                          │
│                                                                 │
│  ┌───────────────────────┐  ┌──────────────────────────────┐   │
│  │   Azure OpenAI        │  │  Azure Document Intelligence │   │
│  │                       │  │                              │   │
│  │  3 model deployments  │  │  No setup needed —           │   │
│  │  o3, o4-mini, gpt-5   │  │  prebuilt-layout is          │   │
│  │  (see below)          │  │  built into every resource   │   │
│  └───────────────────────┘  └──────────────────────────────┘   │
│                                                                 │
│  ┌───────────────────────┐                                      │
│  │  Azure Storage        │                                      │
│  │  Account              │                                      │
│  │                       │                                      │
│  │  1 blob container     │                                      │
│  └───────────────────────┘                                      │
└─────────────────────────────────────────────────────────────────┘
```

---

## Resource 1 — Azure OpenAI

**Create an Azure OpenAI resource** in the Azure Portal (via **Azure AI Foundry** at [ai.azure.com](https://ai.azure.com) — this is the new unified portal for all Azure AI resources).

**Region:** `East US 2` or `Sweden Central` — these have the widest model availability as of mid-2026. Use the same region for all three resources. Check: [Azure OpenAI model availability](https://learn.microsoft.com/azure/ai-services/openai/concepts/models)

**Inside the resource, deploy 3 models — each assigned to specific pipeline phases:**

---

### Deployment 1 — o3 (OpenAI's latest full reasoning model)

| Setting | Value |
|---------|-------|
| Deployment name | `o3` |
| Model | **o3** |
| Model version | Latest available |
| Deployment type | Global Standard |
| Tokens per minute | 50K |

**Used for Phases 5, 6, 7 — Extraction, Verification, Correction.**

o3 is a *reasoning model* — a fundamentally different architecture from GPT. Before producing output, it thinks step-by-step internally. This is what makes it catch extraction errors that GPT-series models miss, and why it can follow complex verifier feedback to correct a previous wrong answer.

- **Phase 5 — Extraction:** Needs to reason about ambiguous layouts — is "4523" on page 2 the total, or a line item subtotal? o3 works through it.
- **Phase 6 — Verification:** Needs to critically re-read the document and dispute the extractor. A pure text-generation model tends to agree with the value it already saw. o3's reasoning produces genuinely independent verdicts.
- **Phase 7 — Correction:** Needs to apply specific feedback ("page 2 row 3 shows 4523, not 45230") and extract the correct value without repeating the original mistake.

---

### Deployment 2 — o4-mini (OpenAI's latest small reasoning model)

| Setting | Value |
|---------|-------|
| Deployment name | `o4-mini` |
| Model | **o4-mini** |
| Model version | Latest available |
| Deployment type | Global Standard |
| Tokens per minute | 100K |

**Used for Phase 8 — Formatting only.**

Phase 8 converts raw values to required formats (e.g. `January 5 2024` → `2024-01-05`, `$45,230` → `45230.00`). This is straightforward instruction-following — it does not require deep document reasoning. o4-mini is the newest small reasoning model, approximately 10× cheaper and 3× faster than o3 with identical accuracy for simple formatting tasks.

---

### Deployment 3 — GPT-5 (OpenAI's latest GPT model)

| Setting | Value |
|---------|-------|
| Deployment name | `gpt-5` |
| Model | **GPT-5** |
| Model version | Latest available |
| Deployment type | Global Standard |
| Tokens per minute | 50K |

**Used for Phases 4 and 9 — Schema Generation and Code Generation.**

GPT-5 (released May 2025) is OpenAI's most capable general-purpose model. It is not a reasoning model — it is optimized for document understanding, structured JSON generation, and code writing, which is exactly what Phases 4 and 9 need.

- **Phase 4 — Schema Generation:** GPT-5 reads the full OCR text of the document and the user's `userPrompt`, then produces a complete `ExtractionSchema` — field names, types, synonyms, output formats, and any derived/computed field instructions. GPT-5's native multimodal capability also means you can pass page images alongside OCR text for documents with complex visual layouts.
- **Phase 9 — Generation Agent:** Writes a single C# expression to compute derived fields (e.g. `totalAmount * 0.15` for tax). GPT-5 follows the strict "expression only, no method wrapper" instruction reliably.

---

**What to note down from this resource:**
- Endpoint URL — looks like `https://your-resource-name.openai.azure.com/`
- API Key — under **Keys and Endpoint** in the Azure Portal (or use managed identity — no key needed)

---

## Resource 2 — Azure Document Intelligence

**Create an Azure AI Document Intelligence resource** (formerly Form Recognizer) in the Azure Portal.

| Setting | Value |
|---------|-------|
| Pricing tier | S0 (Standard) |
| Region | Same region as your OpenAI resource |

**No model training required.** The POC uses `prebuilt-layout` which is built into every Document Intelligence resource out of the box. It handles:
- Page segmentation
- Paragraph detection with roles (title, heading, etc.)
- Table extraction (rows, columns, headers)
- Word-level bounding boxes
- Selection marks (checkboxes, radio buttons)

**What to note down from this resource:**
- Endpoint URL (looks like `https://your-resource-name.cognitiveservices.azure.com/`)
- API Key (under Keys and Endpoint in the Portal)

---

## Resource 3 — Azure Storage Account

**Create an Azure Storage Account** in the Azure Portal.

| Setting | Value |
|---------|-------|
| Performance | Standard |
| Redundancy | LRS (Locally Redundant — fine for POC) |
| Region | Same region as everything else |

**Inside the storage account, create one blob container:**

| Setting | Value |
|---------|-------|
| Container name | `agentic-poc-jobs` |
| Public access level | Private (no anonymous access) |

> The POC uses this container to store uploaded PDFs (`jobs/{jobId}/source.pdf`), the generated schema (`jobs/{jobId}/schema.json`), and the final result (`jobs/{jobId}/result.json`).

**What to note down from this resource:**
- Connection string (under Access Keys in the Portal — copy Connection string for key1)

---

## Local Machine

On your development machine you need:

| Tool | Minimum Version | Why |
|------|----------------|-----|
| .NET SDK | 8.0 | Runs the POC |
| Any REST client | — | Postman, Bruno, or curl to test the API endpoints |

---

## Configuration

Once you have the three resources provisioned, create `appsettings.Development.json` in the POC project root. **Do not commit this file to git** — it contains secrets.

```json
{
  "AgenticExtraction": {
    "O3DeploymentName":             "o3",
    "O4MiniDeploymentName":         "o4-mini",
    "Gpt5DeploymentName":           "gpt-5",
    "O3ReasoningEffort":            "high",
    "AzureOpenAIEndpoint":          "https://<your-openai-resource>.openai.azure.com/",
    "AzureOpenAIKey":               "<key from Azure Portal>",
    "DocumentIntelligenceEndpoint": "https://<your-di-resource>.cognitiveservices.azure.com/",
    "DocumentIntelligenceKey":      "<key from Azure Portal>",
    "BlobConnectionString":         "<connection string from Azure Portal>",
    "BlobContainerName":            "agentic-poc-jobs",
    "MaxFileSizeMb":                50,
    "MaxOcrPagesPerChunk":          100
  }
}
```

---

## Estimated Monthly Cost at POC Scale

| Resource | What drives cost | Estimate for 1 month of testing |
|----------|-----------------|--------------------------------|
| Azure OpenAI — o3 | Tokens per LLM call (input + output) — Phases 5,6,7 | ~$10–20 for ~50 test runs |
| Azure OpenAI — o4-mini | Tokens per format call — Phase 8 | ~$0.50–1 for ~50 test runs |
| Azure OpenAI — GPT-5 | Tokens per schema gen + code gen call — Phases 4,9 | ~$3–6 for ~50 test runs |
| Document Intelligence | Per page analyzed | ~$0.001/page — essentially free at POC scale |
| Blob Storage | GB stored + operations | < $1 |
| **Total** | | **< $30/month** |

---

## What You Do NOT Need

These are common Azure services that are **not required** for the POC:

| Service | Why not needed |
|---------|---------------|
| Azure SQL / Cosmos DB | POC uses in-memory cache + Blob JSON for job state |
| Azure Service Bus | No messaging in POC — result is written to Blob and polled via REST |
| Azure Container Apps / App Service | POC runs on your local machine |
| Azure Key Vault | Not needed for local dev — keys go in `appsettings.Development.json` |
| Azure Event Grid | DIP Core uses this; POC has a direct upload endpoint instead |
| Azure AD App Registration | Not needed for POC — API key auth is sufficient |
| Custom ADI classifier model | POC does not do classification — that is DIP Core's job |

---

## Verify Everything Is Working

Once running locally (`dotnet run`), hit these three checks in order:

1. `GET /health` — should return `{ "status": "Healthy" }` immediately. If this fails, the app did not start correctly.

2. `POST /jobs/upload` with any small PDF and a `userPrompt` — should return `202 Accepted` with a `jobId`. If this fails, check your Blob Storage connection string.

3. `GET /jobs/{jobId}/status` — watch it go `Queued → Processing → Completed`. If it stays on `Processing` for more than 2 minutes, check the console logs — they will show exactly which service call failed (ADI, OpenAI, or Blob).

---

## Teardown

When you are done with the POC, delete the entire resource group from the Azure Portal. This removes all three resources in one action and stops all billing.
