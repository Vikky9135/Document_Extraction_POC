using FluentAssertions;

namespace DIP.AgenticExtraction.Poc.Tests.Unit;

public class GenerationAgentTests
{
    [Fact(Skip = "TODO Step 17 — assert Roslyn executes generated expressions safely.")]
    public void Compute_evaluates_derived_field()
    {
        true.Should().BeTrue();
    }
}
