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

    [Header("Last Stand Highlight (piece outline)")]
    [SerializeField] private Color lastStandKingColor = new Color(1f, 0.8f, 0.1f, 1f);
    [SerializeField] private Color lastStandAttackerColor = new Color(1f, 0.15f, 0.1f, 1f);
    [Range(0f, 1f)]
    [SerializeField] private float lastStandOutlineAlpha = 0.55f;

    [Header("Duel Preview (piece outline)")]
    [SerializeField] private Color duelAttackerColor = new Color(1f, 0.8f, 0.1f, 1f);
    [SerializeField] private Color duelDefenderColor = new Color(1f, 0.15f, 0.1f, 1f);

    // ======================================================
    // STATE
    // ======================================================
    private ChessPosition position = ChessPosition.CreateStart();
    private readonly ChessPiece[] pieceObjects = new ChessPiece[64];
    private readonly List<ChessMove> moveBuffer = new List<ChessMove>(64);
    private readonly List<int> squareBuffer = new List<int>(16);
    private readonly List<ChessPieceShell> lastStandShells = new List<ChessPieceShell>(16);
    private readonly List<ChessPieceShell> duelShells = new List<ChessPieceShell>(2);

    // Re-entrancy guard
    private bool isApplyingMove;

    // ======================================================
    // READ-ONLY STATE ACCESS
    // ======================================================

    /// <summary>Rules state. Read it freely; never mutate it from outside this class.</summary>
    public ChessPosition Position => position;

    public bool whiteToMove => position.WhiteToMove;

    public bool IsApplyingMove => isApplyingMove;

    /// <summary>Increments on every change of the board (move, pass, take-back, removal, init).</summary>
    public int StateVersion { get; private set; }

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
        lastStandShells.Clear();
        duelShells.Clear();
        StateVersion++;

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
            StateVersion++;

            highlight?.ClearTransient();
            highlight?.ClearCheck();
            highlight?.ClearFailedAttack();

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
        StateVersion++;

        highlight?.ClearTransient();
        highlight?.ClearFailedAttack();
        RefreshCheckHighlight();
        return true;
    }

    // ======================================================
    // DUEL PRESENTATION
    // ======================================================

    /// <summary>Before a duel: attacker and defender outlined, their squares marked.</summary>
    public void ShowDuelPreview(CombatRequest request)
    {
        ClearDuelPreview();

        OutlinePiece(request.Move.From, duelAttackerColor, duelShells);
        OutlinePiece(request.DefenderSquare, duelDefenderColor, duelShells);

        if (highlight != null)
        {
            highlight.ClearTransient();
            highlight.ShowSingle(request.Move.FromFile, request.Move.FromRank, HighlightType.Selection, isTransient: true);
            highlight.ShowSingle(ChessPosition.FileOf(request.DefenderSquare), ChessPosition.RankOf(request.DefenderSquare),
                HighlightType.CaptureMove, isTransient: true);
        }
    }

    public void ClearDuelPreview()
    {
        HideShells(duelShells);
        highlight?.ClearTransient();
    }

    /// <summary>After a lost duel: the attacker's square and the target stay marked until the next move.</summary>
    public void ShowFailedAttack(CombatRequest request)
    {
        highlight?.ShowFailedAttack(
            SquareToVec(request.Move.From),
            SquareToVec(request.DefenderSquare));
    }

    // ======================================================
    // THREAT MAP
    // ======================================================

    /// <summary>Shows every square controlled by one side (toggled by the player).</summary>
    public void ShowThreatMap(bool attackersWhite)
    {
        if (highlight == null)
            return;

        position.GetAttackedSquares(attackersWhite, squareBuffer);
        var squares = new List<Vector2Int>(squareBuffer.Count);
        foreach (int sq in squareBuffer)
            squares.Add(SquareToVec(sq));
        highlight.ShowThreats(squares);
    }

    public void ClearThreatMap() => highlight?.ClearThreats();

    // ======================================================
    // LAST STAND
    // ======================================================

    public bool CanRevertLastMove => position.TryGetLastMove(out _);

    /// <summary>Builds the Last Stand request for the side to move, which must be checkmated.</summary>
    public LastStandRequest BuildLastStandRequest(bool kingIsHuman)
    {
        bool kingWhite = position.WhiteToMove;
        position.TryFindKing(kingWhite, out int kingSq);
        position.TryGetLastMove(out ChessMove mating);

        position.GetCheckmateParticipants(squareBuffer);
        var participants = new List<LastStandParticipant>(squareBuffer.Count);
        foreach (int sq in squareBuffer)
            participants.Add(new LastStandParticipant(position.GetPiece(sq).Type, sq));

        // Human king -> the AI mated it -> fight everyone at once.
        // AI king -> a human mated it -> the human plays its pieces one by one.
        LastStandMode mode = kingIsHuman ? LastStandMode.Simultaneous : LastStandMode.Sequential;
        return new LastStandRequest(kingWhite, kingSq, kingIsHuman, mode, participants, mating);
    }

    /// <summary>Freeze-frame before the Last Stand: king and every participant outlined + tiles.</summary>
    public void ShowLastStandPreview(LastStandRequest request)
    {
        ClearLastStandPreview();

        var attackers = new List<Vector2Int>(request.Participants.Count);
        foreach (LastStandParticipant p in request.Participants)
        {
            attackers.Add(SquareToVec(p.Square));
            OutlinePiece(p.Square, lastStandAttackerColor, lastStandShells);
        }
        OutlinePiece(request.KingSquare, lastStandKingColor, lastStandShells);

        highlight?.ClearTransient();
        highlight?.ShowLastStand(SquareToVec(request.KingSquare), attackers);
    }

    /// <summary>Recap after a lost Last Stand: surviving participants stay outlined, defeated squares marked.</summary>
    public void ShowLastStandAftermath(LastStandRequest request, LastStandResult result)
    {
        ClearLastStandPreview();

        var survivors = new List<Vector2Int>();
        foreach (LastStandParticipant p in request.Participants)
        {
            if (Contains(result.DefeatedSquares, p.Square))
                continue;
            survivors.Add(SquareToVec(p.Square));
            OutlinePiece(p.Square, lastStandAttackerColor, lastStandShells);
        }
        OutlinePiece(request.KingSquare, lastStandKingColor, lastStandShells);

        if (highlight != null)
        {
            highlight.ShowLastStand(SquareToVec(request.KingSquare), survivors);
            var defeated = new List<Vector2Int>(result.DefeatedSquares.Count);
            foreach (int sq in result.DefeatedSquares)
                defeated.Add(SquareToVec(sq));
            highlight.ShowLastStandDefeated(defeated);
        }
    }

    public void ClearLastStandPreview()
    {
        HideShells(lastStandShells);
        highlight?.ClearLastStand();
    }

    private void OutlinePiece(int sq, Color color, List<ChessPieceShell> track)
    {
        if ((uint)sq >= 64u)
            return;

        ChessPiece piece = pieceObjects[sq];
        if (piece == null)
            return;

        ChessPieceShell shell = piece.GetComponentInChildren<ChessPieceShell>(true);
        if (shell == null)
            return;

        shell.Show(color, lastStandOutlineAlpha);
        track.Add(shell);
    }

    private static void HideShells(List<ChessPieceShell> shells)
    {
        foreach (ChessPieceShell shell in shells)
            if (shell != null)
                shell.Hide();
        shells.Clear();
    }

    private static bool Contains(IReadOnlyList<int> list, int v)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == v) return true;
        return false;
    }

    /// <summary>
    /// King won its Last Stand: the mating move is taken back (animated) and the mating
    /// side loses its turn. A piece captured by that move stays gone; a promotion is undone.
    /// </summary>
    public async Task<bool> RevertLastMoveAsPassAsync()
    {
        if (isApplyingMove)
            return false;

        isApplyingMove = true;

        try
        {
            // Check the objects first: on a desync nothing may change (rules and objects stay in step).
            if (!position.TryGetLastMove(out ChessMove last))
                return false;

            if (pieceObjects[last.To] == null)
            {
                Debug.LogError($"ChessBoardManager: no piece object on {ChessPosition.SquareName(last.To)} to take back {last}. Board desync.", this);
                return false;
            }

            if (!position.TryRevertLastMoveAsPass(out AppliedMove reverted))
                return false;

            StateVersion++;

            ChessMove m = reverted.Move;
            ChessPiece moverObj = pieceObjects[m.To];
            pieceObjects[m.To] = null;

            // Promotion taken back: the queen model becomes a pawn again (spawned where the queen stands).
            if (m.IsPromotion)
                moverObj = SwapModel(moverObj, PieceType.Pawn, m.ToFile, m.ToRank);

            pieceObjects[m.From] = moverObj;
            moverObj.SetBoardCoords(m.FromFile, m.FromRank);

            ChessPiece rookObj = null;
            if (reverted.RookFrom >= 0)
            {
                rookObj = pieceObjects[reverted.RookTo];
                pieceObjects[reverted.RookTo] = null;
                pieceObjects[reverted.RookFrom] = rookObj;
                if (rookObj != null)
                    rookObj.SetBoardCoords(ChessPosition.FileOf(reverted.RookFrom), ChessPosition.RankOf(reverted.RookFrom));
            }

            highlight?.ClearTransient();
            highlight?.ClearCheck();
            highlight?.ClearLastMove();     // the taken-back move never stood
            highlight?.ClearFailedAttack();

            await AnimateTo(moverObj, m.From);
            if (rookObj != null)
                await AnimateTo(rookObj, reverted.RookFrom);

            RefreshCheckHighlight();
            return true;
        }
        finally
        {
            isApplyingMove = false;
        }
    }

    /// <summary>King fell: the participants it defeated are removed from the board (recap only).</summary>
    public async Task RemoveDefeatedPiecesAsync(IReadOnlyList<int> squares)
    {
        var shatters = new List<Task>(squares.Count);

        foreach (int sq in squares)
        {
            if ((uint)sq >= 64u)
                continue;

            // The mating side's king may have fought in the Last Stand, but the winner's
            // king never leaves the board.
            if (position.GetPiece(sq).Type == PieceType.King)
                continue;

            ChessPiece obj = pieceObjects[sq];
            position.RemovePiece(sq);
            pieceObjects[sq] = null;

            if (obj == null)
                continue;

            Vector3 worldPos = obj.transform.position;
            obj.transform.SetParent(null, true);

            // All defeated pieces break at the same time (not one after another).
            if (shatter != null)
            {
                MeshRenderer mr = obj.GetComponentInChildren<MeshRenderer>();
                Material mat = mr != null ? mr.sharedMaterial : null;
                if (obj.gameObject.activeInHierarchy)
                    obj.gameObject.SetActive(false);
                shatters.Add(SafeShatter(worldPos, mat));
            }

            obj.Capture();
        }

        StateVersion++;
        await Task.WhenAll(shatters);
    }

    /// <summary>The shatter effect is cosmetic: its cancellation or failure never breaks a turn.</summary>
    private async Task SafeShatter(Vector3 position, Material material)
    {
        try
        {
            await shatter.ShatterAt(position, material);
        }
        catch (System.OperationCanceledException)
        {
            // effect destroyed (scene teardown)
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("ChessBoardManager: shatter effect failed: " + ex.Message, this);
        }
    }

    private async Task AnimateTo(ChessPiece piece, int sq)
    {
        Vector3 target = grid.BoardToWorld(ChessPosition.FileOf(sq), ChessPosition.RankOf(sq));
        if (moveAnimator != null)
            await moveAnimator.AnimateMove(piece.transform, target);
        else
            piece.transform.position = target;
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
        // i.e. its king can now be captured.
        if (position.TryFindKing(!side, out int ek) && position.IsInCheck(!side))
            highlight.ShowKingInDanger(SquareToVec(ek));
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

                await SafeShatter(r.capturedWorldPos, mr != null ? mr.sharedMaterial : null);
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

        pieceObjects[sq] = SwapModel(pawn, newType, ChessPosition.FileOf(sq), ChessPosition.RankOf(sq));
    }

    /// <summary>
    /// Replaces a piece object by a freshly spawned model of another type at the old object's
    /// world position and the given board coords. Returns the new object (or the old one,
    /// retyped, when the prefab is missing). Does not touch pieceObjects.
    /// </summary>
    private ChessPiece SwapModel(ChessPiece old, PieceType newType, int f, int r)
    {
        ChessPiece fresh = spawner != null ? spawner.SpawnPiece(newType, old.isWhite, f, r) : null;
        if (fresh == null)
        {
            // Logic is already correct in ChessPosition; keep the old model as a fallback.
            Debug.LogWarning($"ChessBoardManager: model for {newType} missing, keeping the {old.type} model.", this);
            old.type = newType;
            return old;
        }

        fresh.SetPosition(old.transform.position, f, r);
        Destroy(old.gameObject);
        return fresh;
    }
}
