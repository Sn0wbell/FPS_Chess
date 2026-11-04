using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Stand-in for the FPS arena while it is not wired to the board.
/// Assign it to ChessGameManager.combatResolverComponent to test the
/// "attacker lost the duel -> loses the turn" flow without leaving the board.
/// </summary>
public sealed class DebugCombatResolver : MonoBehaviour, ICombatResolver
{
    public enum Mode
    {
        AttackerAlwaysWins,
        DefenderAlwaysWins,
        Random
    }

    [SerializeField] private Mode mode = Mode.Random;

    [Range(0f, 1f)]
    [SerializeField] private float attackerWinChance = 0.5f;

    [Tooltip("Fake duel duration in seconds (0 = instant).")]
    [Min(0f)]
    [SerializeField] private float duelDuration = 0.5f;

    private readonly System.Random rng = new System.Random();

    public async Task<CombatOutcome> ResolveAsync(CombatRequest request, CancellationToken ct)
    {
        if (duelDuration > 0f)
            await Task.Delay(Mathf.CeilToInt(duelDuration * 1000f), ct);

        CombatOutcome outcome;
        switch (mode)
        {
            case Mode.AttackerAlwaysWins: outcome = CombatOutcome.AttackerWins; break;
            case Mode.DefenderAlwaysWins: outcome = CombatOutcome.DefenderWins; break;
            default:
                outcome = rng.NextDouble() < attackerWinChance
                    ? CombatOutcome.AttackerWins
                    : CombatOutcome.DefenderWins;
                break;
        }

        Debug.Log($"[DebugCombatResolver] {request} -> {outcome}");
        return outcome;
    }
}
