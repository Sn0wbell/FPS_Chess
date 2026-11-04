using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// ======================================================
// LAST STAND (pure C#, no UnityEngine)
//
// A checkmate does not end the game right away: the mated king fights, in ONE
// FPS match on ONE map, every piece taking part in the mate.
//   - Mating side is the AI    -> all participants at once   (Simultaneous)
//   - Mating side is a human   -> participants one by one     (Sequential,
//                                 in Participants order); the king keeps its
//                                 health/stacks between opponents and regains
//                                 one lost stack per defeated piece.
// King survives -> the mating move is taken back (a capture it made stands) and
//                  the mating side loses its turn. Participants stay on the board.
// King falls    -> game over (mating side wins); the participants the king
//                  managed to defeat are removed from the board for the recap.
// ======================================================

public enum LastStandMode
{
    Simultaneous,
    Sequential
}

public readonly struct LastStandParticipant
{
    public readonly PieceType Type;
    public readonly int Square;

    public LastStandParticipant(PieceType type, int square)
    {
        Type = type;
        Square = square;
    }

    public override string ToString() => $"{Type}@{ChessPosition.SquareName(Square)}";
}

public sealed class LastStandRequest
{
    public bool KingIsWhite { get; }
    public int KingSquare { get; }
    public bool KingIsHuman { get; }
    public LastStandMode Mode { get; }

    /// <summary>Enemy pieces in the mate. For Sequential this is the fight order.</summary>
    public IReadOnlyList<LastStandParticipant> Participants { get; }

    public ChessMove MatingMove { get; }

    public LastStandRequest(bool kingIsWhite, int kingSquare, bool kingIsHuman, LastStandMode mode,
        IReadOnlyList<LastStandParticipant> participants, ChessMove matingMove)
    {
        KingIsWhite = kingIsWhite;
        KingSquare = kingSquare;
        KingIsHuman = kingIsHuman;
        Mode = mode;
        Participants = participants;
        MatingMove = matingMove;
    }

    public override string ToString() =>
        $"Last Stand: {(KingIsWhite ? "White" : "Black")} king @{ChessPosition.SquareName(KingSquare)} " +
        $"vs {Participants.Count} piece(s) [{string.Join(", ", Participants)}], {Mode}, after {MatingMove.ToUci()}";
}

public sealed class LastStandResult
{
    public bool KingSurvived { get; }

    /// <summary>Squares of the participants the king defeated (all of them when it survived).</summary>
    public IReadOnlyList<int> DefeatedSquares { get; }

    public LastStandResult(bool kingSurvived, IReadOnlyList<int> defeatedSquares)
    {
        KingSurvived = kingSurvived;
        DefeatedSquares = defeatedSquares ?? new int[0];
    }

    public static LastStandResult KingWins(LastStandRequest request)
    {
        var all = new List<int>(request.Participants.Count);
        foreach (LastStandParticipant p in request.Participants)
            all.Add(p.Square);
        return new LastStandResult(true, all);
    }

    public static LastStandResult KingFalls(IReadOnlyList<int> defeatedSquares) =>
        new LastStandResult(false, defeatedSquares);

    public override string ToString() =>
        $"{(KingSurvived ? "king survived" : "king fell")}, defeated {DefeatedSquares.Count}";
}

public interface ILastStandResolver
{
    Task<LastStandResult> ResolveAsync(LastStandRequest request, CancellationToken ct);
}

/// <summary>Plain chess: the mated king always falls (checkmate ends the game).</summary>
public sealed class KingAlwaysFallsResolver : ILastStandResolver
{
    public Task<LastStandResult> ResolveAsync(LastStandRequest request, CancellationToken ct) =>
        Task.FromResult(LastStandResult.KingFalls(new int[0]));
}
