using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// KNIGHT — leap. A parabolic jump forward from the ground (gravity off). Pieces touched in the
/// air are hit once per leap: damage + billiard-style knock-back (like the Rook). The leap ends
/// only on real ground (it follows the arc down to lower ground; touching a piece is a hit, not
/// a landing); facing into an obstacle (wall, ceiling) fails the leap: no landing blast, no bonus.
/// Landing blasts everything around (knock-back fading with distance, small bounce; damage only
/// to pieces not already hit this leap). While leaping the knight takes reduced damage; after
/// landing its next shot deals bonus damage.
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
    [Tooltip("How far along the arc (in leapDuration units) a landing surface is searched for in advance. Beyond it the knight keeps following the arc until it touches ground.")]
    [SerializeField] private float maxArcParameter = 2.5f;

    [Header("Impact")]
    [Tooltip("Damage of a hit: in the air, in a clash and by the landing blast (each piece takes it once per leap).")]
    [SerializeField] private float impactDamage = 25f;
    [SerializeField] private float impulseForce = 6f;
    [Tooltip("Upward bounce of a hit = this x the impact strength (glancing hits barely bounce).")]
    [SerializeField] private float bounceFactor = 0.35f;

    [Header("Landing AoE")]
    [SerializeField] private float landingRadius = 2.5f;

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
    private bool hasPredictedEnd;   // a surface was found on the arc
    private bool predictedEndIsWall; // ...and it is an obstacle, not ground
    private float elapsed;
    private bool leapActive;

    private bool landingBonusAvailable;

    private readonly HashSet<ChessPieceFPSController> hitPieces = new();     // collided this leap (once each)
    private readonly HashSet<ChessPieceFPSController> damagedPieces = new(); // took the damage this leap (once each)

    private ChessPieceFPSController fps;
    private CharacterController controller;

    public bool IsActive => leapActive;
    public Vector3 GetMovementDelta(float deltaTime) => Vector3.zero;
    public Vector3 GetChargeDirection() => leapForward;

    /// <summary>Knock-back strength of this charge (used by the billiard-style clash).</summary>
    public float ImpulseForce => impulseForce;
    public void ForceCancelMovement() => CleanupLeap();

    /// <summary>Damage of a hit (mid-air, clash, landing blast).</summary>
    public float ImpactDamage => impactDamage;

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
    /// Walks the arc until it meets a solid surface (bounded search up to maxArcParameter).
    /// Nothing found: no predicted end, the leap follows the arc until it touches ground.
    /// </summary>
    private void ComputeLeapEnd()
    {
        Vector3 centerOffset = controller != null ? controller.center : Vector3.zero;
        float radius = controller != null ? controller.radius : 0.4f;

        float u = 0f;
        Vector3 prev = ArcPoint(0f);
        leapEndU = float.PositiveInfinity;
        hasPredictedEnd = false;
        predictedEndIsWall = false;

        while (u < maxArcParameter)
        {
            float nextU = Mathf.Min(u + ArcStep, maxArcParameter);
            Vector3 pos = ArcPoint(nextU);
            Vector3 delta = pos - prev;
            float dist = delta.magnitude;

            if (dist > 0f &&
                Physics.SphereCast(prev + centerOffset, radius, delta / dist, out RaycastHit hit, dist, solidMask, QueryTriggerInteraction.Ignore))
            {
                // Ends where the sphere touched (fraction of this step): ground = landing, else an obstacle.
                leapEndU = u + (nextU - u) * Mathf.Clamp01(hit.distance / dist);
                hasPredictedEnd = true;
                predictedEndIsWall = !IsWalkable(hit.normal);
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
        // A knight cannot leap in mid-air: nothing happens, no cooldown.
        if (!StandingOnGround())
        {
            AbortActivation();
            return;
        }

        hitPieces.Clear();
        damagedPieces.Clear();
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

        // The active phase lasts exactly as long as this leap (no surface found: until it touches ground).
        SetActiveRemaining(hasPredictedEnd ? leapEndU * leapDuration + 0.05f : 3600f);
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
            if (!leapActive)
                return; // clash / knocked: the skill is already over

            // Face into an obstacle (wall, ceiling): the leap fails.
            if ((flags & (CollisionFlags.Sides | CollisionFlags.Above)) != 0 && HitsObstacle(step))
            {
                FailLeap();
                return;
            }

            // Real ground under the feet on the way down: land now. (A piece under the feet is a hit, not ground.)
            if (u > 0.5f && (flags & CollisionFlags.Below) != 0 && StandingOnGround())
            {
                EndLeap();
                FinishActive();
                return;
            }
        }

        if (leapActive && hasPredictedEnd && u >= leapEndU)
        {
            if (predictedEndIsWall)
            {
                FailLeap();
                return;
            }
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
        hitPieces.Clear();
        damagedPieces.Clear();
        if (fps != null)
            fps.SetGravityEnabled(this, true);
    }

    /// <summary>Failed leap (faced an obstacle): gravity back, no landing blast, no bonus; cooldown starts.</summary>
    private void FailLeap()
    {
        CleanupLeap();
        FinishActive();
    }

    // =========================
    // GROUND / OBSTACLES
    // =========================
    private bool IsWalkable(Vector3 normal)
    {
        float slopeLimit = controller != null ? controller.slopeLimit : 45f;
        return Vector3.Angle(normal, Vector3.up) <= slopeLimit;
    }

    /// <summary>Walkable solid ground (solidMask) right under the feet.</summary>
    private bool StandingOnGround()
    {
        if (controller == null)
            return true;
        float r = controller.radius * 0.9f;
        Vector3 feet = new Vector3(transform.position.x, controller.bounds.min.y, transform.position.z);
        return Physics.SphereCast(feet + Vector3.up * (r + 0.05f), r, Vector3.down, out RaycastHit ground, 0.2f, solidMask, QueryTriggerInteraction.Ignore)
            && IsWalkable(ground.normal);
    }

    /// <summary>The blocked move ran into something solid that is not walkable ground (a wall, a ceiling).</summary>
    private bool HitsObstacle(Vector3 step)
    {
        Vector3 dir = step;
        if (dir.sqrMagnitude < 1e-8f)
            return false;
        dir.Normalize();
        return Physics.SphereCast(transform.position + controller.center, controller.radius * 0.95f, dir, out RaycastHit hit,
                   step.magnitude + 0.1f, solidMask, QueryTriggerInteraction.Ignore)
               && !IsWalkable(hit.normal);
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
        float otherForce = impulseForce;

        if (opposingSkill is KnightSkill otherKnight)
        {
            otherDirection = otherKnight.GetChargeDirection();
            otherImpact = otherKnight.ImpactDamage;
            otherForce = otherKnight.ImpulseForce;
        }
        else if (opposingSkill is RookSkill otherRook)
        {
            otherDirection = otherRook.GetChargeDirection();
            otherImpact = otherRook.ImpactDamage;
            otherForce = otherRook.PushForce;
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
        // Both bounce off like billiard balls (same rule as the rook's clash).
        SkillTargeting.BilliardCollision(
            fps.transform.position, leapForward * impulseForce,
            otherFps.transform.position, otherDirection.normalized * otherForce,
            bounceFactor, out Vector3 myBounce, out Vector3 otherBounce);
        fps.AddExternalImpulse(myBounce);
        otherFps.AddExternalImpulse(otherBounce);
    }

    private void HandleMidAirCollision()
    {
        if (!leapActive || controller == null)
            return;

        float radius = controller.radius;
        Vector3 bottom = new Vector3(transform.position.x, controller.bounds.min.y + radius, transform.position.z);
        Vector3 top = new Vector3(transform.position.x, controller.bounds.max.y - radius, transform.position.z);

        // Slightly larger than the body: controllers stop at contact and never overlap,
        // so touching another piece must count as a hit.
        float contactRadius = radius + controller.skinWidth + 0.05f;
        Collider[] hits = Physics.OverlapCapsule(bottom, top, contactRadius, enemyMask, QueryTriggerInteraction.Ignore);
        int count = hits.Length;

        for (int i = 0; i < count; i++)
        {
            if (!leapActive)
                return;

            ChessPieceFPSController otherFps = SkillTargeting.PieceOf(hits[i]);
            if (!SkillTargeting.IsAffectable(fps, otherFps) || hitPieces.Contains(otherFps))
                continue; // each piece collides once per leap

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
            // Billiard-style (like the rook); a piece right under the feet is pushed along the leap.
            float impact = SkillTargeting.BilliardCollision(
                transform.position, leapForward * impulseForce,
                otherFps.transform.position, Vector3.zero,
                bounceFactor, out _, out Vector3 knock);
            if (impact <= 0f)
                continue; // behind the leap: not hit
            hitPieces.Add(otherFps);

            otherFps.InterruptActiveMovementSkills();
            otherFps.AddExternalImpulse(knock);

            damagedPieces.Add(otherFps);
            SkillTargeting.TryDealDamage(fps, otherFps.GetComponent<IDamageable>(), new DamageInfo
            {
                amount = impactDamage,
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
        var hitThisLanding = new HashSet<ChessPieceFPSController>();

        for (int i = 0; i < count; i++)
        {
            Collider col = hits[i];
            ChessPieceFPSController otherFps = SkillTargeting.PieceOf(col);
            if (!SkillTargeting.IsAffectable(fps, otherFps) || !hitThisLanding.Add(otherFps))
                continue;

            Vector3 away = SkillTargeting.TravelDirection(transform.position, otherFps.transform.position, leapForward);
            Vector3 flat = new Vector3(away.x, 0f, away.z);
            if (flat.sqrMagnitude < 1e-6f) flat = leapForward;
            flat.Normalize();

            // Blast from the knight: strongest next to it, fading to nothing at the edge; small bounce.
            float distance = new Vector2(otherFps.transform.position.x - transform.position.x,
                                         otherFps.transform.position.z - transform.position.z).magnitude;
            float strength = impulseForce * (1f - Mathf.Clamp01(distance / Mathf.Max(0.01f, landingRadius)));
            otherFps.AddExternalImpulse(flat * strength + Vector3.up * (bounceFactor * strength));

            // Damage once per leap: a piece already hit in the air is only knocked.
            if (!damagedPieces.Add(otherFps))
                continue;

            SkillTargeting.TryDealDamage(fps, otherFps.GetComponent<IDamageable>(), new DamageInfo
            {
                amount = impactDamage,
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
