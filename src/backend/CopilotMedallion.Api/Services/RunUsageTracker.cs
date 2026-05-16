using System.Collections.Concurrent;

namespace CopilotMedallion.Api.Services;

/// <summary>
/// In-memory per-run token usage + cost tracker. Aggregates across all LLM calls
/// made on behalf of a single build run (spec proposal, notebook generation, auto-fix
/// revisions). Survives only until the App Service process restarts, which matches
/// the rest of the run state lifetime.
/// </summary>
public class RunUsageTracker
{
    private readonly ConcurrentDictionary<string, RunUsage> _byRun = new();

    public void Record(string runId, string model, int promptTokens, int completionTokens)
    {
        if (string.IsNullOrEmpty(runId)) return;
        _byRun.AddOrUpdate(runId,
            _ => Create(model, promptTokens, completionTokens),
            (_, existing) => Merge(existing, model, promptTokens, completionTokens));
    }

    public RunUsage? Get(string runId) => _byRun.TryGetValue(runId, out var v) ? v.Clone() : null;

    private static RunUsage Create(string model, int prompt, int completion)
    {
        var u = new RunUsage();
        Merge(u, model, prompt, completion);
        return u;
    }

    private static RunUsage Merge(RunUsage u, string model, int prompt, int completion)
    {
        u.PromptTokens += prompt;
        u.CompletionTokens += completion;
        u.Requests += 1;
        if (!u.PerModel.TryGetValue(model, out var m))
            m = new ModelUsage { Model = model };
        m.PromptTokens += prompt;
        m.CompletionTokens += completion;
        m.Requests += 1;
        u.PerModel[model] = m;
        u.EstimatedCostUsd = EstimateCost(u.PerModel.Values);
        return u;
    }

    // Rough pricing table (USD per 1M tokens). Update when Azure OpenAI pricing changes.
    // Source: Azure OpenAI pricing page. Numbers are deliberately conservative.
    private static readonly Dictionary<string, (decimal Input, decimal Output)> Pricing =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.4"]       = (5.00m,  15.00m),
            ["gpt-5.3-codex"] = (5.00m,  15.00m),
            ["gpt-5"]         = (5.00m,  15.00m),
            ["gpt-5-mini"]    = (0.25m,   2.00m),
            ["gpt-4.1"]       = (2.00m,   8.00m),
            ["gpt-4o"]        = (2.50m,  10.00m),
            ["gpt-4o-mini"]   = (0.15m,   0.60m),
            ["o1"]            = (15.00m, 60.00m),
            ["o3-mini"]       = (1.10m,   4.40m),
        };

    private static decimal EstimateCost(IEnumerable<ModelUsage> models)
    {
        decimal total = 0m;
        foreach (var m in models)
        {
            // Try exact, then "starts-with" matching (e.g. "gpt-5.4-2025-08-01" → "gpt-5.4")
            if (!Pricing.TryGetValue(m.Model, out var rate))
            {
                var fallback = Pricing.Keys
                    .Where(k => m.Model.StartsWith(k, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(k => k.Length)
                    .FirstOrDefault();
                if (fallback != null) rate = Pricing[fallback];
                else continue;
            }
            total += (m.PromptTokens     / 1_000_000m) * rate.Input;
            total += (m.CompletionTokens / 1_000_000m) * rate.Output;
        }
        return Math.Round(total, 4);
    }
}

public class RunUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens => PromptTokens + CompletionTokens;
    public int Requests { get; set; }
    public decimal EstimatedCostUsd { get; set; }
    public Dictionary<string, ModelUsage> PerModel { get; set; } = new();

    public RunUsage Clone()
    {
        var c = new RunUsage
        {
            PromptTokens = PromptTokens,
            CompletionTokens = CompletionTokens,
            Requests = Requests,
            EstimatedCostUsd = EstimatedCostUsd
        };
        foreach (var kv in PerModel)
            c.PerModel[kv.Key] = new ModelUsage { Model = kv.Value.Model, PromptTokens = kv.Value.PromptTokens, CompletionTokens = kv.Value.CompletionTokens, Requests = kv.Value.Requests };
        return c;
    }
}

public class ModelUsage
{
    public string Model { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int Requests { get; set; }
}
