using System.Collections.Generic;
using UnityEngine;
using static UnityEditor.PlayerSettings;

[RequireComponent(typeof(CharacterController))]
public class KnightSkill : ActiveSkill, IDamageModifier, IMovementOverride
{
    [Header("Leap Trajectory")]
    [SerializeField] private float leapDistance = 8f;
    [SerializeField] private float leapHeight = 3.5f;
    [SerializeField] private float leapDuration = 0.6f;

    [Header("Impact")]
    [SerializeField] private float impactDamageScale = 1.0f;
    [SerializeField] private float impulseForce = 6f;

    [Header("Landing AoE")]
    [SerializeField] private float landingRadius = 2.5f;
    [SerializeField] private float landingDamageScale = 1.2f;
    [SerializeField] private float landingKnockUpForce = 6f;

    [Header("Landing Bonus")]
    [SerializeField] private float landingShotDamageBonus = 0.3f;

    [Header("Collision Layers")]
    [SerializeField] private LayerMask solidMask;
    [SerializeField] private LayerMask enemyMask;

    private Vector3 leapStart;
    private Vector3 leapForward;
    private Vector3 lastPos;
    private Vector3 leapEnd;
    private float leapEndT;
    private float leapHeightUsed;

    private float elapsed;
    private bool leapActive;
    private bool landingBonusAvailable;

    private readonly HashSet<IDamageable> damagedTargets = new();

    private ChessPieceFPSController fps;
    private CharacterController controller;

    public bool IsActive => leapActive;
    public Vector3 GetMovementDelta(float deltaTime) => Vector3.zero;
    public void ForceCancelMovement() => CleanupLeap();

    public override void Initialize(in SkillContext ctx)
    {
        base.Initialize(ctx);
        fps = ctx.fps;
        controller = ctx.controller;
    }

    protected override float GetActiveDuration(in SkillContext ctx) => leapDuration;

    // =========================
    // PARAMETRIC ARC
    // =========================
    private Vector3 EvaluateArc(float t)
    {
        float normalizedT = t / leapEndT;

        float yOffset = 4f * leapHeightUsed * normalizedT * (1f - normalizedT);
        Vector3 pos = leapStart + leapForward * (Vector3.Distance(leapStart, leapEnd) * normalizedT);
        pos.y = Mathf.Lerp(leapStart.y, leapEnd.y, normalizedT) + yOffset;

        return pos;
    }

    private void ComputeLeapEnd()
    {
        const float step = 0.05f; // parametric step
        Vector3 pos = leapStart;
        Vector3 prev = pos;

        float t = 0f;
        float horizontalSpeed = leapDistance / leapDuration;

        leapHeightUsed = leapHeight;

        while (true)
        {
            t += step;

            float yOffset = 4f * leapHeightUsed * t * (1f - t); // parabol
            pos = leapStart + leapForward * (horizontalSpeed * t * leapDuration);
            pos.y = leapStart.y + yOffset;

            Vector3 delta = pos - prev;
            float dist = delta.magnitude;

            if (dist > 0f &&
                Physics.SphereCast(prev, controller.radius, delta.normalized, out RaycastHit hit, dist, solidMask, QueryTriggerInteraction.Ignore))
            {
                leapEnd = hit.point;
                leapEndT = t;
                break;
            }

            prev = pos;

        }

        leapEnd = pos;
        leapEndT = t;
    }

    // =========================
    // SKILL FLOW
    // =========================
    protected override void OnEnterActive(in SkillContext ctx)
    {
        damagedTargets.Clear();
        leapActive = true;
        landingBonusAvailable = false;
        elapsed = 0f;

        leapStart = ctx.transform.position;
        leapForward = ctx.transform.forward.normalized;
        lastPos = leapStart;

        leapEnd = Vector3.zero;
        leapEndT = 0f;

        ComputeLeapEnd();
        fps.SetGravityEnabled(false);
    }

    protected override void TickActive(in SkillContext ctx)
    {
        if (!leapActive || controller == null)
            return;

        elapsed += ctx.deltaTime;

        float t = elapsed;
        if (t > leapEndT)
            t = leapEndT;

        Vector3 target = EvaluateArc(t);
        Vector3 delta = target - lastPos;

        int subSteps = Mathf.Max(1, Mathf.CeilToInt(delta.magnitude / 0.1f));
        Vector3 step = delta / subSteps;

        for (int i = 0; i < subSteps; i++)
        {
            controller.Move(step);
            HandleMidAirEnemyCollision();
        }

        lastPos = target;

        if (elapsed >= leapEndT)
            EndLeap();
    }

    protected override void OnExitActive(in SkillContext ctx) => CleanupLeap();
    protected override void OnForceCancel(in SkillContext ctx) => CleanupLeap();

    private void EndLeap()
    {
        if (!leapActive) return;

        fps.SetGravityEnabled(true);
        PerformLandingAoE();
        landingBonusAvailable = true;
        leapActive = false;
    }

    private void CleanupLeap()
    {
        if (!leapActive) return;
        leapActive = false;
        damagedTargets.Clear();
        fps?.SetGravityEnabled(true);
    }

    // =========================
    // COMBAT
    // =========================
    private void ApplyPhysicsCollisionImpulse(
        ChessPieceFPSController other,
        Vector3 contactPoint)
    {
        if (other == null || other == fps) return;
        Vector3 normal = (fps.transform.position - contactPoint).normalized;
        if (normal.sqrMagnitude < 0.0001f) return;
        other.AddExternalImpulse(normal * impulseForce);
    }

    private void HandleMidAirEnemyCollision()
    {
        if (controller == null) return;

        float radius = controller.radius;
        Vector3 bottom = new Vector3(transform.position.x, controller.bounds.min.y + radius, transform.position.z);
        Vector3 top = new Vector3(transform.position.x, controller.bounds.max.y - radius, transform.position.z);

        var hits = Physics.OverlapCapsule(bottom, top, radius, enemyMask, QueryTriggerInteraction.Ignore);
        float speed = leapDistance / leapDuration;

        foreach (var col in hits)
        {
            if (col.TryGetComponent<ChessPieceFPSController>(out var otherFps))
            {
                if (otherFps == fps) continue;
                ApplyPhysicsCollisionImpulse(otherFps, col.ClosestPoint(transform.position));
            }

            if (!col.TryGetComponent<IDamageable>(out var dmg)) continue;
            if (!damagedTargets.Add(dmg)) continue;

            dmg.TakeDamage(new DamageInfo
            {
                amount = speed * impactDamageScale,
                hitPoint = col.ClosestPoint(transform.position),
                hitDirection = (col.transform.position - transform.position).normalized,
                source = gameObject,
                type = DamageType.Melee
            });
        }
    }

    private void ApplyLandingKnockUpImpulse(
        ChessPieceFPSController other,
        Vector3 contactPoint)
    {
        if (other == null || other == fps) return;
        Vector3 normal = (fps.transform.position - contactPoint).normalized;
        if (normal.sqrMagnitude < 0.0001f) return;
        normal += Vector3.up * landingKnockUpForce;
        other.AddExternalImpulse(normal * impulseForce);
    }

    private void PerformLandingAoE()
    {
        var hits = Physics.OverlapSphere(transform.position, landingRadius, enemyMask, QueryTriggerInteraction.Ignore);
        float speed = leapDistance / leapDuration;

        foreach (var col in hits)
        {
            if (col.TryGetComponent<ChessPieceFPSController>(out var otherFps))
            {
                if (otherFps == fps) continue;
                ApplyLandingKnockUpImpulse(otherFps, col.ClosestPoint(transform.position));
            }

            if (!col.TryGetComponent<IDamageable>(out var dmg)) continue;

            dmg.TakeDamage(new DamageInfo
            {
                amount = speed * landingDamageScale,
                hitPoint = col.ClosestPoint(transform.position),
                hitDirection = (col.transform.position - transform.position).normalized,
                source = gameObject,
                type = DamageType.Melee
            });
        }
    }

    // =========================
    // BONUS / DAMAGE MOD
    // =========================
    public bool TryConsumeLandingBonus(out float bonus)
    {
        if (!landingBonusAvailable)
        {
            bonus = 0f;
            return false;
        }

        landingBonusAvailable = false;
        bonus = landingShotDamageBonus;
        return true;
    }

    public void ModifyDamage(ref DamageInfo info)
    {
        if (State != SkillState.Active) return;
        info.amount *= 0.35f;
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (leapEnd == Vector3.zero)
            return;

        const int SEGMENTS = 32;
        Gizmos.color = Color.yellow;

        Vector3 prev = leapStart;
        for (int i = 1; i <= SEGMENTS; i++)
        {
            float t = i / (float)SEGMENTS * leapEndT;
            Vector3 pos = EvaluateArc(t);
            Gizmos.DrawLine(prev, pos);
            prev = pos;
        }

        Gizmos.color = Color.red;
        Gizmos.DrawSphere(EvaluateArc(leapEndT), 0.2f);
    }
#endif
}
