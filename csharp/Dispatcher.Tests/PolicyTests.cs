using LlmDispatcher;

namespace Dispatcher.Tests;

public class PolicyTests
{
    private static readonly List<ModelSpec> Catalog =
    [
        new() { Id = "a/frontier", Provider = "anthropic", Model = "frontier", Name = "Frontier", Tier = 3, Price = new() { Input = 10, Output = 50 }, Context = 1_000_000, MaxOutput = 128_000, Vision = true, Tools = true, Effort = ["low", "medium", "high", "xhigh", "max"] },
        new() { Id = "a/standard", Provider = "anthropic", Model = "standard", Name = "Standard", Tier = 2, Price = new() { Input = 2, Output = 10 }, Context = 1_000_000, MaxOutput = 128_000, Vision = true, Tools = true, Effort = ["low", "medium", "high", "xhigh", "max"], Strengths = ["coding"] },
        new() { Id = "o/economy", Provider = "openai", Model = "economy", Name = "Economy", Tier = 1, Price = new() { Input = 0.2, Output = 1.2 }, Context = 400_000, MaxOutput = 128_000, Vision = true, Tools = true, Effort = ["minimal", "low", "medium", "high"] },
        new() { Id = "r/blind-cheap", Provider = "openrouter", Model = "blind", Name = "Blind cheap", Tier = 1, Price = new() { Input = 0.07, Output = 0.14 }, Context = 1_000_000, MaxOutput = 100_000, Vision = false, Tools = true, Effort = [] },
    ];

    private static Features F(bool images = false, int? maxTokens = null) => new()
    {
        InputTokens = 500, Turns = 1, HasImages = images, RequestedMaxTokens = maxTokens, LastUser = "hi",
    };

    private static Judgment J(string task = "conversation", double difficulty = 0.3, double dConf = 0.9, double reasoning = 0.2, double stakes = 0.05) => new()
    {
        Task = task, TaskConfidence = 0.9, Difficulty = new(difficulty, dConf), Reasoning = new(reasoning, 0.9), Stakes = stakes,
        OutputSize = new(0.2, 0.9), Source = "jev", JevInputTokens = 300, LatencyMs = 40,
    };

    [Fact]
    public void TrivialChatGoesToCheapestEconomyModel()
    {
        var d = Policy.Decide(F(), J(), Catalog);
        Assert.Equal(1, d.Tier);
        Assert.Equal("r/blind-cheap", d.Model.Id);
        Assert.Null(d.Effort);
        Assert.True(d.Estimate.Savings > 0);
        Assert.Equal("a/frontier", d.Estimate.BaselineModel);
    }

    [Fact]
    public void ImagesExcludeModelsWithoutVision()
    {
        var d = Policy.Decide(F(images: true), J(), Catalog);
        Assert.Equal("o/economy", d.Model.Id);
        Assert.Equal("minimal", d.Effort);
    }

    [Fact]
    public void HardReasoningHeavyWorkLandsOnFrontierAtHighEffort()
    {
        var d = Policy.Decide(F(), J("analysis", difficulty: 3.6, dConf: 0.8, reasoning: 2.7, stakes: 0.6), Catalog);
        Assert.Equal(3, d.Tier);
        Assert.Equal("a/frontier", d.Model.Id);
        Assert.Equal("xhigh", d.Effort);
    }

    [Fact]
    public void CodingStrengthLiftsStandardModelIntoFrontierPool()
    {
        var d = Policy.Decide(F(), J("coding", difficulty: 3.5, reasoning: 2.4, stakes: 0.5), Catalog);
        Assert.Equal(3, d.Tier);
        Assert.Equal("a/standard", d.Model.Id);
        Assert.Equal("high", d.Effort);
    }

    [Fact]
    public void HighStakesNeverRoutesToEconomy()
    {
        var d = Policy.Decide(F(), J(stakes: 0.85), Catalog);
        Assert.True(d.Tier >= 2);
        Assert.NotEqual(1, d.Model.Tier);
    }

    [Fact]
    public void LowDifficultyConfidenceBumpsOneTier()
    {
        var d = Policy.Decide(F(), J(difficulty: 0.5, dConf: 0.2), Catalog);
        Assert.Equal(2, d.Tier);
    }

    [Fact]
    public void JevFallbackIsConservative()
    {
        var d = Policy.Decide(F(), Judgment.Fallback("boom"), Catalog);
        Assert.Equal(3, d.Tier);
        Assert.Equal("high", d.Effort);
        Assert.Contains(d.Rationale, r => r.Contains("jev unavailable"));
    }

    [Fact]
    public void ExplicitBaselineChangesSavingsMath()
    {
        var d = Policy.Decide(F(), J(), Catalog, new PolicyOptions { BaselineId = "a/standard" });
        Assert.Equal("a/standard", d.Estimate.BaselineModel);
    }

    [Fact]
    public void RequestedMaxTokensAboveCapExcludesModel()
    {
        var d = Policy.Decide(F(maxTokens: 120_000), J(), Catalog);
        Assert.NotEqual("r/blind-cheap", d.Model.Id);
    }

    [Fact]
    public void EffortSnappingPrefersNextLevelUp()
    {
        Assert.Equal("low", Policy.SnapEffort("minimal", ["low", "medium", "high"]));
        Assert.Equal("high", Policy.SnapEffort("max", ["minimal", "low", "medium", "high"]));
        Assert.Null(Policy.SnapEffort("medium", []));
        Assert.Equal("minimal", Policy.DesiredEffort(0, 0, 0));
        Assert.Equal("high", Policy.DesiredEffort(3.5, 1.0, 0));
        Assert.Equal("max", Policy.DesiredEffort(3.8, 2.9, 0.9));
    }
}
