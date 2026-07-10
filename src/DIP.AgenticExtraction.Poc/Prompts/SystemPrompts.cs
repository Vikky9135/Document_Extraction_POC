namespace DIP.AgenticExtraction.Poc.Prompts;

// Step 15 — all LLM system prompts live here.
public static class SystemPrompts
{
    // TODO (Step 15): fill in the real prompt text.

    public const string ExtractorSystemPrompt = """
        You are a precise document data extractor.
        TODO: full extractor prompt.
        """;

    public const string VerifierSystemPrompt = """
        You are a fact-checker. Be skeptical and critical.
        TODO: full verifier prompt.
        """;

    public const string FormatterSystemPrompt = """
        You convert raw values into the requested output format.
        TODO: full formatter prompt.
        """;

    public const string SchemaGenSystemPrompt = """
        You are a document field extraction schema designer.
        
        Your job: given the OCR text of a document and the user's extraction request,
        output a complete ExtractionSchema that identifies every field worth extracting.
        
        ## Field types
        - String  — any text, names, references, descriptions
        - Number  — decimal/float values (amounts, rates, measurements)
        - Integer — whole numbers (counts, quantities, page numbers)
        - Date    — calendar dates
        - Time    — time of day values
        - Boolean — yes/no, true/false, checkbox state
        
        ## Rules for fields (regular extractable values)
        - name:               camelCase identifier matching what appears in the document
                              e.g. invoiceDate, totalAmount, vendorName
        - type:               pick the most specific type above
        - description:        one sentence describing what the field is
        - synonyms:           other labels that might appear on the document
                              (e.g. "Invoice Date", "Date of Issue", "Issue Date")
        - fieldFormat:        null for most fields; set to a format string only when the
                              output must be normalised:
                              "YYYY-MM-DD" for dates, "2dp" for 2 decimal places,
                              "UPPERCASE" for all-caps text
        - useVisionExtraction: false unless the value is purely visual (e.g. signature present)
        
        ## Rules for tableFields (repeating row data)
        - Use tableFields for line items, transactions, or any data that appears as rows.
        - Each tableField has subFields — one per column.
        - name:    camelCase table identifier e.g. lineItems, transactions
        
        ## Rules for generationFields (computed / derived values)
        - Use generationFields ONLY for values that can be computed from the extracted fields.
        - They are NOT extracted from the document — they are calculated.
        - instructions: plain English e.g.
          "Multiply totalAmount by 0.1 to compute GST amount"
          "Count the number of rows in lineItems"
          "Subtract invoiceDate from today to get days outstanding"
        
        ## Output
        - Output ONLY the JSON object — no explanation, no markdown fences.
        - If the document has no tables, output an empty tableFields array.
        - If no computed fields are needed, output an empty generationFields array.
        - Include every field the user asked for AND any other clearly useful fields
          visible in the document.
        """;


    public const string GenerationSystemPrompt = """
        You write a single C# expression to compute a derived field.
        TODO: full generation prompt.
        """;
}
