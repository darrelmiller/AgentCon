using BuyerAgent;
using System.Text.Json;
using Xunit;

namespace BuyerAgent.Tests;

public sealed class AgentLlmMetricsTests
{
    [Fact]
    public void ActualSdkUsageReportsMillisecondsAndMissingDurationIsNotAssumedToBeZero()
    {
        var metrics = new AgentLlmMetrics();
        foreach (var duration in new double?[] { 123, 456, null })
        {
            metrics.Call();
            var fixture = JsonSerializer.SerializeToElement(new
            {
                type = "assistant.usage", timestamp = "2026-10-10T20:00:00Z",
                data = new { model = "test-model", duration }
            });
            var usage = fixture.Deserialize<GitHub.Copilot.AssistantUsageEvent>(CopilotUsageTrace.JsonOptions)!;
            metrics.Usage(usage);
        }
        var result = metrics.Snapshot();
        Assert.Equal(3, result.Calls);
        Assert.Equal(579, result.ResponseTimeMs);
        Assert.Equal(2, result.TimedCalls);
    }

    [Fact]
    public void BuyerTotalsAccumulateAndResetWithoutMessages()
    {
        var store = new ExchangeStore(new BuyerOptions());
        store.BuyerLlm.Call();
        store.BuyerLlm.Call();
        store.BuyerLlm.Duration(1200);
        store.BuyerLlm.Duration(350);
        store.BuyerLlm.Duration(double.NaN);
        store.BuyerLlm.Duration(-1);
        Assert.Equal(2, store.BuyerLlm.Snapshot().Calls);
        Assert.Equal(1550, store.BuyerLlm.Snapshot().ResponseTimeMs);
        Assert.Equal(2, store.BuyerLlm.Snapshot().TimedCalls);
        store.Clear();
        Assert.Equal(0, store.BuyerLlm.Snapshot().Calls);
        Assert.Equal(0, store.BuyerLlm.Snapshot().ResponseTimeMs);
        store.BuyerLlm.Call();
        Assert.Equal(1, store.BuyerLlm.Snapshot().Calls);
    }

    [Fact]
    public void SupplierTotalsSurvivePollingClearAndProcessRestartsIndependently()
    {
        var store = new ExchangeStore(new BuyerOptions());
        var sample = new AgentLlmTotals("first", 3, 1500, 3);
        Assert.Equal(3, store.SupplierLlm("barolo", sample).Calls);
        Assert.Equal(3, store.SupplierLlm("barolo", sample).Calls);
        store.SupplierLlm("france", new("other", 10, 5000, 10));
        store.Clear();
        Assert.Equal(0, store.SupplierLlm("barolo", sample).Calls);
        var after = store.SupplierLlm("barolo", sample with { Calls = 4, ResponseTimeMs = 1750, TimedCalls = 4 });
        Assert.Equal(1, after.Calls);
        Assert.Equal(250, after.ResponseTimeMs);
        Assert.Equal(1, after.TimedCalls);
        after = store.SupplierLlm("barolo", new("restarted", 2, 800, 2));
        Assert.Equal(3, after.Calls);
        Assert.Equal(1050, after.ResponseTimeMs);
        Assert.Equal(0, store.SupplierLlm("france", new("other", 10, 5000, 10)).Calls);
    }

    [Fact]
    public void ClearWhileSupplierIsOfflineBaselinesTheNextObservation()
    {
        var store = new ExchangeStore(new BuyerOptions());
        store.SupplierLlm("offline", new("same", 3, 1500, 3));
        store.Clear(new HashSet<string>());
        var after = store.SupplierLlm("offline", new("same", 20, 9000, 20));
        Assert.Equal(0, after.Calls);
        Assert.Equal(0, after.ResponseTimeMs);
        Assert.Equal(1, store.SupplierLlm("offline", new("same", 21, 9500, 21)).Calls);
        Assert.Equal(0, store.SupplierLlm("new", new("new-instance", 10, 5000, 10)).Calls);
    }

    [Fact]
    public void AnOlderSampleCannotBeCountedAgainOnTheNextPoll()
    {
        var store = new ExchangeStore(new BuyerOptions());
        store.SupplierLlm("seller", new("same", 4, 2000, 4));
        store.SupplierLlm("seller", new("same", 3, 1500, 3));
        var after = store.SupplierLlm("seller", new("same", 5, 2500, 5));
        Assert.Equal(5, after.Calls);
        Assert.Equal(2500, after.ResponseTimeMs);
        Assert.Equal(5, after.TimedCalls);
    }
}
