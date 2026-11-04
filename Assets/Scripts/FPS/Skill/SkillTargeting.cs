using UnityEngine;

/// <summary>Anything that belongs to a side: pieces, pawn clones, rook walls.</summary>
public interface ITeamOwned
{
    Team Team { get; }
}

/// <summary>
/// Implemented by skills that boost the owner's next shots (e.g. the Knight's landing bonus).
/// Guns ask the owner piece once per shot.
/// </summary>
public interface IOutgoingDamageBonus
{
    /// <summary>Multiplier for the shot being fired now (1 = none). May consume a one-shot bonus.</summary>
    float ConsumeShotDamageMultiplier();
}

/// <summary>
/// Shared targeting rules for skills.
///  - Skills NEVER damage allies (same team), but their effects (push, pull, slow, knock-back) still apply.
///  - Dead pieces are never targeted.
///  - DamageInfo.hitDirection is the TRAVEL direction of the hit (attacker -> victim), like bullets.
/// </summary>
public static class SkillTargeting
{
    public static ITeamOwned OwnerOf(Component c) =>
        c != null ? c.GetComponentInParent<ITeamOwned>() : null;

    public static ChessPieceFPSController PieceOf(Component c) =>
        c != null ? c.GetComponentInParent<ChessPieceFPSController>() : null;

    public static bool IsAlly(ITeamOwned a, ITeamOwned b)
    {
        if (a == null || b == null)
            return false;
        if (a is Object ua && ua == null) return false; // destroyed
        if (b is Object ub && ub == null) return false;
        return a.Team == b.Team;
    }

    public static bool IsAlive(ChessPieceFPSController piece) =>
        piece != null && !piece.IsDead && piece.gameObject.activeInHierarchy;

    /// <summary>A piece that skills may push/pull/slow (alive, not the caster itself).</summary>
    public static bool IsAffectable(ChessPieceFPSController caster, ChessPieceFPSController target) =>
        IsAlive(target) && target != caster;

    /// <summary>Deals skill damage unless the target is an ally of the source, dead, or destroyed.</summary>
    public static bool TryDealDamage(ITeamOwned source, IDamageable target, DamageInfo info)
    {
        if (target == null)
            return false;

        if (target is Component comp)
        {
            if (comp == null)
                return false;

            if (IsAlly(source, OwnerOf(comp)))
                return false;

            ChessPieceFPSController piece = PieceOf(comp);
            if (piece != null && piece.IsDead)
                return false;
        }

        target.TakeDamage(info);
        return true;
    }

    /// <summary>Normalized direction from attacker to victim (travel direction of the hit).</summary>
    public static Vector3 TravelDirection(Vector3 from, Vector3 to, Vector3 fallback)
    {
        Vector3 d = to - from;
        return d.sqrMagnitude > 1e-6f ? d.normalized : fallback;
    }
}
