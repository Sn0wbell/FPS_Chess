using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// UX cache of the FULLY LEGAL moves of the side to move, keyed by board squares.
/// Built from <see cref="ChessPosition"/> (the rules core), so pinned pieces and
/// moves that ignore a check are never offered to the player.
/// Promotion variants of the same from/to are collapsed into one target square.
/// </summary>
public sealed class ChessLegalMoveCache
{
    private readonly Dictionary<Vector2Int, List<Vector2Int>> movesFrom =
        new Dictionary<Vector2Int, List<Vector2Int>>(16);

    // from * 64 + to for every capturing move (incl. en passant / king capture)
    private readonly HashSet<int> captureMoves = new HashSet<int>();

    private readonly List<ChessMove> buffer = new List<ChessMove>(64);

    private bool isReady;

    public bool IsReady => isReady;

    public IReadOnlyList<Vector2Int> GetMovesFrom(Vector2Int from)
    {
        if (!isReady)
            return System.Array.Empty<Vector2Int>();

        return movesFrom.TryGetValue(from, out var list)
            ? list
            : (IReadOnlyList<Vector2Int>)System.Array.Empty<Vector2Int>();
    }

    public bool IsLegal(Vector2Int from, Vector2Int to)
    {
        if (!isReady)
            return false;

        return movesFrom.TryGetValue(from, out var list) && list.Contains(to);
    }

    /// <summary>True when the legal move from -> to captures something.</summary>
    public bool IsCapture(Vector2Int from, Vector2Int to)
    {
        if (!isReady)
            return false;

        return captureMoves.Contains(Key(from, to));
    }

    public void Clear()
    {
        movesFrom.Clear();
        captureMoves.Clear();
        isReady = false;
    }

    public void Rebuild(ChessBoardManager board)
    {
        if (board == null || board.Position == null)
        {
            Clear();
            return;
        }

        Rebuild(board.Position);
    }

    public void Rebuild(ChessPosition position)
    {
        Clear();

        if (position == null)
            return;

        position.GenerateLegalMoves(buffer);

        foreach (ChessMove m in buffer)
        {
            Vector2Int from = new Vector2Int(m.FromFile, m.FromRank);
            Vector2Int to = new Vector2Int(m.ToFile, m.ToRank);

            if (!movesFrom.TryGetValue(from, out var list))
            {
                list = new List<Vector2Int>(8);
                movesFrom[from] = list;
            }

            if (!list.Contains(to))
                list.Add(to);

            if (m.IsCapture)
                captureMoves.Add(Key(from, to));
        }

        isReady = true;
    }

    private static int Key(Vector2Int from, Vector2Int to) =>
        (from.y * 8 + from.x) * 64 + (to.y * 8 + to.x);
}
