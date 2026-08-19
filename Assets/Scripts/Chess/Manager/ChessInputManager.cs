using UnityEngine;
using System.Threading.Tasks;

public sealed class ChessInputManager : MonoBehaviour
{
    [Header("References")]
    public Camera cam;
    public ChessGrid grid;
    public ChessBoardManager board;
    public ChessGameManager game;
    public BoardHighlightSystem highlight;

    [Header("Raycast")]
    public LayerMask boardRaycastMask;

    // ======================================================
    // STATE (INPUT-LOCAL ONLY)
    // ======================================================

    private Vector2Int? selectedSquare;
    private ChessPiece hoveredPiece;

    private readonly ChessLegalMoveCache legalCache =
        new ChessLegalMoveCache();

    private bool cacheDirty = true;
    private bool lastWhiteToMove;

    // HARD UX LOCK (prevents double intent)
    private bool inputLocked;

    // ======================================================
    // UNITY
    // ======================================================

    private void Awake()
    {
        if (board != null)
            lastWhiteToMove = board.whiteToMove;
    }

    private void Update()
    {
        if (game == null || board == null)
            return;

        // --------------------------------------------------
        // HARD BLOCK: NO INPUT / NO UX WHILE BUSY
        // --------------------------------------------------
        if (!game.CanPlayerInput || inputLocked)
        {
            ForceClearInputState();
            return;
        }

        // --------------------------------------------------
        // TURN CHANGE DETECTION (AI / EXTERNAL APPLY)
        // --------------------------------------------------
        if (board.whiteToMove != lastWhiteToMove)
        {
            lastWhiteToMove = board.whiteToMove;
            ForceClearInputState();
            return;
        }

        // --------------------------------------------------
        // CACHE REBUILD (UX ONLY)
        // --------------------------------------------------
        if (cacheDirty)
        {
            legalCache.Rebuild(board);
            cacheDirty = false;
        }

        // --------------------------------------------------
        // HOVER (SINGLE SOURCE OF TRUTH)
        // --------------------------------------------------
        UpdateHover();

        // --------------------------------------------------
        // CLICK
        // --------------------------------------------------
        if (!Input.GetMouseButtonDown(0))
            return;

        if (!TryGetSquareUnderMouse(out Vector2Int clicked))
        {
            ClearHover();
            return;
        }

        HandleClick(clicked);
    }

    // ======================================================
    // FORCE CLEAR (ABSOLUTE SYNC POINT)
    // ======================================================

    private void ForceClearInputState()
    {
        ClearHover();
        selectedSquare = null;
        cacheDirty = true;
    }

    // ======================================================
    // HOVER (STRICT UX – NO SIDE EFFECT)
    // ======================================================

    private void UpdateHover()
    {
        if (!TryGetSquareUnderMouse(out Vector2Int sq))
        {
            ClearHover();
            return;
        }

        ChessPiece piece = board.GetPieceAt(sq.x, sq.y);
        if (piece == null || !piece.IsActiveOnBoard)
        {
            ClearHover();
            return;
        }

        // Rule 0: never hover selected piece
        if (selectedSquare.HasValue &&
            piece.file == selectedSquare.Value.x &&
            piece.rank == selectedSquare.Value.y)
        {
            ClearHover();
            return;
        }

        bool isOwnPiece = piece.isWhite == board.whiteToMove;

        bool isCaptureTarget =
            selectedSquare.HasValue &&
            !isOwnPiece &&
            legalCache.IsReady &&
            legalCache.IsLegal(selectedSquare.Value, sq);

        // Rule 1: reject invalid hover
        if (!isOwnPiece && !isCaptureTarget)
        {
            ClearHover();
            return;
        }

        // No change
        if (hoveredPiece == piece)
            return;

        ClearHover();

        hoveredPiece = piece;

        ChessPieceShell shell =
            hoveredPiece.GetComponentInChildren<ChessPieceShell>(true);

        if (shell == null)
            return;

        // Rule 2: capture hover has priority
        if (isCaptureTarget)
        {
            shell.Show(Color.red, 0.35f);
        }
        else
        {
            shell.ShowHover();
        }
    }

    private void ClearHover()
    {
        if (hoveredPiece == null)
            return;

        ChessPieceShell shell =
            hoveredPiece.GetComponentInChildren<ChessPieceShell>(true);

        if (shell != null)
            shell.Hide();

        hoveredPiece = null;
    }

    // ======================================================
    // CLICK FLOW
    // ======================================================

    private void HandleClick(Vector2Int sq)
    {
        if (!selectedSquare.HasValue)
        {
            TrySelectSquare(sq);
            return;
        }

        Vector2Int from = selectedSquare.Value;

        if (from == sq)
        {
            ClearSelectionInternal();
            return;
        }

        if (!legalCache.IsReady || !legalCache.IsLegal(from, sq))
        {
            ClearSelectionInternal();
            TrySelectSquare(sq);
            return;
        }

        ClearSelectionInternal();
        _ = TryRequestMoveAsync(from, sq);
    }

    private void TrySelectSquare(Vector2Int sq)
    {
        ChessPiece piece = board.GetPieceAt(sq.x, sq.y);
        if (piece == null || !piece.IsActiveOnBoard)
            return;

        if (piece.isWhite != board.whiteToMove)
            return;

        selectedSquare = sq;
        ShowHighlightsForSelection(sq);
    }

    private void ClearSelectionInternal()
    {
        selectedSquare = null;
        highlight?.ClearTransient();
    }

    // ======================================================
    // HIGHLIGHT (UX ONLY – STRICT CALLER)
    // ======================================================

    private void ShowHighlightsForSelection(Vector2Int from)
    {
        if (highlight == null || !legalCache.IsReady)
            return;

        highlight.ClearTransient();

        highlight.ShowSingle(
            from.x,
            from.y,
            HighlightType.Selection,
            isTransient: true);

        var moves = legalCache.GetMovesFrom(from);
        for (int i = 0; i < moves.Count; i++)
        {
            Vector2Int to = moves[i];
            bool isCapture = legalCache.CanCapture(to);

            highlight.ShowSingle(
                to.x,
                to.y,
                isCapture ? HighlightType.CaptureMove : HighlightType.LegalMove,
                isTransient: true);
        }
    }

    // ======================================================
    // MOVE REQUEST (UX-LOCKED)
    // ======================================================

    private async Task TryRequestMoveAsync(Vector2Int from, Vector2Int to)
    {
        if (game == null)
            return;

        inputLocked = true;
        cacheDirty = true;

        string uci = SquareToUci(from, to);
        await game.OnPlayerMoveRequested(uci);

        inputLocked = false;
    }

    // ======================================================
    // RAYCAST
    // ======================================================

    private bool TryGetSquareUnderMouse(out Vector2Int sq)
    {
        sq = default;

        if (cam == null || grid == null)
            return false;

        Ray ray = cam.ScreenPointToRay(Input.mousePosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, 100f, boardRaycastMask))
            return false;

        if (!grid.WorldToBoard(hit.point, out int f, out int r))
            return false;

        sq = new Vector2Int(f, r);
        return true;
    }

    // ======================================================
    // UCI
    // ======================================================

    private static string SquareToUci(Vector2Int from, Vector2Int to)
    {
        char f1 = (char)('a' + from.x);
        char r1 = (char)('1' + from.y);
        char f2 = (char)('a' + to.x);
        char r2 = (char)('1' + to.y);
        return $"{f1}{r1}{f2}{r2}";
    }
}
