using System.Security.Cryptography;
using System.Text;

namespace Daksh.Platform.Runtime;

public enum QueueAction { Start, Enqueue, Coalesce, Steer, Interrupt, Reject }

public static class QueuePlanner
{
    /// <summary>What to do with a message that arrives for a session, given the agent's queue mode.</summary>
    public static QueueAction Decide(string mode, bool hasActiveRun, int queuedRuns, int cap)
    {
        if (!hasActiveRun && queuedRuns == 0) return QueueAction.Start;
        return mode switch
        {
            "interrupt" => QueueAction.Interrupt,
            "steer" when hasActiveRun => QueueAction.Steer,
            "collect" when queuedRuns > 0 => QueueAction.Coalesce,
            _ when queuedRuns >= cap => QueueAction.Reject,
            _ => QueueAction.Enqueue
        };
    }
}

/// <summary>Stops a run that keeps making the same tool call with the same arguments.</summary>
public sealed class LoopDetector(int maxConsecutive = 3, int maxTotal = 6)
{
    private readonly Dictionary<string, int> _total = [];
    private string? _last;
    private int _streak;

    public IReadOnlyDictionary<string, int> Totals => _total;

    public static string Signature(string tool, string arguments) =>
        tool + ":" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(arguments)))[..16];

    /// <summary>Records a call; returns a reason when the run should stop.</summary>
    public string? Record(string tool, string arguments)
    {
        var sig = Signature(tool, arguments);
        _streak = sig == _last ? _streak + 1 : 1;
        _last = sig;
        var total = _total[sig] = _total.GetValueOrDefault(sig) + 1;
        if (_streak >= maxConsecutive) return $"The tool '{tool}' was called {_streak} times in a row with the same arguments.";
        if (total >= maxTotal) return $"The tool '{tool}' was called {total} times with the same arguments in this run.";
        return null;
    }

    public void Restore(IEnumerable<(string Tool, string Arguments)> history)
    {
        foreach (var (t, a) in history) Record(t, a);
    }
}

public static class ContextBudget
{
    /// <summary>
    /// How many of the most recent messages fit. Always keeps at least <paramref name="keepRecent"/> messages and never
    /// starts on a tool result (which would orphan it from its call).
    /// </summary>
    public static int FirstIncludedIndex(IReadOnlyList<int> messageTokens, IReadOnlyList<bool> isToolResult, int budget, int keepRecent)
    {
        var total = 0;
        var start = messageTokens.Count;
        for (var i = messageTokens.Count - 1; i >= 0; i--)
        {
            var kept = messageTokens.Count - i;
            if (total + messageTokens[i] > budget && kept > keepRecent) break;
            total += messageTokens[i];
            start = i;
        }
        while (start < messageTokens.Count && isToolResult[start]) start++;
        return start;
    }

    public static bool ShouldCompact(int promptTokens, int contextWindow, double threshold) =>
        contextWindow > 0 && promptTokens > contextWindow * Math.Clamp(threshold, 0.1, 0.95);
}
