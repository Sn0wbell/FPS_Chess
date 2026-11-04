using System;
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

    /// <summary>Best move in UCI notation ("e2e4", "e7e8q"), or "(none)".</summary>
    Task<string> GetBestMoveAsync(string fen, CancellationToken ct);
}

public sealed class StockfishMoveEngine : IChessMoveEngine
{
    private readonly StockfishUCI uci = new StockfishUCI();
    private readonly int depth;
    private readonly int moveTimeMs;

    public bool IsReady { get; private set; }

    public StockfishMoveEngine(int depth, int moveTimeMs)
    {
        this.depth = Math.Max(1, depth);
        this.moveTimeMs = Math.Max(1, moveTimeMs);
    }

    /// <param name="skillLevel">Stockfish "Skill Level", 0 (weakest) .. 20 (full strength).</param>
    public async Task StartAsync(string exePath, int skillLevel)
    {
        await uci.StartEngineAsync(exePath);
        await uci.SetSkillLevelAsync(Math.Max(0, Math.Min(20, skillLevel)));
        await uci.SendReadyAsync();
        IsReady = true;
    }

    public async Task<string> GetBestMoveAsync(string fen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await uci.SendPositionAsync(fen);
        string best = await uci.GetBestMoveAsync(depth, moveTimeMs);
        ct.ThrowIfCancellationRequested();
        return best;
    }

    public void Dispose()
    {
        IsReady = false;
        uci.StopEngine();
    }
}
