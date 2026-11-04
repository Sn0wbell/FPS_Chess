using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

// ======================================================
// MOVE SNAPSHOT (visuals only)
// ======================================================
public struct MoveResult
{
    public ChessMove move;

    public ChessPiece mover;
    public Vector2Int from;
    public Vector2Int to;

    public ChessPiece captured;
    public Vector3 capturedWorldPos;

    public bool isCastling;
    public ChessPiece rook;
    public Vector2Int rookFrom;
    public Vector2Int rookTo;
}

/// <summary>
/// Owns the board: the rules state (<see cref="ChessPosition"/>, authoritative)
/// and the piece GameObjects that mirror it (visual only).
///
/// State changes go through exactly two doors:
///   <see cref="CommitMoveAsync"/> — a legal move is played (after a won duel for captures).
///   <see cref="PassTurn"/>        — the attacker lost the FPS duel and loses its turn.
/// </summary>
public sealed class ChessBoardManager : MonoBehaviour
{
    [Header("References")]
    public ChessGrid grid;
    public ChessPieceSpawner spawner;
    public MoveAnimationController moveAnimator;
    public BrokenShatterGenerator shatter;

    [Header("Highlight")]
    public BoardHighlightSystem highlight;

    [Header("Setup")]
    [Tooltip("Optional FEN to start from (testing). Empty = standard start position.")]
    [SerializeField] private string startFen = "";

    // ======================================================
    // STATE
    // ======================================================
    private ChessPosition position = ChessPosition.CreateStart();
    private readonly ChessPiece[] pieceObjects = new ChessPiece[64];
    private readonly List<ChessMove> moveBuffer = new List<ChessMove>(64);

    // Re-entrancy guard
    private bool isApplyingMove;

    // ======================================================
    // READ-ONLY STATE ACCESS
    // ======================================================

    /// <summary>Rules state. Read it freely; never mutate it from outside this class.</summary>
    public ChessPosition Position => position;

    public bool whiteToMove => position.WhiteToMove;

    public bool IsApplyingMove => isApplyingMove;

    // ======================================================
    // INIT
    // ======================================================
    public void InitializeBoard() => InitializeBoard(startFen);

    public void InitializeBoard(string fen)
    {
        ClearPieceObjects();

        if (string.IsNullOrWhiteSpace(fen))
        {
            position = ChessPosition.CreateStart();
        }
        else
        {
            try
            {
                position = ChessPosition.FromFen(fen);
            }
            catch (System.FormatException ex)
            {
                Debug.LogError($"ChessBoardManager: invalid start FEN, using standard start. {ex.Message}", this);
                position = ChessPosition.CreateStart();
            }
        }

        for (int sq = 0; sq < 64; sq++)
        {
            BoardPiece bp = position.GetPiece(sq);
            if (bp.IsEmpty)
                continue;

            int f = ChessPosition.FileOf(sq);
            int r = ChessPosition.RankOf(sq);

            ChessPiece piece = spawner != null ? spawner.SpawnPiece(bp.Type, bp.IsWhite, f, r) : null;
            if (piece == null)
            {
                Debug.LogError($"ChessBoardManager: failed to spawn {bp} at {ChessPosition.SquareName(sq)}.", this);
                continue;
            }

            piece.SetPosition(grid.BoardToWorld(f, r), f, r);
            pieceObjects[sq] = piece;
        }

        isApplyingMove = false;

        if (highlight != null)
            highlight.ClearAll();

        RefreshCheckHighlight();
    }

    private void ClearPieceObjects()
    {
        for (int i = 0; i < 64; i++)
        {
            if (pieceObjects[i] != null)
                Destroy(pieceObjects[i].gameObject);
            pieceObjects[i] = null;
        }
    }

    // ======================================================
    // QUERIES
    // ======================================================

    public bool TryGetLegalMove(string uci, out ChessMove move) =>
        position.TryFindLegalMove(uci, out move);

    /// <param name="promotion">None = queen when the move is a promotion.</param>
    public bool TryGetLegalMove(Vector2Int from, Vector2Int to, PieceType promotion, out ChessMove move) =>
        position.TryFindLegalMove(
            ChessPosition.Square(from.x, from.y),
            ChessPosition.Square(to.x, to.y),
            promotion,
            out move);

    public void GetLegalMoves(List<ChessMove> moves) => position.GenerateLegalMoves(moves);

    public GameResult EvaluateResult() => position.GetResult();

    public string GenerateFEN() => position.ToFen();

    public ChessPiece GetPieceAt(int file, int rank) =>
        ChessPosition.InBounds(file, rank) ? pieceObjects[ChessPosition.Square(file, rank)] : null;

    // ======================================================
    // STATE CHANGE 1: COMMIT A LEGAL MOVE
    // ======================================================

    /// <summary>
    /// Plays a legal move on the rules state, then animates it.
    /// Returns false (nothing changed) when the move is illegal or another move is in progress.
    /// </summary>
    public async Task<bool> CommitMoveAsync(ChessMove move)
    {
        if (isApplyingMove)
            return false;

        isApplyingMove = true;

        try
        {
            ChessPiece mover = pieceObjects[move.From];
            if (mover == null)
            {
                Debug.LogError($"ChessBoardManager: no piece object on {ChessPosition.SquareName(move.From)} for {move}. Board desync.", this);
                return false;
            }

            if (!position.TryApply(move, out AppliedMove applied))
                return false;

            MoveResult result = SyncPieceObjects(applied, mover);

            highlight?.ClearTransient();
            highlight?.ClearCheck();

            await PlayMoveVisuals(result);

            if (move.IsPromotion)
                ReplacePromotedPiece(move.To, move.Promotion);

            highlight?.ShowLastMove(result.from, result.to);
            RefreshCheckHighlight();

            return true;
        }
        finally
        {
            isApplyingMove = false;
        }
    }

    /// <summary>Convenience: find the legal move for a UCI string and commit it.</summary>
    public async Task<bool> ApplyMove(string uci)
    {
        if (!TryGetLegalMove(uci, out ChessMove move))
            return false;

        return await CommitMoveAsync(move);
    }

    /// <summary>Mirrors an applied move onto the piece GameObjects (board logic already committed).</summary>
    private MoveResult SyncPieceObjects(AppliedMove applied, ChessPiece mover)
    {
        ChessMove move = applied.Move;

        var result = new MoveResult
        {
            move = move,
            mover = mover,
            from = new Vector2Int(move.FromFile, move.FromRank),
            to = new Vector2Int(move.ToFile, move.ToRank)
        };

        if (applied.CapturedSquare >= 0)
        {
            ChessPiece captured = pieceObjects[applied.CapturedSquare];
            pieceObjects[applied.CapturedSquare] = null;
            result.captured = captured;
            result.capturedWorldPos = captured != null
                ? captured.transform.position
                : grid.BoardToWorld(ChessPosition.FileOf(applied.CapturedSquare), ChessPosition.RankOf(applied.CapturedSquare));
        }

        pieceObjects[move.From] = null;
        pieceObjects[move.To] = mover;
        mover.SetBoardCoords(move.ToFile, move.ToRank);

        if (applied.RookFrom >= 0)
        {
            ChessPiece rook = pieceObjects[applied.RookFrom];
            pieceObjects[applied.RookFrom] = null;
            pieceObjects[applied.RookTo] = rook;

            int rf = ChessPosition.FileOf(applied.RookTo);
            int rr = ChessPosition.RankOf(applied.RookTo);
            if (rook != null)
                rook.SetBoardCoords(rf, rr);

            result.isCastling = true;
            result.rook = rook;
            result.rookFrom = new Vector2Int(ChessPosition.FileOf(applied.RookFrom), ChessPosition.RankOf(applied.RookFrom));
            result.rookTo = new Vector2Int(rf, rr);
        }

        return result;
    }

    // ======================================================
    // STATE CHANGE 2: PASS (ATTACKER LOST THE DUEL)
    // ======================================================

    /// <summary>
    /// The side to move lost its capture duel: nothing moves, the turn passes.
    /// </summary>
    public bool PassTurn()
    {
        if (isApplyingMove)
            return false;

        position.PassTurn();

        highlight?.ClearTransient();
        RefreshCheckHighlight();
        return true;
    }

    // ======================================================
    // CHECK / CHECKMATE (HIGHLIGHT ONLY)
    // ======================================================
    private void RefreshCheckHighlight()
    {
        if (highlight == null)
            return;

        highlight.ClearCheck();

        bool side = position.WhiteToMove;

        if (position.TryFindKing(side, out int k) && position.IsInCheck(side))
        {
            position.GenerateLegalMoves(moveBuffer);
            highlight.ShowCheck(SquareToVec(k), moveBuffer.Count == 0);
            return;
        }

        // Variant: after a lost duel the side that just passed may still be in check,
        // i.e. its king can now be captured. Show it as a (non-mate) check.
        if (position.TryFindKing(!side, out int ek) && position.IsInCheck(!side))
            highlight.ShowCheck(SquareToVec(ek), false);
    }

    private static Vector2Int SquareToVec(int sq) =>
        new Vector2Int(ChessPosition.FileOf(sq), ChessPosition.RankOf(sq));

    // ======================================================
    // VISUALS (ANIMATION → SHATTER → CAPTURE)
    // ======================================================
    private async Task PlayMoveVisuals(MoveResult r)
    {
        Vector3 target = grid.BoardToWorld(r.to.x, r.to.y);

        // MOVE MAIN PIECE
        if (moveAnimator != null)
            await moveAnimator.AnimateMove(
                r.mover.transform,
                target,
                r.captured != null ? r.captured.transform : null
            );
        else
            r.mover.transform.position = target;

        // CASTLING ROOK
        if (r.isCastling && r.rook != null)
        {
            Vector3 rookTarget = grid.BoardToWorld(r.rookTo.x, r.rookTo.y);
            if (moveAnimator != null)
                await moveAnimator.AnimateMove(r.rook.transform, rookTarget);
            else
                r.rook.transform.position = rookTarget;
        }

        // CAPTURE (AFTER ALL MOVES)
        if (r.captured != null)
        {
            // Detach so it never blocks raycast / hover
            r.captured.transform.SetParent(null, true);

            if (shatter != null)
            {
                MeshRenderer mr = r.captured.GetComponentInChildren<MeshRenderer>();
                if (r.captured.gameObject.activeInHierarchy)
                    r.captured.gameObject.SetActive(false);

                await shatter.ShatterAt(
                    r.capturedWorldPos,
                    mr != null ? mr.sharedMaterial : null
                );
            }

            r.captured.Capture();
        }
    }

    // ======================================================
    // PROMOTION (MODEL SWAP)
    // ======================================================
    private void ReplacePromotedPiece(int sq, PieceType newType)
    {
        ChessPiece pawn = pieceObjects[sq];
        if (pawn == null)
            return;

        int f = ChessPosition.FileOf(sq);
        int r = ChessPosition.RankOf(sq);

        ChessPiece promoted = spawner != null ? spawner.SpawnPiece(newType, pawn.isWhite, f, r) : null;
        if (promoted == null)
        {
            // Logic is already correct in ChessPosition; keep the pawn model as a fallback.
            Debug.LogWarning($"ChessBoardManager: promotion model for {newType} missing, keeping pawn model.", this);
            pawn.type = newType;
            return;
        }

        promoted.SetPosition(grid.BoardToWorld(f, r), f, r);
        pieceObjects[sq] = promoted;
        Destroy(pawn.gameObject);
    }
}
