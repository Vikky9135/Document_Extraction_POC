using FluentAssertions;

namespace DIP.AgenticExtraction.Poc.Tests.Integration;

public class FullPipelineTests
{
    [Fact(Skip = "TODO Step 17 — Phase 3-9 end-to-end with a real PDF and WireMock for the LLM.")]
    public void FullPipeline_upload_to_result_succeeds()
    {
        true.Should().BeTrue();
    }
}
