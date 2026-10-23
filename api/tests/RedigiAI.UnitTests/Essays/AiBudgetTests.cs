using RedigiAI.Application.Essays;

namespace RedigiAI.UnitTests.Essays;

/// <summary>Teto de custo de IA por redação (especificação, seção 8).</summary>
public class AiBudgetTests
{
    [Fact]
    public void Default_caps_are_the_reference_and_the_hard_limit_of_the_spec()
    {
        var options = new AiBudgetOptions();

        Assert.Equal((0.26m, 0.57m), (options.ReferenceCapUsd, options.HardCapUsd));
    }
}
