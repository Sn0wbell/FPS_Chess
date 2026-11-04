using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Authoritative chess rules state (pure C#, no UnityEngine).
///
/// Standard FIDE move generation plus the FPS-Chess variant rules:
///  - <see cref="PassTurn"/>: an attacker that lost the FPS duel loses its turn.
///  - A king left in check after such a pass can be captured
///    (<see cref="ChessMoveFlags.KingCapture"/>), which ends the game.
///
/// Square index = rank * 8 + file (a1 = 0, h1 = 7, a8 = 56, h8 = 63).
/// </summary>
public sealed class ChessPosition
{
    public const string StartFen =
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    // 0 = empty, +(type+1) = white piece, -(type+1) = black piece
    private readonly sbyte[] board = new sbyte[64];

    // Repetition keys (placement, side, castling, en passant) of every position reached BY A MOVE.
    // Positions created by a lost duel (PassTurn) are not recorded and never count as repetitions.
    private readonly List<string> history = new List<string>(128);
    private bool currentFromPass;

    // Scratch buffer for internal legal-move queries.
    private readonly List<ChessMove> scratch = new List<ChessMove>(64);

    public bool WhiteToMove { get; private set; }
    public CastlingRights Castling { get; private set; }
    public int EnPassantSquare { get; private set; } = -1;
    public int HalfmoveClock { get; private set; }
    public int FullmoveNumber { get; private set; } = 1;

    private ChessPosition() { }

    // ======================================================
    // CONSTRUCTION
    // ======================================================

    public static ChessPosition CreateStart() => FromFen(StartFen);

    /// <exception cref="FormatException">Invalid FEN.</exception>
    public static ChessPosition FromFen(string fen)
    {
        if (string.IsNullOrWhiteSpace(fen))
            throw new FormatException("Empty FEN.");

        string[] parts = fen.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            throw new FormatException("FEN needs at least 4 fields: " + fen);

        var p = new ChessPosition();

        string[] ranks = parts[0].Split('/');
        if (ranks.Length != 8)
            throw new FormatException("FEN placement must have 8 ranks: " + fen);

        for (int i = 0; i < 8; i++)
        {
            int rank = 7 - i;
            int file = 0;
            foreach (char c in ranks[i])
            {
                if (c >= '1' && c <= '8')
                {
                    file += c - '0';
                    continue;
                }

                PieceType type = CharToPieceType(c);
                if (type == PieceType.None || file > 7)
                    throw new FormatException("Bad FEN placement: " + fen);

                p.board[Square(file, rank)] = Code(type, char.IsUpper(c));
                file++;
            }

            if (file != 8)
                throw new FormatException("Bad FEN rank length: " + fen);
        }

        if (parts[1] == "w") p.WhiteToMove = true;
        else if (parts[1] == "b") p.WhiteToMove = false;
        else throw new FormatException("Bad FEN side to move: " + fen);

        CastlingRights rights = CastlingRights.None;
        if (parts[2] != "-")
        {
            foreach (char c in parts[2])
            {
                switch (c)
                {
                    case 'K': rights |= CastlingRights.WhiteKingSide; break;
                    case 'Q': rights |= CastlingRights.WhiteQueenSide; break;
                    case 'k': rights |= CastlingRights.BlackKingSide; break;
                    case 'q': rights |= CastlingRights.BlackQueenSide; break;
                    default: throw new FormatException("Bad FEN castling: " + fen);
                }
            }
        }
        p.Castling = rights;

        if (parts[3] == "-")
            p.EnPassantSquare = -1;
        else if (!TryParseSquare(parts[3], out int ep))
            throw new FormatException("Bad FEN en passant: " + fen);
        else
            p.EnPassantSquare = ep;

        p.HalfmoveClock = parts.Length > 4 ? int.Parse(parts[4]) : 0;
        p.FullmoveNumber = parts.Length > 5 ? int.Parse(parts[5]) : 1;

        p.history.Add(p.RepetitionKey());
        return p;
    }

    public ChessPosition Clone()
    {
        var c = new ChessPosition
        {
            WhiteToMove = WhiteToMove,
            Castling = Castling,
            EnPassantSquare = EnPassantSquare,
            HalfmoveClock = HalfmoveClock,
            FullmoveNumber = FullmoveNumber,
            currentFromPass = currentFromPass
        };
        Array.Copy(board, c.board, 64);
        c.history.AddRange(history);
        return c;
    }

    // ======================================================
    // SQUARES / PIECES
    // ======================================================

    public static int Square(int file, int rank) => rank * 8 + file;
    public static int FileOf(int sq) => sq & 7;
    public static int RankOf(int sq) => sq >> 3;
    public static bool InBounds(int file, int rank) => (uint)file < 8u && (uint)rank < 8u;

    public static string SquareName(int sq) =>
        $"{(char)('a' + FileOf(sq))}{(char)('1' + RankOf(sq))}";

    public static bool TryParseSquare(string s, out int sq)
    {
        sq = -1;
        if (s == null || s.Length != 2)
            return false;
        int f = s[0] - 'a';
        int r = s[1] - '1';
        if (!InBounds(f, r))
            return false;
        sq = Square(f, r);
        return true;
    }

    public BoardPiece GetPiece(int sq)
    {
        if ((uint)sq >= 64u)
            return BoardPiece.Empty;
        sbyte c = board[sq];
        return c == 0 ? BoardPiece.Empty : new BoardPiece(TypeOf(c), c > 0);
    }

    public BoardPiece GetPiece(int file, int rank) =>
        InBounds(file, rank) ? GetPiece(Square(file, rank)) : BoardPiece.Empty;

    private static sbyte Code(PieceType type, bool white) =>
        (sbyte)(white ? (int)type + 1 : -((int)type + 1));

    private static PieceType TypeOf(sbyte code) =>
        (PieceType)(Math.Abs(code) - 1);

    public static PieceType CharToPieceType(char c)
    {
        switch (char.ToLowerInvariant(c))
        {
            case 'p': return PieceType.Pawn;
            case 'r': return PieceType.Rook;
            case 'n': return PieceType.Knight;
            case 'b': return PieceType.Bishop;
            case 'q': return PieceType.Queen;
            case 'k': return PieceType.King;
            default: return PieceType.None;
        }
    }

    /// <summary>Uppercase letter of a piece type (P, R, N, B, Q, K).</summary>
    public static char PieceTypeToChar(PieceType t)
    {
        switch (t)
        {
            case PieceType.Pawn: return 'P';
            case PieceType.Rook: return 'R';
            case PieceType.Knight: return 'N';
            case PieceType.Bishop: return 'B';
            case PieceType.Queen: return 'Q';
            case PieceType.King: return 'K';
            default: return '?';
        }
    }

    // ======================================================
    // KING / ATTACKS
    // ======================================================

    /// <summary>Returns false when the king of that colour is not on the board (captured).</summary>
    public bool TryFindKing(bool white, out int square)
    {
        sbyte king = Code(PieceType.King, white);
        for (int sq = 0; sq < 64; sq++)
        {
            if (board[sq] == king)
            {
                square = sq;
                return true;
            }
        }
        square = -1;
        return false;
    }

    /// <summary>True when that side's king exists and is attacked.</summary>
    public bool IsInCheck(bool white) =>
        TryFindKing(white, out int k) && IsSquareAttacked(k, !white);

    private static readonly int[] KnightDf = { 1, 2, 2, 1, -1, -2, -2, -1 };
    private static readonly int[] KnightDr = { 2, 1, -1, -2, -2, -1, 1, 2 };
    private static readonly int[] KingDf = { 1, 1, 0, -1, -1, -1, 0, 1 };
    private static readonly int[] KingDr = { 0, 1, 1, 1, 0, -1, -1, -1 };
    private static readonly int[] RookDf = { 1, -1, 0, 0 };
    private static readonly int[] RookDr = { 0, 0, 1, -1 };
    private static readonly int[] BishopDf = { 1, 1, -1, -1 };
    private static readonly int[] BishopDr = { 1, -1, 1, -1 };

    public bool IsSquareAttacked(int sq, bool byWhite)
    {
        int f = FileOf(sq);
        int r = RankOf(sq);

        // Pawns: a white pawn attacks diagonally upwards, so it sits one rank below.
        int pr = byWhite ? r - 1 : r + 1;
        sbyte pawn = Code(PieceType.Pawn, byWhite);
        if (InBounds(f - 1, pr) && board[Square(f - 1, pr)] == pawn) return true;
        if (InBounds(f + 1, pr) && board[Square(f + 1, pr)] == pawn) return true;

        sbyte knight = Code(PieceType.Knight, byWhite);
        for (int i = 0; i < 8; i++)
        {
            int tf = f + KnightDf[i], tr = r + KnightDr[i];
            if (InBounds(tf, tr) && board[Square(tf, tr)] == knight) return true;
        }

        sbyte king = Code(PieceType.King, byWhite);
        for (int i = 0; i < 8; i++)
        {
            int tf = f + KingDf[i], tr = r + KingDr[i];
            if (InBounds(tf, tr) && board[Square(tf, tr)] == king) return true;
        }

        sbyte queen = Code(PieceType.Queen, byWhite);
        sbyte rook = Code(PieceType.Rook, byWhite);
        sbyte bishop = Code(PieceType.Bishop, byWhite);

        if (RayHits(f, r, RookDf, RookDr, rook, queen)) return true;
        if (RayHits(f, r, BishopDf, BishopDr, bishop, queen)) return true;

        return false;
    }

    private bool RayHits(int f, int r, int[] df, int[] dr, sbyte a, sbyte b)
    {
        for (int d = 0; d < df.Length; d++)
        {
            int tf = f + df[d], tr = r + dr[d];
            while (InBounds(tf, tr))
            {
                sbyte c = board[Square(tf, tr)];
                if (c != 0)
                {
                    if (c == a || c == b) return true;
                    break;
                }
                tf += df[d];
                tr += dr[d];
            }
        }
        return false;
    }

    // ======================================================
    // MOVE GENERATION
    // ======================================================

    /// <summary>Fully legal moves for the side to move (clears the list first).</summary>
    public void GenerateLegalMoves(List<ChessMove> moves)
    {
        moves.Clear();
        GeneratePseudoLegalMoves(moves);

        bool white = WhiteToMove;
        int write = 0;
        for (int i = 0; i < moves.Count; i++)
        {
            ChessMove m = moves[i];
            if (IsLegalPseudo(m, white))
                moves[write++] = m;
        }
        moves.RemoveRange(write, moves.Count - write);
    }

    private bool IsLegalPseudo(ChessMove m, bool white)
    {
        // Variant: capturing the enemy king ends the game, so own-king safety is moot.
        if (m.IsKingCapture)
            return true;

        Undo u = MakeRaw(m);
        bool ok = !IsInCheck(white);
        UnmakeRaw(m, u);
        return ok;
    }

    public bool IsLegal(ChessMove move)
    {
        GenerateLegalMoves(scratch);
        return scratch.Contains(move);
    }

    public bool HasAnyLegalMove()
    {
        GenerateLegalMoves(scratch);
        return scratch.Count > 0;
    }

    /// <summary>
    /// Finds the legal move matching a UCI string ("e2e4", "e7e8q").
    /// A promotion without a piece letter defaults to a queen.
    /// </summary>
    public bool TryFindLegalMove(string uci, out ChessMove move)
    {
        move = default;
        if (string.IsNullOrEmpty(uci) || uci.Length < 4)
            return false;

        if (!TryParseSquare(uci.Substring(0, 2), out int from) ||
            !TryParseSquare(uci.Substring(2, 2), out int to))
            return false;

        PieceType promo = PieceType.None;
        if (uci.Length >= 5)
        {
            promo = CharToPieceType(uci[4]);
            if (promo == PieceType.None || promo == PieceType.Pawn || promo == PieceType.King)
                return false;
        }

        return TryFindLegalMove(from, to, promo, out move);
    }

    /// <param name="promotion">None = queen when the move is a promotion.</param>
    public bool TryFindLegalMove(int from, int to, PieceType promotion, out ChessMove move)
    {
        GenerateLegalMoves(scratch);
        PieceType wanted = promotion == PieceType.None ? PieceType.Queen : promotion;

        for (int i = 0; i < scratch.Count; i++)
        {
            ChessMove m = scratch[i];
            if (m.From != from || m.To != to)
                continue;
            if (m.IsPromotion && m.Promotion != wanted)
                continue;
            move = m;
            return true;
        }

        move = default;
        return false;
    }

    public void GeneratePseudoLegalMoves(List<ChessMove> moves)
    {
        bool white = WhiteToMove;

        for (int sq = 0; sq < 64; sq++)
        {
            sbyte c = board[sq];
            if (c == 0 || (c > 0) != white)
                continue;

            switch (TypeOf(c))
            {
                case PieceType.Pawn: AddPawnMoves(moves, sq, white); break;
                case PieceType.Knight: AddSteps(moves, sq, white, KnightDf, KnightDr); break;
                case PieceType.Bishop: AddSlides(moves, sq, white, BishopDf, BishopDr); break;
                case PieceType.Rook: AddSlides(moves, sq, white, RookDf, RookDr); break;
                case PieceType.Queen:
                    AddSlides(moves, sq, white, RookDf, RookDr);
                    AddSlides(moves, sq, white, BishopDf, BishopDr);
                    break;
                case PieceType.King:
                    AddSteps(moves, sq, white, KingDf, KingDr);
                    AddCastling(moves, sq, white);
                    break;
            }
        }
    }

    private ChessMoveFlags CaptureFlags(sbyte target)
    {
        ChessMoveFlags f = ChessMoveFlags.Capture;
        if (TypeOf(target) == PieceType.King)
            f |= ChessMoveFlags.KingCapture;
        return f;
    }

    private void AddPawnMoves(List<ChessMove> moves, int from, bool white)
    {
        int f = FileOf(from);
        int r = RankOf(from);
        int dir = white ? 1 : -1;
        int startRank = white ? 1 : 6;

        // Forward 1, then forward 2 only if BOTH squares are empty (fix: no jumping over a blocker).
        int r1 = r + dir;
        if (InBounds(f, r1) && board[Square(f, r1)] == 0)
        {
            AddPawnMove(moves, from, Square(f, r1), ChessMoveFlags.None);

            int r2 = r + 2 * dir;
            if (r == startRank && board[Square(f, r2)] == 0)
                moves.Add(new ChessMove(from, Square(f, r2), ChessMoveFlags.DoublePush));
        }

        // Captures (incl. en passant)
        for (int df = -1; df <= 1; df += 2)
        {
            int tf = f + df;
            if (!InBounds(tf, r1))
                continue;

            int to = Square(tf, r1);
            sbyte t = board[to];

            if (t != 0)
            {
                if ((t > 0) != white)
                    AddPawnMove(moves, from, to, CaptureFlags(t));
            }
            else if (to == EnPassantSquare)
            {
                sbyte victim = board[Square(tf, r)];
                if (victim == Code(PieceType.Pawn, !white))
                    moves.Add(new ChessMove(from, to, ChessMoveFlags.Capture | ChessMoveFlags.EnPassant));
            }
        }
    }

    private static void AddPawnMove(List<ChessMove> moves, int from, int to, ChessMoveFlags flags)
    {
        int toRank = RankOf(to);
        if (toRank == 0 || toRank == 7)
        {
            flags |= ChessMoveFlags.Promotion;
            moves.Add(new ChessMove(from, to, flags, PieceType.Queen));
            moves.Add(new ChessMove(from, to, flags, PieceType.Rook));
            moves.Add(new ChessMove(from, to, flags, PieceType.Bishop));
            moves.Add(new ChessMove(from, to, flags, PieceType.Knight));
        }
        else
        {
            moves.Add(new ChessMove(from, to, flags));
        }
    }

    private void AddSteps(List<ChessMove> moves, int from, bool white, int[] df, int[] dr)
    {
        int f = FileOf(from), r = RankOf(from);
        for (int i = 0; i < df.Length; i++)
        {
            int tf = f + df[i], tr = r + dr[i];
            if (!InBounds(tf, tr))
                continue;

            int to = Square(tf, tr);
            sbyte t = board[to];
            if (t == 0)
                moves.Add(new ChessMove(from, to, ChessMoveFlags.None));
            else if ((t > 0) != white)
                moves.Add(new ChessMove(from, to, CaptureFlags(t)));
        }
    }

    private void AddSlides(List<ChessMove> moves, int from, bool white, int[] df, int[] dr)
    {
        int f = FileOf(from), r = RankOf(from);
        for (int d = 0; d < df.Length; d++)
        {
            int tf = f + df[d], tr = r + dr[d];
            while (InBounds(tf, tr))
            {
                int to = Square(tf, tr);
                sbyte t = board[to];
                if (t == 0)
                {
                    moves.Add(new ChessMove(from, to, ChessMoveFlags.None));
                }
                else
                {
                    if ((t > 0) != white)
                        moves.Add(new ChessMove(from, to, CaptureFlags(t)));
                    break;
                }
                tf += df[d];
                tr += dr[d];
            }
        }
    }

    private void AddCastling(List<ChessMove> moves, int from, bool white)
    {
        int rank = white ? 0 : 7;
        if (from != Square(4, rank))
            return;

        CastlingRights ks = white ? CastlingRights.WhiteKingSide : CastlingRights.BlackKingSide;
        CastlingRights qs = white ? CastlingRights.WhiteQueenSide : CastlingRights.BlackQueenSide;
        if ((Castling & (ks | qs)) == 0)
            return;

        if (IsSquareAttacked(from, !white))
            return; // cannot castle out of check

        sbyte rook = Code(PieceType.Rook, white);

        // King side: rook must really be on h-file (fix: never castle with a missing rook)
        if ((Castling & ks) != 0 &&
            board[Square(7, rank)] == rook &&
            board[Square(5, rank)] == 0 &&
            board[Square(6, rank)] == 0 &&
            !IsSquareAttacked(Square(5, rank), !white) &&
            !IsSquareAttacked(Square(6, rank), !white))
        {
            moves.Add(new ChessMove(from, Square(6, rank), ChessMoveFlags.Castle));
        }

        if ((Castling & qs) != 0 &&
            board[Square(0, rank)] == rook &&
            board[Square(1, rank)] == 0 &&
            board[Square(2, rank)] == 0 &&
            board[Square(3, rank)] == 0 &&
            !IsSquareAttacked(Square(3, rank), !white) &&
            !IsSquareAttacked(Square(2, rank), !white))
        {
            moves.Add(new ChessMove(from, Square(2, rank), ChessMoveFlags.Castle));
        }
    }

    // ======================================================
    // MAKE / UNMAKE (internal, used for legality + perft)
    // ======================================================

    private struct Undo
    {
        public sbyte Captured;
        public int CapturedSquare;
        public CastlingRights Castling;
        public int EnPassant;
        public int Halfmove;
        public int Fullmove;
        public bool WhiteToMove;
    }

    private Undo MakeRaw(ChessMove m)
    {
        var u = new Undo
        {
            Captured = 0,
            CapturedSquare = -1,
            Castling = Castling,
            EnPassant = EnPassantSquare,
            Halfmove = HalfmoveClock,
            Fullmove = FullmoveNumber,
            WhiteToMove = WhiteToMove
        };

        sbyte mover = board[m.From];
        bool white = mover > 0;
        PieceType type = TypeOf(mover);

        if (m.IsEnPassant)
        {
            int capSq = Square(m.ToFile, m.FromRank);
            u.Captured = board[capSq];
            u.CapturedSquare = capSq;
            board[capSq] = 0;
        }
        else if (board[m.To] != 0)
        {
            u.Captured = board[m.To];
            u.CapturedSquare = m.To;
        }

        board[m.To] = m.IsPromotion ? Code(m.Promotion, white) : mover;
        board[m.From] = 0;

        if (m.IsCastle)
        {
            int rank = m.FromRank;
            bool kingSide = m.ToFile == 6;
            int rf = Square(kingSide ? 7 : 0, rank);
            int rt = Square(kingSide ? 5 : 3, rank);
            board[rt] = board[rf];
            board[rf] = 0;
        }

        // Castling rights: king moves, or anything leaves/lands on a rook home square.
        CastlingRights rights = Castling;
        if (type == PieceType.King)
            rights &= white
                ? ~(CastlingRights.WhiteKingSide | CastlingRights.WhiteQueenSide)
                : ~(CastlingRights.BlackKingSide | CastlingRights.BlackQueenSide);
        rights &= ~RightsTouchedBy(m.From);
        rights &= ~RightsTouchedBy(m.To);
        Castling = rights;

        EnPassantSquare = (m.Flags & ChessMoveFlags.DoublePush) != 0
            ? Square(m.FromFile, (m.FromRank + m.ToRank) / 2)
            : -1;

        HalfmoveClock = (type == PieceType.Pawn || u.Captured != 0) ? 0 : HalfmoveClock + 1;
        if (!white)
            FullmoveNumber++;

        WhiteToMove = !WhiteToMove;
        return u;
    }

    private void UnmakeRaw(ChessMove m, Undo u)
    {
        bool white = u.WhiteToMove;
        sbyte moved = board[m.To];
        board[m.From] = m.IsPromotion ? Code(PieceType.Pawn, white) : moved;
        board[m.To] = 0;

        if (u.CapturedSquare >= 0)
            board[u.CapturedSquare] = u.Captured; // en passant: restores the pawn on ITS square

        if (m.IsCastle)
        {
            int rank = m.FromRank;
            bool kingSide = m.ToFile == 6;
            int rf = Square(kingSide ? 7 : 0, rank);
            int rt = Square(kingSide ? 5 : 3, rank);
            board[rf] = board[rt];
            board[rt] = 0;
        }

        Castling = u.Castling;
        EnPassantSquare = u.EnPassant;
        HalfmoveClock = u.Halfmove;
        FullmoveNumber = u.Fullmove;
        WhiteToMove = u.WhiteToMove;
    }

    private static CastlingRights RightsTouchedBy(int sq)
    {
        switch (sq)
        {
            case 0: return CastlingRights.WhiteQueenSide;  // a1
            case 7: return CastlingRights.WhiteKingSide;   // h1
            case 56: return CastlingRights.BlackQueenSide; // a8
            case 63: return CastlingRights.BlackKingSide;  // h8
            default: return CastlingRights.None;
        }
    }

    // ======================================================
    // PUBLIC STATE CHANGES
    // ======================================================

    /// <summary>Applies a legal move. Returns false (and changes nothing) when it is not legal.</summary>
    public bool TryApply(ChessMove move, out AppliedMove applied)
    {
        applied = default;
        if (!IsLegal(move))
            return false;

        BoardPiece mover = GetPiece(move.From);
        int rookFrom = -1, rookTo = -1;
        if (move.IsCastle)
        {
            bool kingSide = move.ToFile == 6;
            rookFrom = Square(kingSide ? 7 : 0, move.FromRank);
            rookTo = Square(kingSide ? 5 : 3, move.FromRank);
        }

        Undo u = MakeRaw(move);
        BoardPiece captured = u.Captured == 0
            ? BoardPiece.Empty
            : new BoardPiece(TypeOf(u.Captured), u.Captured > 0);

        history.Add(RepetitionKey());
        currentFromPass = false;
        applied = new AppliedMove(move, mover, captured, u.CapturedSquare, rookFrom, rookTo);
        return true;
    }

    /// <summary>
    /// FPS-Chess: the side to move attempted a capture and lost the duel.
    /// Nothing moves; the turn goes to the opponent. Counts as a non-capture
    /// move for the 50-move rule, but the resulting position is NOT counted for
    /// threefold repetition (two failed attacks in a row would otherwise repeat the board).
    /// </summary>
    public void PassTurn()
    {
        EnPassantSquare = -1;
        HalfmoveClock++;
        if (!WhiteToMove)
            FullmoveNumber++;
        WhiteToMove = !WhiteToMove;
        currentFromPass = true;
    }

    // ======================================================
    // GAME RESULT
    // ======================================================

    public int RepetitionCount
    {
        get
        {
            if (currentFromPass)
                return 1; // reached by a pass: never a repetition
            string current = RepetitionKey();
            int n = 0;
            for (int i = 0; i < history.Count; i++)
                if (history[i] == current) n++;
            return n;
        }
    }

    public GameResult GetResult()
    {
        bool whiteKing = TryFindKing(true, out _);
        bool blackKing = TryFindKing(false, out _);
        if (!whiteKing && blackKing)
            return new GameResult(GameOutcome.BlackWins, GameEndReason.KingCaptured);
        if (!blackKing && whiteKing)
            return new GameResult(GameOutcome.WhiteWins, GameEndReason.KingCaptured);

        if (!HasAnyLegalMove())
        {
            if (IsInCheck(WhiteToMove))
                return new GameResult(
                    WhiteToMove ? GameOutcome.BlackWins : GameOutcome.WhiteWins,
                    GameEndReason.Checkmate);
            return new GameResult(GameOutcome.Draw, GameEndReason.Stalemate);
        }

        if (IsInsufficientMaterial())
            return new GameResult(GameOutcome.Draw, GameEndReason.InsufficientMaterial);

        if (HalfmoveClock >= 100)
            return new GameResult(GameOutcome.Draw, GameEndReason.FiftyMoveRule);

        if (RepetitionCount >= 3)
            return new GameResult(GameOutcome.Draw, GameEndReason.ThreefoldRepetition);

        return GameResult.Ongoing;
    }

    /// <summary>
    /// Dead positions: K v K, K+minor v K, and any number of bishops all on one square colour.
    /// </summary>
    public bool IsInsufficientMaterial()
    {
        int minors = 0, knights = 0, lightBishops = 0, darkBishops = 0;

        for (int sq = 0; sq < 64; sq++)
        {
            sbyte c = board[sq];
            if (c == 0) continue;

            switch (TypeOf(c))
            {
                case PieceType.King:
                    break;
                case PieceType.Knight:
                    minors++; knights++;
                    break;
                case PieceType.Bishop:
                    minors++;
                    if (((FileOf(sq) + RankOf(sq)) & 1) == 0) darkBishops++;
                    else lightBishops++;
                    break;
                default:
                    return false; // pawn, rook or queen
            }
        }

        if (minors <= 1) return true;
        if (knights == 0 && (lightBishops == 0 || darkBishops == 0)) return true;
        return false;
    }

    // ======================================================
    // FEN
    // ======================================================

    public string ToFen() =>
        $"{PlacementFen()} {(WhiteToMove ? "w" : "b")} {CastlingFen()} {EnPassantFen()} {HalfmoveClock} {FullmoveNumber}";

    private string RepetitionKey() =>
        $"{PlacementFen()} {(WhiteToMove ? "w" : "b")} {CastlingFen()} {EnPassantFen()}";

    private string PlacementFen()
    {
        var sb = new StringBuilder(72);
        for (int rank = 7; rank >= 0; rank--)
        {
            int empty = 0;
            for (int file = 0; file < 8; file++)
            {
                sbyte c = board[Square(file, rank)];
                if (c == 0)
                {
                    empty++;
                    continue;
                }
                if (empty > 0)
                {
                    sb.Append(empty);
                    empty = 0;
                }
                char ch = PieceTypeToChar(TypeOf(c));
                sb.Append(c > 0 ? ch : char.ToLowerInvariant(ch));
            }
            if (empty > 0) sb.Append(empty);
            if (rank > 0) sb.Append('/');
        }
        return sb.ToString();
    }

    private string CastlingFen()
    {
        string s = "";
        if ((Castling & CastlingRights.WhiteKingSide) != 0) s += "K";
        if ((Castling & CastlingRights.WhiteQueenSide) != 0) s += "Q";
        if ((Castling & CastlingRights.BlackKingSide) != 0) s += "k";
        if ((Castling & CastlingRights.BlackQueenSide) != 0) s += "q";
        return s.Length == 0 ? "-" : s;
    }

    private string EnPassantFen() =>
        EnPassantSquare < 0 ? "-" : SquareName(EnPassantSquare);

    // ======================================================
    // DEBUG / TEST
    // ======================================================

    /// <summary>Counts leaf nodes of the legal move tree (standard perft).</summary>
    public long Perft(int depth)
    {
        if (depth <= 0) return 1;

        var moves = new List<ChessMove>(64);
        GenerateLegalMoves(moves);
        if (depth == 1) return moves.Count;

        long n = 0;
        foreach (ChessMove m in moves)
        {
            Undo u = MakeRaw(m);
            n += Perft(depth - 1);
            UnmakeRaw(m, u);
        }
        return n;
    }
}
