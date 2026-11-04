using System.Threading;
using System.Threading.Tasks;

// ======================================================
// CHESS <-> FPS BRIDGE (pure C#, no UnityEngine)
//
// Every capture move is a duel. ChessGameManager asks an ICombatResolver
// who wins before the move is committed:
//   AttackerWins -> the capture is committed on the board.
//   DefenderWins -> nothing moves, the attacker loses its turn (PassTurn).
//
// The real implementation will load the FPS arena with the two pieces and
// await ArenaController.OnMatchEnd. Until then use AttackerAlwaysWinsResolver
// (plain chess) or DebugCombatResolver (inspector-driven).
// ======================================================

public enum CombatOutcome
{
    AttackerWins,
    DefenderWins
}

public readonly struct CombatRequest
{
    public readonly ChessMove Move;

    public readonly PieceType AttackerType;
    public readonly bool AttackerIsWhite;
    public readonly bool AttackerIsHuman;

    public readonly PieceType DefenderType;
    public readonly bool DefenderIsHuman;

    /// <summary>Square of the defending piece (differs from Move.To for en passant).</summary>
    public readonly int DefenderSquare;

    public CombatRequest(ChessMove move,
        PieceType attackerType, bool attackerIsWhite, bool attackerIsHuman,
        PieceType defenderType, bool defenderIsHuman, int defenderSquare)
    {
        Move = move;
        AttackerType = attackerType;
        AttackerIsWhite = attackerIsWhite;
        AttackerIsHuman = attackerIsHuman;
        DefenderType = defenderType;
        DefenderIsHuman = defenderIsHuman;
        DefenderSquare = defenderSquare;
    }

    public bool DefenderIsWhite => !AttackerIsWhite;
    public bool IsKingCapture => Move.IsKingCapture;

    public override string ToString() =>
        $"{(AttackerIsWhite ? "White" : "Black")} {AttackerType} attacks " +
        $"{(AttackerIsWhite ? "Black" : "White")} {DefenderType} ({Move.ToUci()})";
}

public interface ICombatResolver
{
    Task<CombatOutcome> ResolveAsync(CombatRequest request, CancellationToken ct);
}

/// <summary>Plain chess: every capture succeeds immediately.</summary>
public sealed class AttackerAlwaysWinsResolver : ICombatResolver
{
    public Task<CombatOutcome> ResolveAsync(CombatRequest request, CancellationToken ct) =>
        Task.FromResult(CombatOutcome.AttackerWins);
}
