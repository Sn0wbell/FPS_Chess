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
    /// <summary>
    /// Landing-style blast (same rule for every skill): share of the base knock a piece takes at this distance,
    /// measured edge to edge. 1 within fullShare x radius, then linearly down to minShare at the radius; 0 outside.
    /// </summary>
    public static float BlastForceShare(float edgeDistance, float radius, float fullShare, float minShare)
    {
        if (edgeDistance > radius)
            return 0f;
        float full = radius * Mathf.Clamp01(fullShare);
        if (edgeDistance <= full)
            return 1f;
        return Mathf.Lerp(1f, minShare, (edgeDistance - full) / Mathf.Max(1e-4f, radius - full));
    }

    /// <summary>
    /// A piece that cannot be knocked this way: right behind it (within checkDistance along the knock's horizontal
    /// direction) is a wall or terrain too steep to walk. Other pieces do not count (they are knocked along).
    /// </summary>
    public static bool CannotBePushed(ChessPieceFPSController piece, Vector3 knock, float checkDistance)
    {
        Vector3 dir = new Vector3(knock.x, 0f, knock.z);
        var cc = piece != null ? piece.GetComponent<CharacterController>() : null;
        if (cc == null || !cc.enabled || dir.sqrMagnitude < 1e-6f)
            return false;
        dir.Normalize();

        Vector3 c = cc.bounds.center;
        float half = Mathf.Max(0f, cc.height * 0.5f - cc.radius);
        int mask = Physics.DefaultRaycastLayers & ~(1 << piece.gameObject.layer);
        foreach (RaycastHit h in Physics.CapsuleCastAll(c + Vector3.up * half, c - Vector3.up * half, cc.radius * 0.95f, dir,
                     checkDistance + cc.skinWidth, mask, QueryTriggerInteraction.Ignore))
        {
            if (h.collider == null || h.collider.transform.IsChildOf(piece.transform))
                continue;
            if (h.collider.GetComponentInParent<ChessPieceFPSController>() != null)
                continue; // another piece: knocked along, not a wall
            if (Vector3.Angle(h.normal, Vector3.up) > cc.slopeLimit)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The push-back a charging piece takes when the piece it hits cannot be knocked: share x the knock it gave,
    /// sideways reversed, upward the same.
    /// </summary>
    public static Vector3 BlockedReaction(Vector3 knock, float share) =>
        new Vector3(-knock.x, knock.y, -knock.z) * share;

    /// <summary>Colliders within <paramref name="gap"/> of this body's surface (edge to edge): its capsule grown by gap.</summary>
    public static Collider[] OverlapBody(CharacterController cc, float gap, int mask)
    {
        Vector3 c = cc.bounds.center;
        float half = Mathf.Max(0f, cc.height * 0.5f - cc.radius);
        return Physics.OverlapCapsule(c + Vector3.up * half, c - Vector3.up * half, cc.radius + Mathf.Max(0f, gap), mask, QueryTriggerInteraction.Ignore);
    }

    /// <summary>Horizontal gap between two bodies (centre distance minus both radii, never below 0).</summary>
    public static float EdgeDistanceXZ(Vector3 from, float fromRadius, ChessPieceFPSController other)
    {
        Vector3 d = other.transform.position - from;
        d.y = 0f;
        var cc = other.GetComponent<CharacterController>();
        return Mathf.Max(0f, d.magnitude - fromRadius - (cc != null ? cc.radius : 0f));
    }

    /// <summary>Reference mass of a chess piece (kg), to turn a piece's knock-back into momentum for heavier things (rook walls).</summary>
    public const float PieceMass = 80f;

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

    /// <summary>
    /// Collision between two pieces like billiard balls of equal mass (elastic), on the
    /// horizontal plane: the velocity components along the line between the two centres are
    /// exchanged, the sideways components are kept. A hit from the front sends the target
    /// backwards, from the left sends it to the right, a glancing hit barely moves it.
    /// Both bodies also bounce up by <paramref name="bounce"/> x the impact strength.
    /// Velocities here are knock-back strengths (direction x force); a standing piece is zero.
    /// </summary>
    /// <summary>
    /// Blast knock-back away from <paramref name="from"/> (horizontal), strength fading from
    /// <paramref name="force"/> at the centre to 0 at <paramref name="radius"/>, plus a bounce of
    /// <paramref name="bounce"/> x that strength.
    /// </summary>
    public static Vector3 RadialKnock(Vector3 from, Vector3 to, float radius, float force, float bounce)
    {
        Vector3 flat = to - from;
        flat.y = 0f;
        float distance = flat.magnitude;
        flat = distance > 1e-3f ? flat / distance : Vector3.forward;

        float strength = force * (1f - Mathf.Clamp01(distance / Mathf.Max(0.01f, radius)));
        return flat * strength + Vector3.up * (bounce * strength);
    }

    /// <summary>
    /// Closest point of a piece's body to <paramref name="point"/>. Unity's Collider.ClosestPoint does not
    /// support CharacterController, so its capsule is computed here; other colliders use ClosestPoint.
    /// </summary>
    public static Vector3 ClosestPointOnBody(Collider body, Vector3 point)
    {
        if (body == null)
            return point;
        if (!(body is CharacterController cc))
            return body.ClosestPoint(point);

        Vector3 center = cc.bounds.center;
        float half = Mathf.Max(0f, cc.height * 0.5f - cc.radius);
        Vector3 a = center + Vector3.up * half, b = center - Vector3.up * half;
        Vector3 ab = a - b;
        float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(point - b, ab) / ab.sqrMagnitude) : 0f;
        Vector3 axis = b + ab * t;
        Vector3 offset = point - axis;
        return offset.magnitude <= cc.radius ? point : axis + offset.normalized * cc.radius;
    }

    /// <summary>
    /// Moves a controller by <paramref name="move"/>, sliding along the ground / walls it runs into
    /// (speed x cos of the hit angle). Hitting something at <paramref name="headOnAngle"/> or more
    /// (90 = straight into it) does not move it and returns false.
    /// </summary>
    public static bool SlideMove(CharacterController cc, Vector3 move, LayerMask obstacles, float headOnAngle)
    {
        float length = move.magnitude;
        if (cc == null || length < 1e-6f)
            return true;

        Vector3 dir = move / length;
        Vector3 center = cc.bounds.center;
        float half = Mathf.Max(0f, cc.height * 0.5f - cc.radius);
        if (Physics.CapsuleCast(center + Vector3.up * half, center - Vector3.up * half, cc.radius * 0.95f, dir, out RaycastHit hit,
                length + cc.skinWidth + 0.05f, obstacles, QueryTriggerInteraction.Ignore))
        {
            float angle = Mathf.Asin(Mathf.Clamp01(Vector3.Dot(dir, -hit.normal))) * Mathf.Rad2Deg; // 0 = along it, 90 = straight in
            if (angle >= headOnAngle)
                return false;
            move = (dir - Vector3.Dot(dir, hit.normal) * hit.normal) * length;
        }

        cc.Move(move);
        return true;
    }

    /// <returns>The impact strength (closing speed along the line of centres); 0 or less = no collision.</returns>
    public static float BilliardCollision(Vector3 posA, Vector3 velA, Vector3 posB, Vector3 velB, float bounce,
        out Vector3 newVelA, out Vector3 newVelB)
    {
        velA.y = 0f;
        velB.y = 0f;

        Vector3 n = posB - posA;
        n.y = 0f;
        if (n.sqrMagnitude < 1e-6f)
        {
            Vector3 rel = velA - velB; // exactly on top of each other: along the relative motion
            n = rel.sqrMagnitude > 1e-6f ? rel : Vector3.forward;
        }
        n.Normalize();

        float impact = Vector3.Dot(velA - velB, n); // closing speed along the line of centres
        if (impact <= 0f)
        {
            newVelA = velA; // moving apart: no exchange
            newVelB = velB;
            return impact;
        }

        Vector3 up = Vector3.up * (bounce * impact);
        newVelA = velA - n * impact + up;
        newVelB = velB + n * impact + up;
        return impact;
    }
}
