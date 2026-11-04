using System;

// ======================================================
// PURE CHESS TYPES (no UnityEngine dependency)
// Shared by the rules core, the Unity managers and the
// out-of-Unity logic tests (Tools/ChessLogicTests).
// ======================================================

public enum PieceType
{
    Pawn,
    Rook,
    Knight,
    Bishop,
    Queen,
    King,
    None
}

[Flags]
public enum CastlingRights : byte
{
    None = 0,
    WhiteKingSide = 1,
    WhiteQueenSide = 2,
    BlackKingSide = 4,
    BlackQueenSide = 8,
    All = WhiteKingSide | WhiteQueenSide | BlackKingSide | BlackQueenSide
}

[Flags]
public enum ChessMoveFlags : byte
{
    None = 0,
    Capture = 1,      // any capture, including en passant and king capture
    EnPassant = 2,
    Castle = 4,
    DoublePush = 8,
    Promotion = 16,
    KingCapture = 32  // FPS-Chess variant: capturing a king left in check (direction A)
}

/// <summary>Square index = rank * 8 + file (a1 = 0, h8 = 63).</summary>
public readonly struct ChessMove : IEquatable<ChessMove>
{
    public readonly int From;
    public readonly int To;
    public readonly PieceType Promotion;
    public readonly ChessMoveFlags Flags;

    public ChessMove(int from, int to, ChessMoveFlags flags, PieceType promotion = PieceType.None)
    {
        From = from;
        To = to;
        Flags = flags;
        Promotion = promotion;
    }

    public int FromFile => From & 7;
    public int FromRank => From >> 3;
    public int ToFile => To & 7;
    public int ToRank => To >> 3;

    public bool IsCapture => (Flags & ChessMoveFlags.Capture) != 0;
    public bool IsEnPassant => (Flags & ChessMoveFlags.EnPassant) != 0;
    public bool IsCastle => (Flags & ChessMoveFlags.Castle) != 0;
    public bool IsPromotion => (Flags & ChessMoveFlags.Promotion) != 0;
    public bool IsKingCapture => (Flags & ChessMoveFlags.KingCapture) != 0;

    public string ToUci()
    {
        string s = ChessPosition.SquareName(From) + ChessPosition.SquareName(To);
        if (IsPromotion)
            s += char.ToLowerInvariant(ChessPosition.PieceTypeToChar(Promotion));
        return s;
    }

    public bool Equals(ChessMove other) =>
        From == other.From && To == other.To &&
        Promotion == other.Promotion && Flags == other.Flags;

    public override bool Equals(object obj) => obj is ChessMove m && Equals(m);

    public override int GetHashCode() =>
        From | (To << 6) | ((int)Promotion << 12) | ((int)Flags << 16);

    public override string ToString() => ToUci();
}

public readonly struct BoardPiece
{
    public readonly PieceType Type;
    public readonly bool IsWhite;

    public BoardPiece(PieceType type, bool isWhite)
    {
        Type = type;
        IsWhite = isWhite;
    }

    public bool IsEmpty => Type == PieceType.None;

    public static readonly BoardPiece Empty = new BoardPiece(PieceType.None, false);

    public override string ToString() =>
        IsEmpty ? "empty" : $"{(IsWhite ? "White" : "Black")} {Type}";
}

/// <summary>Visual-relevant facts about a move that was applied to a position.</summary>
public readonly struct AppliedMove
{
    public readonly ChessMove Move;
    public readonly BoardPiece Mover;
    public readonly BoardPiece Captured;
    public readonly int CapturedSquare; // -1 when nothing was captured
    public readonly int RookFrom;       // -1 when not castling
    public readonly int RookTo;

    public AppliedMove(ChessMove move, BoardPiece mover, BoardPiece captured,
        int capturedSquare, int rookFrom, int rookTo)
    {
        Move = move;
        Mover = mover;
        Captured = captured;
        CapturedSquare = capturedSquare;
        RookFrom = rookFrom;
        RookTo = rookTo;
    }
}

public enum GameOutcome
{
    Ongoing,
    WhiteWins,
    BlackWins,
    Draw
}

public enum GameEndReason
{
    None,
    Checkmate,
    KingCaptured,
    Stalemate,
    FiftyMoveRule,
    ThreefoldRepetition,
    InsufficientMaterial
}

public readonly struct GameResult
{
    public readonly GameOutcome Outcome;
    public readonly GameEndReason Reason;

    public GameResult(GameOutcome outcome, GameEndReason reason)
    {
        Outcome = outcome;
        Reason = reason;
    }

    public bool IsOver => Outcome != GameOutcome.Ongoing;

    public static readonly GameResult Ongoing =
        new GameResult(GameOutcome.Ongoing, GameEndReason.None);

    public override string ToString() =>
        IsOver ? $"{Outcome} ({Reason})" : "Ongoing";
}
