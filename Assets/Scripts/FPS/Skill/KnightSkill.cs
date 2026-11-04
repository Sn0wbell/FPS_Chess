using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// KNIGHT — leap. A parabolic jump forward (gravity off). Pieces hit in the air are damaged
/// and knocked back; landing hits everything around (damage + knock-up). While leaping the
/// knight takes reduced damage; after landing its next shot within a short window deals bonus damage.
///
/// Trajectory uses one parameter for both the precomputed landing and the motion:
///   u = elapsed / leapDuration,  pos(u) = start + forward * leapDistance * u,
///   y(u) = start.y + 4 * leapHeight * u * (1 - u)   (u = 1 is back at start height).
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class KnightSkill : ActiveSkill, IDamageModifier, IMovementOverride, IOutgoingDamageBonus
{
    [Header("Leap Trajectory")]
    [SerializeField] private float leapDistance = 8f;
    [SerializeField] private float leapHeight = 3.5f;
    [SerializeField] private float leapDuration = 0.6f;
    [Tooltip("Longest arc (in leapDuration units) followed when no landing surface is found (falls off a ledge).")]
    [SerializeField] private float maxArcParameter = 2.5f;

    [Header("Impact")]
    [SerializeField] private float impactDamageScale = 1.0f;
    [SerializeField] private float impulseForce = 6f;

    [Header("Landing AoE")]
    [SerializeField] private float landingRadius = 2.5f;
    [SerializeField] private float landingDamageScale = 1.2f;
    [SerializeField] private float landingKnockUpForce = 6f;

    [Header("Landing Bonus")]
    [Tooltip("+30% damage on the first shot after landing.")]
    [SerializeField] private float landingShotDamageBonus = 0.3f;

    [Header("Defence While Leaping")]
    [Tooltip("Fraction of incoming damage removed while in the air (0.65 = takes 35%).")]
    [Range(0f, 1f)]
    [SerializeField] private float leapDamageReduction = 0.65f;

    [Header("Collision Layers")]
    [SerializeField] private LayerMask solidMask;
    [SerializeField] private LayerMask enemyMask;

    private const float ArcStep = 0.05f; // parametric step

    private Vector3 leapStart;
    private Vector3 leapForward;
    private float leapEndU;
    private float elapsed;
    private bool leapActive;

    private bool landingBonusAvailable;

    private readonly HashSet<IDamageable> damagedTargets = new();

    private ChessPieceFPSController fps;
    private CharacterController controller;

    public bool IsActive => leapActive;
    public Vector3 GetMovementDelta(float deltaTime) => Vector3.zero;
    public Vector3 GetChargeDirection() => leapForward;

    /// <summary>Knock-back strength of this charge (used by the billiard-style clash).</summary>
    public float ImpulseForce => impulseForce;
    public void ForceCancelMovement() => CleanupLeap();

    /// <summary>Damage of a mid-air hit (also used in clashes).</summary>
    public float ImpactDamage => (leapDistance / Mathf.Max(0.01f, leapDuration)) * impactDamageScale;

    public override void Initialize(in SkillContext ctx)
    {
        base.Initialize(ctx);
        fps = ctx.fps;
        controller = ctx.controller;
    }

    protected override float GetActiveDuration(in SkillContext ctx) => leapDuration * maxArcParameter + 0.1f;

    // =========================
    // PARAMETRIC ARC
    // =========================
    private Vector3 ArcPoint(float u)
    {
        Vector3 pos = leapStart + leapForward * (leapDistance * u);
        pos.y = leapStart.y + 4f * leapHeight * u * (1f - u); // parabol
        return pos;
    }

    /// <summary>
    /// Walks the arc until it meets a solid surface. Bounded: stops at maxArcParameter
    /// (a leap over a ledge then just ends and normal gravity takes over).
    /// </summary>
    private void ComputeLeapEnd()
    {
        Vector3 centerOffset = controller != null ? controller.center : Vector3.zero;
        float radius = controller != null ? controller.radius : 0.4f;

        float u = 0f;
        Vector3 prev = ArcPoint(0f);
        leapEndU = maxArcParameter;

        while (u < maxArcParameter)
        {
            float nextU = Mathf.Min(u + ArcStep, maxArcParameter);
            Vector3 pos = ArcPoint(nextU);
            Vector3 delta = pos - prev;
            float dist = delta.magnitude;

            if (dist > 0f &&
                Physics.SphereCast(prev + centerOffset, radius, delta / dist, out RaycastHit hit, dist, solidMask, QueryTriggerInteraction.Ignore))
            {
                // Land where the sphere touched (fraction of this step).
                leapEndU = u + (nextU - u) * Mathf.Clamp01(hit.distance / dist);
                return;
            }

            prev = pos;
            u = nextU;
        }
    }

    // =========================
    // SKILL FLOW
    // =========================
    protected override void OnEnterActive(in SkillContext ctx)
    {
        damagedTargets.Clear();
        elapsed = 0f;

        Transform t = ctx.transform != null ? ctx.transform : transform;
        leapStart = t.position;
        Vector3 forward = t.forward;
        forward.y = 0f;
        leapForward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;

        ComputeLeapEnd();

        leapActive = true;
        landingBonusAvailable = false;
        fps?.SetGravityEnabled(this, false);

        // The active phase lasts exactly as long as this leap.
        SetActiveRemaining(leapEndU * leapDuration + 0.05f);
    }

    protected override void TickActive(in SkillContext ctx)
    {
        if (!leapActive || controller == null)
            return;

        elapsed += ctx.deltaTime;
        float u = Mathf.Min(elapsed / Mathf.Max(0.01f, leapDuration), leapEndU);

        Vector3 target = ArcPoint(u);
        Vector3 delta = target - transform.position;

        int subSteps = Mathf.Max(1, Mathf.CeilToInt(delta.magnitude / 0.1f));
        Vector3 step = delta / subSteps;

        for (int i = 0; i < subSteps && leapActive; i++)
        {
            CollisionFlags flags = controller.Move(step);
            HandleMidAirCollision();

            // Touched the ground on the way down before the predicted point: land now.
            if (u > 0.5f && (flags & CollisionFlags.Below) != 0)
            {
                EndLeap();
                FinishActive();
                return;
            }
        }

        if (leapActive && u >= leapEndU)
        {
            EndLeap();
            FinishActive();
        }
    }

    protected override void OnExitActive(in SkillContext ctx)
    {
        // Time ran out mid-air (should not happen; the phase matches the leap): land cleanly.
        if (leapActive)
            EndLeap();
    }

    protected override void OnForceCancel(in SkillContext ctx) => CleanupLeap();

    private void EndLeap()
    {
        if (!leapActive) return;

        leapActive = false;
        fps?.SetGravityEnabled(this, true);
        PerformLandingAoE();

        landingBonusAvailable = true;
    }

    private void CleanupLeap()
    {
        if (!leapActive) return;
        leapActive = false;
        damagedTargets.Clear();
        if (fps != null)
            fps.SetGravityEnabled(this, true);
    }

    // =========================
    // COMBAT
    // =========================
    private void ResolveSkillClash(ChessPieceFPSController otherFps, ActiveSkill opposingSkill)
    {
        if (!leapActive || otherFps == null)
            return;

        Vector3 otherDirection = Vector3.zero;
        float otherImpact = ImpactDamage;

        if (opposingSkill is KnightSkill otherKnight)
        {
            otherDirection = otherKnight.GetChargeDirection();
            otherImpact = otherKnight.ImpactDamage;
        }
        else if (opposingSkill is RookSkill otherRook)
        {
            otherDirection = otherRook.GetChargeDirection();
            otherImpact = otherRook.ImpactDamage;
        }

        if (otherDirection.sqrMagnitude < 0.001f)
            return;

        // Damage cả hai phía.
        SkillTargeting.TryDealDamage(fps, otherFps.GetComponent<IDamageable>(), new DamageInfo
        {
            amount = ImpactDamage,
            hitPoint = otherFps.transform.position,
            hitDirection = leapForward,
            source = gameObject,
            type = DamageType.Melee
        });

        SkillTargeting.TryDealDamage(otherFps, fps.GetComponent<IDamageable>(), new DamageInfo
        {
            amount = otherImpact,
            hitPoint = fps.transform.position,
            hitDirection = otherDirection,
            source = otherFps.gameObject,
            type = DamageType.Melee
        });

        // Ngắt chuyển động skill của cả hai phía.
        opposingSkill.ForceCancel();
        fps.InterruptActiveMovementSkills();
        otherFps.InterruptActiveMovementSkills();

        // Hai phía văng ngược hướng lao tới của đối phương.
        fps.AddExternalImpulse(-otherDirection.normalized * impulseForce);
        otherFps.AddExternalImpulse(-leapForward.normalized * impulseForce);
    }

    private void HandleMidAirCollision()
    {
        if (!leapActive || controller == null)
            return;

        float radius = controller.radius;
        Vector3 bottom = new Vector3(transform.position.x, controller.bounds.min.y + radius, transform.position.z);
        Vector3 top = new Vector3(transform.position.x, controller.bounds.max.y - radius, transform.position.z);

        Collider[] hits = Physics.OverlapCapsule(bottom, top, radius, enemyMask, QueryTriggerInteraction.Ignore);
        int count = hits.Length;

        for (int i = 0; i < count; i++)
        {
            if (!leapActive)
                return;

            ChessPieceFPSController otherFps = SkillTargeting.PieceOf(hits[i]);
            if (!SkillTargeting.IsAffectable(fps, otherFps))
                continue;

            // Kiểm tra đối phương có đang dùng Knight/Rook dash không.
            ActiveSkill opposingMovementSkill = null;
            foreach (var skill in otherFps.boundSkills)
            {
                if (skill == null || skill.State != SkillState.Active)
                    continue;
                if (skill is KnightSkill || skill is RookSkill)
                {
                    opposingMovementSkill = skill;
                    break;
                }
            }

            // CLASH: Knight/Rook đang lao vào nhau.
            if (opposingMovementSkill != null)
            {
                ResolveSkillClash(otherFps, opposingMovementSkill);
                return;
            }

            // Va chạm thông thường: chỉ enemy nhận impulse và damage.
            IDamageable dmg = otherFps.GetComponent<IDamageable>();
            if (dmg == null || !damagedTargets.Add(dmg))
                continue; // each piece is hit once per leap

            otherFps.InterruptActiveMovementSkills();
            otherFps.AddExternalImpulse(-leapForward.normalized * impulseForce);

            SkillTargeting.TryDealDamage(fps, dmg, new DamageInfo
            {
                amount = ImpactDamage,
                hitPoint = hits[i].ClosestPoint(transform.position),
                hitDirection = leapForward,
                source = gameObject,
                type = DamageType.Melee
            });
        }
    }

    private void PerformLandingAoE()
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, landingRadius, enemyMask, QueryTriggerInteraction.Ignore);
        int count = hits.Length;
        float damage = (leapDistance / Mathf.Max(0.01f, leapDuration)) * landingDamageScale;
        var hitThisLanding = new HashSet<ChessPieceFPSController>();

        for (int i = 0; i < count; i++)
        {
            Collider col = hits[i];
            ChessPieceFPSController otherFps = SkillTargeting.PieceOf(col);
            if (!SkillTargeting.IsAffectable(fps, otherFps) || !hitThisLanding.Add(otherFps))
                continue;

            Vector3 away = SkillTargeting.TravelDirection(transform.position, otherFps.transform.position, leapForward);
            Vector3 flat = new Vector3(away.x, 0f, away.z);
            // Same strength as before (not normalized), but away from the knight.
            otherFps.AddExternalImpulse((flat.normalized + Vector3.up * landingKnockUpForce) * impulseForce);

            SkillTargeting.TryDealDamage(fps, otherFps.GetComponent<IDamageable>(), new DamageInfo
            {
                amount = damage,
                hitPoint = col.ClosestPoint(transform.position),
                hitDirection = away,
                source = gameObject,
                type = DamageType.Melee
            });
        }
    }


    // =========================
    // BONUS / DAMAGE MOD
    // =========================
    /// <summary>Applied by ChessPieceHealth to every hit this knight takes.</summary>
    public void ModifyDamage(ref DamageInfo info)
    {
        if (leapActive)
            info.amount *= 1f - leapDamageReduction;
    }

    public bool LandingBonusReady => landingBonusAvailable;

    /// <summary>Asked by the gun once per shot: the first shot after landing gets the bonus.</summary>
    public float ConsumeShotDamageMultiplier()
    {
        if (!landingBonusAvailable)
            return 1f;

        landingBonusAvailable = false;
        return 1f + landingShotDamageBonus;
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!leapActive)
            return;

        const int SEGMENTS = 32;
        Gizmos.color = Color.yellow;

        Vector3 prev = leapStart;
        for (int i = 1; i <= SEGMENTS; i++)
        {
            Vector3 pos = ArcPoint(i / (float)SEGMENTS * leapEndU);
            Gizmos.DrawLine(prev, pos);
            prev = pos;
        }

        Gizmos.color = Color.red;
        Gizmos.DrawSphere(ArcPoint(leapEndU), 0.2f);
    }
#endif
}
