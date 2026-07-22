# Agentic Document Extraction — Architecture & Strategy

## 1. Model Assignment

| Phase | Model | Price ($/1M tokens) | Context | Why this model |
|-------|-------|--------------------:|---------|---------------|
| Schema Generation | **gpt-5.4** | $2.50 in / $15 out | 922K | Accuracy-critical — must correctly discover all field definitions from raw OCR |
| Schema Merge | **gpt-5.4-mini** | $0.75 in / $4.50 out | 272K | Structured dedup task — doesn't need full reasoning power |
| Extraction | **gpt-5.4** | $2.50 in / $15 out | 922K | Accuracy-critical — must not miss values or fabricate data |
| Verification | **gpt-5.4** | $2.50 in / $15 out | 922K | Accuracy-critical — must correctly validate extracted values against source |
| Correction | **gpt-5.4-mini** | $0.75 in / $4.50 out | 272K | Re-extracts only failed fields with feedback — simpler task, fewer fields |
| Formatting | **gpt-5-mini** | $0.25 in / $2 out | 272K | Simple pattern transformation (date/number formatting) — no reasoning needed |
| Code Generation | **gpt-5.4** | $2.50 in / $15 out | 922K | Generates Roslyn C# expressions — needs precise, executable code |
| Deduplication | **gpt-5.4-mini** | $0.75 in / $4.50 out | 272K | Compares instances by key field — structured comparison task |

> **Pricing tier:** gpt-5.4 charges $2.50/$15 for requests ≤272K tokens. Requests >272K cost $5/$22.50. We chunk at 200 pages (~200K tokens) to stay in the cheap tier.

---

## 2. Chunking — When & Why

### Why chunk?

LLM models have a maximum input token limit. gpt-5.4 supports 922K input tokens, but the **cheap pricing tier** only applies to requests ≤272K. At ~1,000 tokens per page, 200 pages ≈ 200K tokens — safely under the boundary.

### When does chunking activate?

| Document Size | Schema Generation | Extraction |
|---|---|---|
| **≤ 200 pages** | Single call (no chunking) | Single call (no chunking, no dedup) |
| **> 200 pages** | Split into 200-page chunks | Split into 200-page chunks + 2-page overlap → dedup |

### Chunking parameters

| Setting | Value | How it was calculated |
|---------|-------|----------------------|
| `SchemaChunkPages` | 200 | (272K tier − 5K overhead) × 80% safety ÷ 1,000 tokens/page |
| `ExtractionChunkPages` | 200 | Same calculation |
| `ExtractionOverlapPages` | 2 | Catches entities that span chunk boundaries |

---

## 3. Schema Generation — How It Works

**Goal:** Discover what fields to extract (field definitions, not values).

**Input per chunk:** User's prompt + OCR text of that chunk.

**What each chunk produces:** A partial schema containing only the user-requested fields that are visible in that chunk. If a field isn't found in a chunk, it's simply omitted from that chunk's partial schema.

```
Document 1 (500 pages):
  Chunk 1 (pages 1-200)   → Schema Agent → partial schema A (found: invoiceNumber, subtotal, tax, total)
  Chunk 2 (pages 201-400) → Schema Agent → partial schema B (found: invoiceNumber, subtotal, tax, total)
  Chunk 3 (pages 401-500) → Schema Agent → partial schema C (found: nothing relevant — empty)

Document 2 (50 pages):
  Full document            → Schema Agent → partial schema D (found: invoiceNumber, subtotal, total)

                 ↓ Code-based UNION merge ↓

Merged: { invoiceNumber, subtotal, tax, total }  (union of all fields found in ANY chunk)

                 ↓ LLM dedup + add generation fields + validation rules ↓

Final schema: {
  fields: [invoiceNumber, subtotal, salesTax, total],
  generationFields: [taxRatePercent],
  validationRules: [total == subtotal + salesTax]
}
```

### Key behaviors

| Question | Answer |
|----------|--------|
| Does it extract only user-requested fields? | **Yes.** The system prompt says: "Include ONLY fields the user explicitly asked to extract, or fields needed to compute a generated value." |
| What if a field isn't found in a chunk? | **Omitted from that chunk's partial schema.** Not an error — the field may appear in another chunk. |
| Does it wait for the next chunk? | **No.** Each chunk is processed independently and in parallel. |
| What if a field isn't found in ANY chunk? | It won't be in the final schema. You can't extract what doesn't exist in the documents. |
| Is overlap needed for schema? | **No.** Field definitions (labels/headers) don't span pages. |

---

## 4. Extraction — How It Works

**Goal:** Find actual field VALUES for every entity instance in the document.

**Input per chunk:** The final schema (same for all chunks) + OCR text of that chunk.

**What each chunk produces:** All entity instances found in those pages, with all field values.

```
Final schema: { invoiceNumber, subtotal, salesTax, total, taxRatePercent(gen) }

Chunk 1 (pages 1-200):
  Finds 40 invoices → [INV-001, INV-002, ..., INV-040]
  Each with: { invoiceNumber: "INV-001", subtotal: 500, salesTax: 50, total: 550 }

Chunk 2 (pages 199-398, overlap=2):
  Finds 41 invoices → [INV-040, INV-041, ..., INV-080]
  INV-040 is a DUPLICATE (from overlap pages 199-200)

Chunk 3 (pages 397-500, overlap=2):
  Finds 21 invoices → [INV-080, INV-081, ..., INV-100]
  INV-080 is a DUPLICATE (from overlap pages 397-398)

               ↓ Combine all chunks ↓

Before dedup: 102 instances (40 + 41 + 21, includes 2 duplicates)

               ↓ Deduplication by key field (invoiceNumber) ↓

After dedup: 100 unique instances ✓
```

### Key behaviors

| Question | Answer |
|----------|--------|
| Does each chunk get the full schema? | **Yes.** Every chunk receives the identical final schema — all fields to look for. |
| What if a field value isn't in a chunk? | Returns `null` for that field in that instance. The entity might be split across chunks. |
| Why overlap for extraction? | An entity (invoice) can start on page 200 (end of chunk 1) and continue on page 201 (start of chunk 2). Overlap ensures both chunks see the complete entity. |
| How are duplicates resolved? | Group by key field value → keep higher confidence instance, or merge partial instances. |

---

## 5. Deduplication — The Overlap Problem

### Why duplicates happen

```
Page 199-200: Invoice INV-040 (header + line items)

Chunk 1 includes pages 1-200   → extracts INV-040 ✓
Chunk 2 includes pages 199-398 → extracts INV-040 AGAIN (from overlap) ✓

Result: INV-040 appears twice
```

### How dedup works

1. **Identify key field:** First field with role `Extract` (e.g., `invoiceNumber`)
2. **Group instances by key value:** All instances with `invoiceNumber = "INV-040"` are grouped
3. **Resolve each group:**

| Scenario | Resolution |
|----------|-----------|
| 1 instance (no duplicate) | Keep as-is |
| 2 instances, same values | Keep the one with higher average confidence |
| 2 instances, one has null fields | Merge — fill nulls from the other instance |

### The split-entity edge case

```
WITHOUT overlap:
  Chunk 1 (pages 1-200): Sees "Invoice #: INV-040, Subtotal: $500" on page 200
                          → extracts { invoiceNumber: "INV-040", subtotal: 500, total: null }
  Chunk 2 (pages 201-398): Sees "Tax: $50, Total: $550" on page 201
                            → might not extract at all (no invoice header)

  PROBLEM: Split entity, partial data, possible data loss

WITH 2-page overlap:
  Chunk 1 (pages 1-200): Same as above
  Chunk 2 (pages 199-398): Sees pages 199-200 (header) + page 201 (tax/total)
                            → extracts COMPLETE { invoiceNumber: "INV-040", subtotal: 500, salesTax: 50, total: 550 }

  Dedup merges: chunk 1's partial + chunk 2's complete → final complete instance ✓
```

---

## 6. Pipeline Summary

```
┌─────────────────────────────────────────────────────────┐
│  UPLOAD: documents + classification name → OCR          │
└─────────────────────────────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────┐
│  SCHEMA (gpt-5.4, no overlap)                           │
│  Each doc → chunk at 200pg → partial schema per chunk   │
│  All partials → UNION merge → LLM dedup → final schema │
│                                                         │
│  Output: field definitions only (not values)            │
└─────────────────────────────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────┐
│  EXTRACTION (gpt-5.4, with 2-page overlap)              │
│  Each doc → chunk at 200pg + 2 overlap                  │
│  Each chunk: extract → verify → correct → format → gen  │
│  All chunk instances → dedup by key field               │
│                                                         │
│  Output: list of entity instances with field values     │
└─────────────────────────────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────┐
│  RESPONSE: per-document instances with requested fields │
└─────────────────────────────────────────────────────────┘
```

---

## 7. Cost Estimate (5 documents × 500 pages)

| Phase | LLM Calls | Cost |
|-------|-----------|------|
| Schema Generation (3 chunks × 5 docs) | 15 | $8.63 |
| Schema Merge | 1 | $0.07 |
| Extraction (3 chunks × 5 docs) | 15 | $18.75 |
| Verification (3 chunks × 5 docs) | 15 | $9.75 |
| Correction (~30% fields fail) | ~5 | $2.00 |
| Deduplication (1 per doc) | 5 | $0.50 |
| Formatting (1 per doc) | 5 | $0.02 |
| Code Generation | 1 | $0.06 |
| **Total** | **~62** | **~$40** |

---

## 8. Configuration

```json
{
  "AgenticExtraction": {
    "Gpt54DeploymentName": "gpt-5.4",
    "Gpt54MiniDeploymentName": "gpt-5.4-mini",
    "SchemaChunkPages": 200,
    "ExtractionChunkPages": 200,
    "ExtractionOverlapPages": 2
  }
}
```

---

## 9. Storage Structure

```
<classification_name>/
  final-schema.json              ← merged schema (field definitions)
  generation-scripts.json        ← C# expressions for computed fields
  document1.pdf                  ← original uploaded PDF
  document1.ocr.json             ← OCR output (structured text + word/line data)
  document1.extraction.json      ← all extracted instances for this document
  document2.pdf
  document2.ocr.json
  document2.extraction.json
```

---

## 10. API Endpoints

| # | Endpoint | Method | Input | Output |
|---|----------|--------|-------|--------|
| 1 | `/documents/upload` | POST (multipart) | `classificationName` + PDF files | Per-doc OCR metadata |
| 2 | `/extraction/run` | POST (JSON) | `classificationName`, `extractionGoal`, `describeWhatToExtract` | Schema + instances per document |
