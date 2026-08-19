using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// ======================================================
// MOVE SNAPSHOT
// ======================================================
public struct MoveResult
{
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

public sealed class ChessBoardManager : MonoBehaviour
{
    [Header("References")]
    public ChessGrid grid;
    public ChessPieceSpawner spawner;
    public MoveAnimationController moveAnimator;
    public BrokenShatterGenerator shatter;

    [Header("Highlight")]
    public BoardHighlightSystem highlight;

    // ======================================================
    // BOARD STATE (AUTHORITATIVE)
    // ======================================================
    private readonly ChessPiece[,] board = new ChessPiece[8, 8];

    public bool whiteToMove { get; private set; } = true;

    // Castling rights
    private bool whiteKingMoved;
    private bool blackKingMoved;
    private bool whiteRookA1Moved;
    private bool whiteRookH1Moved;
    private bool blackRookA8Moved;
    private bool blackRookH8Moved;

    // En-passant (valid for ONE turn)
    private int enPassantFile = -1;
    private int enPassantRank = -1;

    // FEN counters
    private int halfmoveClock = 0;
    private int fullmoveNumber = 1;

    // Re-entrancy guard
    private bool isApplyingMove;

    // ======================================================
    // READ-ONLY STATE ACCESS
    // ======================================================
    public int EnPassantFile => enPassantFile;
    public int EnPassantRank => enPassantRank;

    public bool WhiteKingMoved => whiteKingMoved;
    public bool BlackKingMoved => blackKingMoved;
    public bool WhiteRookA1Moved => whiteRookA1Moved;
    public bool WhiteRookH1Moved => whiteRookH1Moved;
    public bool BlackRookA8Moved => blackRookA8Moved;
    public bool BlackRookH8Moved => blackRookH8Moved;

    // ======================================================
    // INIT
    // ======================================================
    public void InitializeBoard()
    {
        System.Array.Clear(board, 0, board.Length);

        List<ChessPiece> pieces = spawner.SpawnAllPieces();
        foreach (var p in pieces)
        {
            board[p.file, p.rank] = p;
            p.SetPosition(grid.BoardToWorld(p.file, p.rank), p.file, p.rank);
        }

        whiteToMove = true;
        enPassantFile = -1;
        enPassantRank = -1;

        halfmoveClock = 0;
        fullmoveNumber = 1;

        whiteKingMoved = false;
        blackKingMoved = false;
        whiteRookA1Moved = false;
        whiteRookH1Moved = false;
        blackRookA8Moved = false;
        blackRookH8Moved = false;

        isApplyingMove = false;

        if (highlight != null)
            highlight.ClearAll();
    }

    // ======================================================
    // PUBLIC ENTRY (ONE TRUE ENTRY)
    // ======================================================
    public async Task<bool> ApplyMove(string uci)
    {
        if (isApplyingMove)
            return false;

        isApplyingMove = true;

        try
        {
            if (!TryCommitMove(uci, out MoveResult result))
                return false;

            // ==================================================
            // CLEAR TRANSIENT ONLY — KEEP PERSISTENT
            // ==================================================
            if (highlight != null)
            {
                highlight.ClearTransient();
                highlight.ClearCheck();
            }

            // ==================================================
            // PLAY VISUALS (BOARD STATE ALREADY COMMITTED)
            // ==================================================
            await PlayMoveVisuals(result);

            // ==================================================
            // LAST MOVE (PERSISTENT — ALWAYS AFTER ANIMATION)
            // ==================================================
            highlight?.ShowLastMove(result.from, result.to);

            // ==================================================
            // CHECK / CHECKMATE (MUST NOT CLEAR LAST MOVE)
            // ==================================================
            EvaluateCheckHighlight();

            return true;
        }
        finally
        {
            isApplyingMove = false;
        }
    }

    // ======================================================
    // MOVE LOGIC (ENGINE-TRUSTED)
    // ======================================================
    private bool TryCommitMove(string uci, out MoveResult result)
    {
        result = default;

        if (string.IsNullOrEmpty(uci) || uci.Length < 4)
            return false;

        int ff = uci[0] - 'a';
        int fr = uci[1] - '1';
        int tf = uci[2] - 'a';
        int tr = uci[3] - '1';

        if (!InBounds(ff, fr) || !InBounds(tf, tr))
            return false;

        ChessPiece mover = board[ff, fr];
        if (mover == null || mover.isWhite != whiteToMove)
            return false;

        if (!IsMoveKingSafe(uci))
            return false;

        ChessPiece captured = board[tf, tr];

        result = new MoveResult
        {
            mover = mover,
            from = new Vector2Int(ff, fr),
            to = new Vector2Int(tf, tr),
            captured = captured,
            capturedWorldPos = captured != null ? captured.transform.position : Vector3.zero,
            isCastling = false,
            rook = null
        };

        int prevEpFile = enPassantFile;
        int prevEpRank = enPassantRank;
        enPassantFile = -1;
        enPassantRank = -1;

        if (mover.type == PieceType.Pawn || captured != null)
            halfmoveClock = 0;
        else
            halfmoveClock++;

        // EN PASSANT
        if (mover.type == PieceType.Pawn &&
            ff != tf &&
            captured == null &&
            tf == prevEpFile &&
            tr == prevEpRank)
        {
            int capRank = mover.isWhite ? tr - 1 : tr + 1;
            ChessPiece ep = board[tf, capRank];
            if (ep != null)
            {
                board[tf, capRank] = null;
                result.captured = ep;
                result.capturedWorldPos = ep.transform.position;
            }
        }

        // CASTLING
        if (mover.type == PieceType.King && Mathf.Abs(tf - ff) == 2)
        {
            bool kingSide = tf > ff;
            int rookFromFile = kingSide ? 7 : 0;
            int rookToFile = kingSide ? 5 : 3;

            ChessPiece rook = board[rookFromFile, fr];
            board[rookFromFile, fr] = null;
            board[rookToFile, fr] = rook;
            rook.SetBoardCoords(rookToFile, fr);

            result.isCastling = true;
            result.rook = rook;
            result.rookFrom = new Vector2Int(rookFromFile, fr);
            result.rookTo = new Vector2Int(rookToFile, fr);
        }

        // CASTLING RIGHTS
        if (mover.type == PieceType.King)
        {
            if (mover.isWhite) whiteKingMoved = true;
            else blackKingMoved = true;
        }

        if (mover.type == PieceType.Rook)
        {
            if (mover.isWhite)
            {
                if (ff == 0 && fr == 0) whiteRookA1Moved = true;
                if (ff == 7 && fr == 0) whiteRookH1Moved = true;
            }
            else
            {
                if (ff == 0 && fr == 7) blackRookA8Moved = true;
                if (ff == 7 && fr == 7) blackRookH8Moved = true;
            }
        }

        if (captured != null && captured.type == PieceType.Rook)
        {
            if (captured.isWhite)
            {
                if (tf == 0 && tr == 0) whiteRookA1Moved = true;
                if (tf == 7 && tr == 0) whiteRookH1Moved = true;
            }
            else
            {
                if (tf == 0 && tr == 7) blackRookA8Moved = true;
                if (tf == 7 && tr == 7) blackRookH8Moved = true;
            }
        }

        // REMOVE CAPTURED FROM BOARD (LOGIC)
        if (result.captured != null)
            board[result.captured.file, result.captured.rank] = null;

        board[ff, fr] = null;
        board[tf, tr] = mover;
        mover.SetBoardCoords(tf, tr);

        if (mover.type == PieceType.Pawn && Mathf.Abs(tr - fr) == 2)
        {
            enPassantFile = ff;
            enPassantRank = (fr + tr) / 2;
        }

        if (mover.type == PieceType.Pawn && (tr == 0 || tr == 7))
        {
            char promo = uci.Length >= 5 ? uci[4] : 'q';
            PromotePiece(mover, promo);
        }

        if (!whiteToMove)
            fullmoveNumber++;

        whiteToMove = !whiteToMove;
        return true;
    }

    // ======================================================
    // KING SAFETY (SIMULATION)
    // ======================================================
    private struct SimulationSnapshot
    {
        public ChessPiece captured;
        public int epFile, epRank;
        public bool whiteToMove;
    }

    private bool IsMoveKingSafe(string uci)
    {
        int ff = uci[0] - 'a';
        int fr = uci[1] - '1';
        int tf = uci[2] - 'a';
        int tr = uci[3] - '1';

        ChessPiece mover = board[ff, fr];

        SimulationSnapshot snap = SimulateMove(ff, fr, tf, tr);

        // King belongs to mover
        bool kingColor = mover.isWhite;
        Vector2Int kingSq = FindKingSquare(kingColor);

        // --- FORCE enemy perspective ---
        bool prevTurn = whiteToMove;
        whiteToMove = !kingColor;

        ChessLegalMoveCache enemyCache = new ChessLegalMoveCache();
        enemyCache.Rebuild(this);
        bool inCheck = enemyCache.CanCapture(kingSq);

        // --- RESTORE ---
        whiteToMove = prevTurn;

        UndoSimulate(ff, fr, tf, tr, snap);
        return !inCheck;
    }

    private SimulationSnapshot SimulateMove(int ff, int fr, int tf, int tr)
    {
        SimulationSnapshot s;
        s.captured = board[tf, tr];
        s.epFile = enPassantFile;
        s.epRank = enPassantRank;
        s.whiteToMove = whiteToMove;

        ChessPiece mover = board[ff, fr];

        // En-passant simulation
        if (mover.type == PieceType.Pawn &&
        ff != tf &&
        s.captured == null &&
        tf == enPassantFile &&
        tr == enPassantRank)
        {
            int capRank = mover.isWhite ? tr - 1 : tr + 1;
            s.captured = board[tf, capRank];
            board[tf, capRank] = null;
        }

        board[ff, fr] = null;
        board[tf, tr] = mover;
        mover.SetBoardCoords(tf, tr);

        enPassantFile = -1;
        enPassantRank = -1;
        whiteToMove = !whiteToMove;

        return s;
    }

    private void UndoSimulate(int ff, int fr, int tf, int tr, SimulationSnapshot s)
    {
        ChessPiece mover = board[tf, tr];
        board[tf, tr] = s.captured;

        if (s.captured != null &&
        mover.type == PieceType.Pawn &&
        ff != tf &&
        s.captured.file != tf)
        {
            board[s.captured.file, s.captured.rank] = s.captured;
        }
        board[ff, fr] = mover;
        mover.SetBoardCoords(ff, fr);

        enPassantFile = s.epFile;
        enPassantRank = s.epRank;
        whiteToMove = s.whiteToMove;
    }

    private Vector2Int FindKingSquare(bool white)
    {
        for (int f = 0; f < 8; f++)
            for (int r = 0; r < 8; r++)
            {
                ChessPiece p = board[f, r];
                if (p != null && p.type == PieceType.King && p.isWhite == white)
                    return new Vector2Int(f, r);
            }
        return default;
    }

    // ======================================================
    // CHECK / CHECKMATE (HIGHLIGHT)
    // ======================================================
    private void EvaluateCheckHighlight()
    {
        if (highlight == null)
            return;

        bool sideInTurn = whiteToMove;
        Vector2Int kingSq = FindKingSquare(sideInTurn);

        // Enemy perspective
        whiteToMove = !sideInTurn;
        ChessLegalMoveCache enemyCache = new ChessLegalMoveCache();
        enemyCache.Rebuild(this);
        whiteToMove = sideInTurn;

        bool inCheck = enemyCache.CanCapture(kingSq);
        if (!inCheck)
            return;

        bool hasAnyLegalMove = false;

        ChessLegalMoveCache ownCache = new ChessLegalMoveCache();
        ownCache.Rebuild(this);

        for (int f = 0; f < 8 && !hasAnyLegalMove; f++)
            for (int r = 0; r < 8 && !hasAnyLegalMove; r++)
            {
                ChessPiece p = board[f, r];
                if (p == null || p.isWhite != sideInTurn)
                    continue;

                var moves = ownCache.GetMovesFrom(new Vector2Int(f, r));
                if (moves == null) continue;

                for (int i = 0; i < moves.Count; i++)
                {
                    string uci =
                        $"{(char)('a' + f)}{(char)('1' + r)}" +
                        $"{(char)('a' + moves[i].x)}{(char)('1' + moves[i].y)}";

                    if (IsMoveKingSafe(uci))
                    {
                        hasAnyLegalMove = true;
                        break;
                    }
                }
            }

        // ❗ CHỈ VẼ CHECK / CHECKMATE — KHÔNG CLEAR GÌ KHÁC
        highlight.ShowCheck(kingSq, !hasAnyLegalMove);
    }

    // ======================================================
    // VISUALS (ANIMATION → SHATTER → CAPTURE)
    // ======================================================
    private async Task PlayMoveVisuals(MoveResult r)
    {
        Vector3 target = grid.BoardToWorld(r.to.x, r.to.y);

        // -------------------------
        // MOVE MAIN PIECE
        // -------------------------
        if (moveAnimator != null)
            await moveAnimator.AnimateMove(
                r.mover.transform,
                target,
                r.captured != null ? r.captured.transform : null
            );
        else
            r.mover.transform.position = target;

        // -------------------------
        // CASTLING ROOK
        // -------------------------
        if (r.isCastling && r.rook != null)
        {
            Vector3 rookTarget = grid.BoardToWorld(r.rookTo.x, r.rookTo.y);
            if (moveAnimator != null)
                await moveAnimator.AnimateMove(r.rook.transform, rookTarget);
            else
                r.rook.transform.position = rookTarget;
        }

        // -------------------------
        // CAPTURE (AFTER ALL MOVES)
        // -------------------------
        if (r.captured != null)
        {
            // Detach so it never blocks raycast / hover
            r.captured.transform.SetParent(null, true);

            if (shatter != null)
            {
                MeshRenderer mr =
                    r.captured.GetComponentInChildren<MeshRenderer>();
                if (r.captured.gameObject.activeInHierarchy)
                {
                    r.captured.gameObject.SetActive(false);
                }
                await shatter.ShatterAt(
                    r.capturedWorldPos,
                    mr != null ? mr.sharedMaterial : null
                );
            }

            r.captured.Capture();
        }
    }

    // ======================================================
    // PROMOTION
    // ======================================================
    private static void PromotePiece(ChessPiece piece, char c)
    {
        c = char.ToLower(c);

        PieceType newType =
            c == 'r' ? PieceType.Rook :
            c == 'n' ? PieceType.Knight :
            c == 'b' ? PieceType.Bishop :
            PieceType.Queen;

        if (piece.type == newType)
            return;

        piece.type = newType;

        // Safety: promotion changes move topology
        piece.OnPromoted(); // NO-OP nếu chưa implement
    }

    // ======================================================
    // FEN
    // ======================================================
    public string GenerateFEN()
    {
        string placement = "";

        for (int rank = 7; rank >= 0; rank--)
        {
            int empty = 0;
            for (int file = 0; file < 8; file++)
            {
                ChessPiece p = board[file, rank];
                if (p == null)
                {
                    empty++;
                    continue;
                }

                if (empty > 0)
                {
                    placement += empty;
                    empty = 0;
                }

                placement += PieceToFenChar(p);
            }

            if (empty > 0)
                placement += empty;

            if (rank > 0)
                placement += "/";
        }

        string active = whiteToMove ? "w" : "b";
        string castling = GenerateCastlingRights();
        string enp = GenerateEnPassantSquare();

        return $"{placement} {active} {castling} {enp} {halfmoveClock} {fullmoveNumber}";
    }

    private static char PieceToFenChar(ChessPiece p)
    {
        char c =
            p.type == PieceType.Pawn ? 'p' :
            p.type == PieceType.Rook ? 'r' :
            p.type == PieceType.Knight ? 'n' :
            p.type == PieceType.Bishop ? 'b' :
            p.type == PieceType.Queen ? 'q' : 'k';

        return p.isWhite ? char.ToUpper(c) : c;
    }

    private string GenerateCastlingRights()
    {
        string s = "";

        if (!whiteKingMoved)
        {
            if (!whiteRookH1Moved) s += "K";
            if (!whiteRookA1Moved) s += "Q";
        }

        if (!blackKingMoved)
        {
            if (!blackRookH8Moved) s += "k";
            if (!blackRookA8Moved) s += "q";
        }

        return s == "" ? "-" : s;
    }

    private string GenerateEnPassantSquare()
    {
        if (enPassantFile < 0)
            return "-";

        return $"{(char)('a' + enPassantFile)}{(char)('1' + enPassantRank)}";
    }

    // ======================================================
    // QUERY / UTIL
    // ======================================================
    public ChessPiece GetPieceAt(int file, int rank)
    {
        return InBounds(file, rank) ? board[file, rank] : null;
    }

    private static bool InBounds(int f, int r) =>
        (uint)f < 8 && (uint)r < 8;
}
