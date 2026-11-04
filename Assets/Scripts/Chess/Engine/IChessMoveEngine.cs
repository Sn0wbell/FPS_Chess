using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Chess.Engine;

/// <summary>
/// Move source for the AI side. Stockfish in the game; a fake in the logic tests.
/// Returned moves are always re-validated by the rules core before being played.
/// </summary>
public interface IChessMoveEngine : IDisposable
{
    bool IsReady { get; }

    /// <summary>
    /// Best move in UCI notation ("e2e4", "e7e8q", or "(none)") plus the top candidate
    /// lines with scores from the side to move's point of view (may be empty).
    /// </summary>
    Task<EngineAnalysis> AnalyseAsync(string fen, CancellationToken ct);
}

public sealed class StockfishMoveEngine : IChessMoveEngine
{
    private readonly StockfishUCI uci = new StockfishUCI();
    private readonly int depth;
    private readonly int moveTimeMs;
    private readonly int multiPv;

    public bool IsReady { get; private set; }

    public StockfishMoveEngine(int depth, int moveTimeMs, int multiPv = 1)
    {
        this.depth = Math.Max(1, depth);
        this.moveTimeMs = Math.Max(1, moveTimeMs);
        this.multiPv = Math.Max(1, Math.Min(10, multiPv));
    }

    /// <param name="skillLevel">Stockfish "Skill Level", 0 (weakest) .. 20 (full strength).</param>
    public async Task StartAsync(string exePath, int skillLevel)
    {
        await uci.StartEngineAsync(exePath);
        await uci.SetSkillLevelAsync(Math.Max(0, Math.Min(20, skillLevel)));
        await uci.SetOptionAsync("MultiPV", multiPv);
        await uci.SendReadyAsync();
        IsReady = true;
    }

    public async Task<EngineAnalysis> AnalyseAsync(string fen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await uci.SendPositionAsync(fen);
        var (best, lines) = await uci.AnalyseAsync(depth, moveTimeMs);
        ct.ThrowIfCancellationRequested();

        var candidates = new List<EngineCandidate>(lines.Count);
        foreach (StockfishUCI.PvLine l in lines)
            candidates.Add(new EngineCandidate(l.Move, l.Score));

        return new EngineAnalysis(best, candidates);
    }

    public void Dispose()
    {
        IsReady = false;
        uci.StopEngine();
    }
}
