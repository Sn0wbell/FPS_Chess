using System;
using System.Collections.Generic;

// ======================================================
// AI DUEL AWARENESS (pure C#, no UnityEngine)
//
// Stockfish assumes every capture succeeds. In FPS-Chess a capture is a duel the
// attacker can lose (then it only loses its turn). When the engine's best move is a
// capture, the AI compares its EXPECTED value with the other candidate moves:
//
//   EV(capture) = p * score(capture) + (1 - p) * score(lost duel = pass)
//   EV(quiet)   = score(quiet)
//
// p comes from DuelOdds (tunable FPS strength per piece type).
// ======================================================

/// <summary>Estimated chance that the attacker wins a duel: p = A / (A + D) on FPS strengths.</summary>
public sealed class DuelOdds
{
    // Indexed by PieceType: Pawn, Rook, Knight, Bishop, Queen, King.
    public static readonly float[] DefaultStrength = { 1f, 5f, 3f, 3f, 9f, 4f };

    private readonly float[] strength;

    public DuelOdds(float[] strengthByPieceType = null)
    {
        strength = strengthByPieceType != null && strengthByPieceType.Length >= 6
            ? strengthByPieceType
            : DefaultStrength;
    }

    public float StrengthOf(PieceType t)
    {
        int i = (int)t;
        return i >= 0 && i < 6 ? Math.Max(0.01f, strength[i]) : 1f;
    }

    public double AttackerWinChance(PieceType attacker, PieceType defender)
    {
        double a = StrengthOf(attacker), d = StrengthOf(defender);
        return a / (a + d);
    }
}

/// <summary>One engine line: a move and its score from the side to move's point of view.</summary>
public readonly struct EngineCandidate
{
    /// <summary>Centipawn-like scale; mates are ±(MateScore - plies).</summary>
    public const int MateScore = 100000;

    public readonly string Uci;
    public readonly int Score;

    public EngineCandidate(string uci, int score)
    {
        Uci = uci;
        Score = score;
    }

    public static int FromMate(int mateInMoves) =>
        mateInMoves > 0 ? MateScore - mateInMoves : -MateScore - mateInMoves;

    public override string ToString() => $"{Uci}:{Score}";
}

public sealed class EngineAnalysis
{
    public string BestMove { get; }

    /// <summary>Top lines (MultiPV), best first. May be empty.</summary>
    public IReadOnlyList<EngineCandidate> Candidates { get; }

    public EngineAnalysis(string bestMove, IReadOnlyList<EngineCandidate> candidates)
    {
        BestMove = bestMove;
        Candidates = candidates ?? new EngineCandidate[0];
    }
}

public static class DuelRiskSelector
{
    public readonly struct Option
    {
        public readonly ChessMove Move;
        public readonly int Score;
        public readonly double WinChance; // ignored for non-captures

        public Option(ChessMove move, int score, double winChance)
        {
            Move = move;
            Score = score;
            WinChance = winChance;
        }

        public double ExpectedValue(int failScore) =>
            Move.IsCapture ? WinChance * Score + (1.0 - WinChance) * failScore : Score;
    }

    /// <summary>
    /// Keeps the engine's move unless it is a capture whose expected value (accounting for
    /// a lost duel) is beaten by another candidate. A quiet engine move is always kept, so
    /// the difficulty setting (Stockfish Skill Level) still shapes normal play.
    /// </summary>
    /// <param name="engineBestOption">The engine's chosen (legal) move with its score and win chance.</param>
    /// <param name="others">Other legal candidates with scores.</param>
    /// <param name="failScore">Score (AI's view) of the position after losing the duel (a pass).</param>
    public static ChessMove Choose(Option engineBestOption, IReadOnlyList<Option> others, int failScore)
    {
        ChessMove engineBest = engineBestOption.Move;
        if (!engineBest.IsCapture)
            return engineBest;

        ChessMove choice = engineBest;
        double bestValue = engineBestOption.ExpectedValue(failScore);

        foreach (Option o in others)
        {
            if (o.Move.Equals(engineBest))
                continue;
            double v = o.ExpectedValue(failScore);
            if (v > bestValue + 1e-6)
            {
                bestValue = v;
                choice = o.Move;
            }
        }

        return choice;
    }
}
