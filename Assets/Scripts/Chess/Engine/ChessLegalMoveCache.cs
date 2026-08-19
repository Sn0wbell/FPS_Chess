using System.Collections.Generic;
using UnityEngine;

public sealed class ChessLegalMoveCache
{
    // ======================================================
    // DATA
    // ======================================================

    private readonly Dictionary<Vector2Int, List<Vector2Int>> movesFrom =
        new Dictionary<Vector2Int, List<Vector2Int>>(16);

    private readonly HashSet<Vector2Int> captureTargets =
        new HashSet<Vector2Int>();

    private bool isReady;

    // ======================================================
    // PUBLIC QUERY
    // ======================================================

    public bool IsReady => isReady;

    public IReadOnlyList<Vector2Int> GetMovesFrom(Vector2Int from)
    {
        if (!isReady)
            return System.Array.Empty<Vector2Int>();

        return movesFrom.TryGetValue(from, out var list)
            ? list
            : System.Array.Empty<Vector2Int>();
    }

    public bool IsLegal(Vector2Int from, Vector2Int to)
    {
        if (!isReady)
            return false;

        return movesFrom.TryGetValue(from, out var list) && list.Contains(to);
    }

    public bool CanCapture(Vector2Int square)
    {
        if (!isReady)
            return false;

        return captureTargets.Contains(square);
    }

    public void Clear()
    {
        movesFrom.Clear();
        captureTargets.Clear();
        isReady = false;
    }

    // ======================================================
    // BUILD
    // ======================================================

    public void Rebuild(ChessBoardManager board)
    {
        Clear();

        if (board == null)
            return;

        bool whiteToMove = board.whiteToMove;

        for (int f = 0; f < 8; f++)
        {
            for (int r = 0; r < 8; r++)
            {
                ChessPiece p = board.GetPieceAt(f, r);
                if (p == null || !p.IsActiveOnBoard)
                    continue;

                if (p.isWhite != whiteToMove)
                    continue;

                Vector2Int from = new Vector2Int(f, r);

                switch (p.type)
                {
                    case PieceType.Pawn:
                        AddPawnMoves(board, p, from);
                        break;
                    case PieceType.Knight:
                        AddKnightMoves(board, p, from);
                        break;
                    case PieceType.Bishop:
                        AddSliding(board, p, from, bishopDirs);
                        break;
                    case PieceType.Rook:
                        AddSliding(board, p, from, rookDirs);
                        break;
                    case PieceType.Queen:
                        AddSliding(board, p, from, queenDirs);
                        break;
                    case PieceType.King:
                        AddKingMoves(board, p, from);
                        break;
                }
            }
        }

        isReady = true;
    }

    // ======================================================
    // ADD HELPERS
    // ======================================================

    private void AddMove(Vector2Int from, Vector2Int to, bool isCapture)
    {
        if (!movesFrom.TryGetValue(from, out var list))
        {
            list = new List<Vector2Int>(8);
            movesFrom[from] = list;
        }

        list.Add(to);

        if (isCapture)
            captureTargets.Add(to);
    }

    // ======================================================
    // PAWN
    // ======================================================

    private void AddPawnMoves(ChessBoardManager board, ChessPiece p, Vector2Int from)
    {
        int dir = p.isWhite ? 1 : -1;
        int startRank = p.isWhite ? 1 : 6;

        int f = from.x;
        int r = from.y;

        TryPawnForward(board, from, f, r + dir);

        if (r == startRank)
            TryPawnForward(board, from, f, r + dir * 2);

        TryPawnCapture(board, p, from, f - 1, r + dir);
        TryPawnCapture(board, p, from, f + 1, r + dir);
    }

    private void TryPawnForward(
        ChessBoardManager board,
        Vector2Int from,
        int f,
        int r)
    {
        if (!InBounds(f, r))
            return;

        if (board.GetPieceAt(f, r) == null)
            AddMove(from, new Vector2Int(f, r), false);
    }

    private void TryPawnCapture(
        ChessBoardManager board,
        ChessPiece p,
        Vector2Int from,
        int f,
        int r)
    {
        if (!InBounds(f, r))
            return;

        ChessPiece target = board.GetPieceAt(f, r);
        if (target != null && target.isWhite != p.isWhite)
        {
            AddMove(from, new Vector2Int(f, r), true);
            return;
        }

        if (board.EnPassantFile == f && board.EnPassantRank == r)
        {
            ChessPiece epPawn = board.GetPieceAt(f, from.y);
            if (epPawn != null &&
                epPawn.type == PieceType.Pawn &&
                epPawn.isWhite != p.isWhite)
            {
                AddMove(from, new Vector2Int(f, r), true);
            }
        }
    }

    // ======================================================
    // KNIGHT
    // ======================================================

    private static readonly Vector2Int[] knightOffsets =
    {
        new Vector2Int(1,2), new Vector2Int(2,1),
        new Vector2Int(-1,2), new Vector2Int(-2,1),
        new Vector2Int(1,-2), new Vector2Int(2,-1),
        new Vector2Int(-1,-2), new Vector2Int(-2,-1),
    };

    private void AddKnightMoves(
        ChessBoardManager board,
        ChessPiece p,
        Vector2Int from)
    {
        foreach (var o in knightOffsets)
        {
            Vector2Int to = from + o;
            if (!InBounds(to.x, to.y))
                continue;

            ChessPiece t = board.GetPieceAt(to.x, to.y);
            if (t == null)
                AddMove(from, to, false);
            else if (t.isWhite != p.isWhite)
                AddMove(from, to, true);
        }
    }

    // ======================================================
    // SLIDERS
    // ======================================================

    private static readonly Vector2Int[] rookDirs =
    {
        new Vector2Int(1,0), new Vector2Int(-1,0),
        new Vector2Int(0,1), new Vector2Int(0,-1)
    };

    private static readonly Vector2Int[] bishopDirs =
    {
        new Vector2Int(1,1), new Vector2Int(1,-1),
        new Vector2Int(-1,1), new Vector2Int(-1,-1)
    };

    private static readonly Vector2Int[] queenDirs =
    {
        new Vector2Int(1,0), new Vector2Int(-1,0),
        new Vector2Int(0,1), new Vector2Int(0,-1),
        new Vector2Int(1,1), new Vector2Int(1,-1),
        new Vector2Int(-1,1), new Vector2Int(-1,-1)
    };

    private void AddSliding(
        ChessBoardManager board,
        ChessPiece p,
        Vector2Int from,
        Vector2Int[] dirs)
    {
        foreach (var d in dirs)
        {
            Vector2Int cur = from + d;

            while (InBounds(cur.x, cur.y))
            {
                ChessPiece t = board.GetPieceAt(cur.x, cur.y);

                if (t == null)
                {
                    AddMove(from, cur, false);
                }
                else
                {
                    if (t.isWhite != p.isWhite)
                        AddMove(from, cur, true);
                    break;
                }

                cur += d;
            }
        }
    }

    // ======================================================
    // KING (CASTLING FIXED)
    // ======================================================

    private void AddKingMoves(
        ChessBoardManager board,
        ChessPiece p,
        Vector2Int from)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0)
                    continue;

                Vector2Int to = new Vector2Int(from.x + dx, from.y + dy);
                if (!InBounds(to.x, to.y))
                    continue;

                ChessPiece t = board.GetPieceAt(to.x, to.y);
                if (t == null)
                    AddMove(from, to, false);
                else if (t.isWhite != p.isWhite)
                    AddMove(from, to, true);
            }
        }

        // ---- CASTLING ----
        if (IsSquareAttacked(board, from, !p.isWhite))
            return;

        if (p.isWhite && !board.WhiteKingMoved && from == new Vector2Int(4, 0))
        {
            TryAddCastle(board, from, true, true);
            TryAddCastle(board, from, false, true);
        }
        else if (!p.isWhite && !board.BlackKingMoved && from == new Vector2Int(4, 7))
        {
            TryAddCastle(board, from, true, false);
            TryAddCastle(board, from, false, false);
        }
    }

    private void TryAddCastle(
        ChessBoardManager board,
        Vector2Int kingFrom,
        bool kingSide,
        bool isWhite)
    {
        int rank = isWhite ? 0 : 7;

        if (kingSide)
        {
            if ((isWhite && board.WhiteRookH1Moved) ||
                (!isWhite && board.BlackRookH8Moved))
                return;

            if (board.GetPieceAt(5, rank) != null ||
                board.GetPieceAt(6, rank) != null)
                return;

            if (IsSquareAttacked(board, new Vector2Int(5, rank), !isWhite) ||
                IsSquareAttacked(board, new Vector2Int(6, rank), !isWhite))
                return;

            AddMove(kingFrom, new Vector2Int(6, rank), false);
        }
        else
        {
            if ((isWhite && board.WhiteRookA1Moved) ||
                (!isWhite && board.BlackRookA8Moved))
                return;

            if (board.GetPieceAt(1, rank) != null ||
                board.GetPieceAt(2, rank) != null ||
                board.GetPieceAt(3, rank) != null)
                return;

            if (IsSquareAttacked(board, new Vector2Int(3, rank), !isWhite) ||
                IsSquareAttacked(board, new Vector2Int(2, rank), !isWhite))
                return;

            AddMove(kingFrom, new Vector2Int(2, rank), false);
        }
    }

    // ======================================================
    // ATTACK CHECK
    // ======================================================

    private bool IsSquareAttacked(
        ChessBoardManager board,
        Vector2Int square,
        bool byWhite)
    {
        for (int f = 0; f < 8; f++)
        {
            for (int r = 0; r < 8; r++)
            {
                ChessPiece p = board.GetPieceAt(f, r);
                if (p == null || p.isWhite != byWhite)
                    continue;

                if (AttacksSquare(board, p, new Vector2Int(f, r), square))
                    return true;
            }
        }
        return false;
    }

    private bool AttacksSquare(
        ChessBoardManager board,
        ChessPiece p,
        Vector2Int from,
        Vector2Int target)
    {
        Vector2Int d = target - from;

        switch (p.type)
        {
            case PieceType.Pawn:
                int dir = p.isWhite ? 1 : -1;
                return d.y == dir && Mathf.Abs(d.x) == 1;

            case PieceType.Knight:
                return System.Math.Abs(d.x * d.y) == 2;

            case PieceType.Bishop:
                return IsSlidingAttack(board, from, target, bishopDirs);

            case PieceType.Rook:
                return IsSlidingAttack(board, from, target, rookDirs);

            case PieceType.Queen:
                return IsSlidingAttack(board, from, target, queenDirs);

            case PieceType.King:
                return Mathf.Max(Mathf.Abs(d.x), Mathf.Abs(d.y)) == 1;
        }

        return false;
    }

    private bool IsSlidingAttack(
        ChessBoardManager board,
        Vector2Int from,
        Vector2Int target,
        Vector2Int[] dirs)
    {
        foreach (var d in dirs)
        {
            Vector2Int cur = from + d;
            while (InBounds(cur.x, cur.y))
            {
                if (cur == target)
                    return true;

                if (board.GetPieceAt(cur.x, cur.y) != null)
                    break;

                cur += d;
            }
        }
        return false;
    }

    // ======================================================
    // UTIL
    // ======================================================

    private static bool InBounds(int f, int r) =>
        (uint)f < 8 && (uint)r < 8;
}
