using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// BISHOP — grapple hook, aimed with the piece's own camera.
///  - Map hit   : the bishop is pulled to the point; landing hits pieces around (AoE).
///  - Enemy hit : the target is pulled to the bishop; on contact it is hit and pushed away.
///  - Rook wall : the wall is pulled over (lies flat) to open a line of fire.
///  - Miss / out of the arena: the rope retracts, nothing happens.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class BishopSkill : ActiveSkill, IMovementOverride
{
    private enum GrappleMode { PullSelf, PullEnemy, PullWall }

    [Header("Grapple")]
    [SerializeField] private float pullSpeed = 15f;
    [SerializeField] private float maxDuration = 1.5f;
    [SerializeField] private float maxRange = 20f;

    [Header("Rope Shoot")]
    [SerializeField] private float ropeShootSpeed = 40f;

    [Header("Rope Origin")]
    [SerializeField] private Transform ropeStart;

    [Header("Layers")]
    [SerializeField] private LayerMask mapMask;
    [SerializeField] private LayerMask enemyMask;
    [SerializeField] private LayerMask wallMask;
    [SerializeField] private LayerMask obstacleMask;

    [Header("Impact")]
    [SerializeField] private float impactRadius = 1f;
    [SerializeField] private float impactDamageScale = 4f;
    [SerializeField] private float impactImpulseForce = 12f;

    [Header("Landing")]
    [SerializeField] private float landingRadius = 1.5f;
    [SerializeField] private float landingDamageScale = 6f;
    [SerializeField] private float landingKnockUp = 6f;

    [Header("Rook Wall")]
    [Tooltip("Speed given to the top of a Rook wall to pull it over.")]
    [SerializeField] private float wallToppleForce = 6f;

    [Header("Pools")]
    [SerializeField] private BishopAnchorPool anchorPool;
    [SerializeField] private BishopTrailPool trailPool;

    private CharacterController controller;
    private ChessPieceFPSController fps;
    private ChessPieceFPSController hookedEnemyFps;
    private CharacterController hookedEnemyController;
    private GrappleMode mode;
    private bool active;

    private Vector3 targetPos;
    private Vector3 pullStartPos;
    private Vector3 ropeLogicOriginWS;

    private float traveled;
    private float totalDistance;

    private float ropeTraveled;
    private bool ropeArrived;
    private bool isRetracting;
    private bool hasValidTarget;

    private Transform anchor;
    private Transform trail;
    private LineRenderer trailLR;
    private WallSegment hookedWall;

    private readonly HashSet<IDamageable> hitCache = new();
    private readonly Collider[] hitBuffer = new Collider[16];

    private Bounds arenaBounds;
    private bool hasArenaBounds;

    public bool IsActive => active;
    public Vector3 GetMovementDelta(float deltaTime) => Vector3.zero;
    public void ForceCancelMovement() => ForceCancel();

    public override void Initialize(in SkillContext ctx)
    {
        base.Initialize(ctx);
        controller = ctx.controller;
        fps = ctx.fps;
        ResolveArenaBounds();
    }

    /// <summary>Per instance (each arena map has its own boundary; never cached statically).</summary>
    private void ResolveArenaBounds()
    {
        var arenaController = FindFirstObjectByType<ArenaController>();
        if (!arenaController || !arenaController.arenaBoundary)
        {
            hasArenaBounds = false;
            return;
        }

        Transform t = arenaController.arenaBoundary;
        Vector3 size = new Vector3(Mathf.Abs(t.lossyScale.x), Mathf.Abs(t.lossyScale.y), Mathf.Abs(t.lossyScale.z));
        arenaBounds = new Bounds(t.position, size);
        hasArenaBounds = true;
    }

    protected override float GetActiveDuration(in SkillContext ctx) => maxDuration;

    protected override void OnEnterActive(in SkillContext ctx)
    {
        AcquireTarget();

        if (mode == GrappleMode.PullEnemy && hasValidTarget)
        {
            // Another skill is already moving that piece: treat as a miss.
            if (hookedEnemyFps == null || !hookedEnemyFps.TryBeginForcedMove(this))
            {
                hookedEnemyFps = null;
                hasValidTarget = false;
            }
            else
            {
                hookedEnemyController = hookedEnemyFps.GetComponent<CharacterController>();
            }
        }

        Transform visualOrigin = ropeStart ? ropeStart : transform;
        ropeLogicOriginWS = visualOrigin.position;

        ropeTraveled = 0f;
        ropeArrived = false;
        isRetracting = false;
        traveled = 0f;
        pullStartPos = transform.position;

        totalDistance = Vector3.Distance(ropeLogicOriginWS, targetPos);
        if (totalDistance < 0.05f)
        {
            ReleaseHookedEnemy();
            AbortActivation();
            return;
        }

        hitCache.Clear();
        active = true;

        trail = trailPool ? trailPool.Get() : null;
        if (trail)
        {
            trailLR = trail.GetComponent<LineRenderer>();
            if (trailLR)
            {
                trailLR.positionCount = 2;
                trailLR.SetPosition(0, visualOrigin.position);
                trailLR.SetPosition(1, visualOrigin.position);
            }
        }

        anchor = null;
        fps.ClearOverrideMovement(MovementOverrideSource.Skill);
    }

    private bool IsInsideArena(Vector3 position)
    {
        if (!hasArenaBounds) return true;
        return position.x >= arenaBounds.min.x && position.x <= arenaBounds.max.x &&
               position.z >= arenaBounds.min.z && position.z <= arenaBounds.max.z;
    }

    protected override void TickActive(in SkillContext ctx)
    {
        if (!active) return;

        Vector3 visualOrigin = ropeStart ? ropeStart.position : transform.position;

        // ---------- ROPE SHOOT ----------
        if (!ropeArrived)
        {
            ropeTraveled += ropeShootSpeed * ctx.deltaTime;
            float t = Mathf.Clamp01(ropeTraveled / totalDistance);
            Vector3 ropeHead = Vector3.Lerp(ropeLogicOriginWS, targetPos, t);

            if (!IsInsideArena(ropeHead))
            {
                // Leaving the arena is always a miss (even if something was hit out there).
                hasValidTarget = false;
                ReleaseHookedEnemy();
                isRetracting = true;
                ropeArrived = true;
                return;
            }

            if (trailLR)
            {
                trailLR.SetPosition(0, visualOrigin);
                trailLR.SetPosition(1, ropeHead);
            }

            if (t >= 1f)
            {
                ropeArrived = true;

                if (!hasValidTarget)
                {
                    isRetracting = true;
                    return;
                }

                if (mode == GrappleMode.PullWall)
                {
                    if (hookedWall != null)
                        hookedWall.Topple(transform.position, wallToppleForce);
                    FinishActive();
                    return;
                }

                anchor = anchorPool ? anchorPool.Get() : null;
                if (anchor)
                    anchor.position = targetPos;

                if (mode == GrappleMode.PullSelf)
                    fps.SetGravityEnabled(this, false);
                else if (hookedEnemyFps != null)
                {
                    hookedEnemyFps.SuppressMovement(this);
                    hookedEnemyFps.SetGravityEnabled(this, false);
                }
            }

            return;
        }

        // ---------- ROPE RETRACT (MISS) ----------
        if (isRetracting)
        {
            ropeTraveled -= ropeShootSpeed * ctx.deltaTime;
            float t = Mathf.Clamp01(ropeTraveled / totalDistance);
            Vector3 ropeHead = Vector3.Lerp(visualOrigin, targetPos, t);

            if (trailLR)
            {
                trailLR.SetPosition(0, visualOrigin);
                trailLR.SetPosition(1, ropeHead);
            }

            if (t <= 0f)
                FinishActive();
            return;
        }

        // ---------- PULL ----------
        float step = (mode == GrappleMode.PullEnemy ? ropeShootSpeed : pullSpeed) * ctx.deltaTime;
        traveled += step;
        float pullT = Mathf.Clamp01(traveled / totalDistance);

        if (mode == GrappleMode.PullEnemy)
        {
            if (!SkillTargeting.IsAlive(hookedEnemyFps) || hookedEnemyController == null)
            {
                FinishActive();
                return;
            }

            Transform enemy = hookedEnemyFps.transform;
            Vector3 enemyNext = Vector3.Lerp(enemy.position, transform.position, pullT);
            Vector3 moveDelta = enemyNext - enemy.position;

            // Test at the body centre (whatever the pivot), not at the feet (always touching the floor).
            Vector3 bodyOffset = hookedEnemyController.bounds.center - enemy.position;
            if (Physics.CheckSphere(enemyNext + bodyOffset, 0.4f, obstacleMask, QueryTriggerInteraction.Ignore))
            {
                // Something is in the way: the rope slips, the pull stops here.
                FinishActive();
                return;
            }

            if (moveDelta.sqrMagnitude > 0.0001f)
                hookedEnemyController.Move(moveDelta);

            if (trailLR)
            {
                trailLR.SetPosition(0, visualOrigin);
                trailLR.SetPosition(1, enemy.position);
            }
        }
        else // PullSelf
        {
            Vector3 nextPos = Vector3.Lerp(pullStartPos, targetPos, pullT);
            Vector3 delta = nextPos - transform.position;
            controller.Move(Vector3.Lerp(Vector3.zero, delta, Mathf.Clamp01(16f * ctx.deltaTime)));

            if (trailLR)
            {
                trailLR.SetPosition(0, visualOrigin);
                trailLR.SetPosition(1, targetPos);
            }
        }

        if (HandleImpact())
            return; // impact ends the skill

        if (pullT >= 1f)
        {
            if (mode == GrappleMode.PullSelf)
                ResolveLanding();
            FinishActive();
        }
    }

    protected override void OnForceCancel(in SkillContext ctx) => Cleanup();
    protected override void OnExitActive(in SkillContext ctx) => Cleanup();

    private static void InterruptMovementSkills(ChessPieceFPSController target)
    {
        if (target == null || target.boundSkills == null)
            return;

        foreach (var skill in target.boundSkills)
            if (skill != null && skill.State == SkillState.Active && skill is IMovementOverride)
                skill.ForceCancel();
    }

    // =========================
    // TARGET ACQUIRE
    // =========================
    private void AcquireTarget()
    {
        hookedEnemyFps = null;
        hookedEnemyController = null;
        hookedWall = null;

        // Aim with this piece's own camera (never Camera.main: AI pieces use the skill too).
        Transform aim = fps != null && fps.cameraPoint != null ? fps.cameraPoint : transform;
        Ray ray = new Ray(aim.position, aim.forward);

        int mask = mapMask | enemyMask | wallMask;
        RaycastHit[] hits = Physics.RaycastAll(ray, maxRange, mask, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (RaycastHit hit in hits)
        {
            // Skip our own colliders (body, weapon).
            if (hit.collider.transform.IsChildOf(transform))
                continue;

            int layer = 1 << hit.collider.gameObject.layer;
            targetPos = hit.point;

            if (!IsInsideArena(hit.point))
                break; // first real hit is outside the arena: miss

            // Rook walls are recognised by component (their collider sits on the Default layer).
            WallSegment wall = hit.collider.GetComponentInParent<WallSegment>();
            if (wall != null)
            {
                mode = GrappleMode.PullWall;
                hookedWall = wall;
                hasValidTarget = true;
                return;
            }

            if ((layer & enemyMask) != 0)
            {
                ChessPieceFPSController piece = SkillTargeting.PieceOf(hit.collider);
                if (piece == null || piece == fps)
                    continue;
                if (!SkillTargeting.IsAlive(piece))
                    continue;

                mode = GrappleMode.PullEnemy;
                hookedEnemyFps = piece;
                targetPos = piece.transform.position;
                hasValidTarget = true;
                return;
            }

            if ((layer & mapMask) != 0)
            {
                mode = GrappleMode.PullSelf;
                hasValidTarget = true;
                return;
            }
        }

        // Nothing usable: shoot to max range and retract.
        mode = GrappleMode.PullSelf;
        targetPos = ray.origin + ray.direction * maxRange;
        hasValidTarget = false;
    }

    // =========================
    // DAMAGE
    // =========================

    /// <summary>Hits the first piece touched while pulling. Returns true when the skill ended.</summary>
    private bool HandleImpact()
    {
        int count = Physics.OverlapSphereNonAlloc(transform.position, impactRadius, hitBuffer, enemyMask, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
        {
            Collider col = hitBuffer[i];
            if (!col) continue;

            ChessPieceFPSController otherFps = SkillTargeting.PieceOf(col);
            if (!SkillTargeting.IsAffectable(fps, otherFps))
                continue;

            IDamageable dmg = otherFps.GetComponent<IDamageable>();
            if (dmg == null || !hitCache.Add(dmg))
                continue;

            Vector3 travel = SkillTargeting.TravelDirection(transform.position, otherFps.transform.position, transform.forward);

            SkillTargeting.TryDealDamage(fps, dmg, new DamageInfo
            {
                amount = pullSpeed * impactDamageScale,
                hitPoint = col.ClosestPoint(transform.position),
                hitDirection = travel,
                source = gameObject,
                type = DamageType.Melee
            });

            // Push it away (allies too). Release our own hold first so its flight is not frozen.
            ReleaseHookedEnemy();
            InterruptMovementSkills(otherFps);
            otherFps.ForceStopAllMotion();

            Vector3 push = new Vector3(travel.x, 0f, travel.z);
            if (push.sqrMagnitude < 1e-4f) push = new Vector3(transform.forward.x, 0f, transform.forward.z);
            otherFps.AddExternalImpulse(push.normalized * impactImpulseForce);

            FinishActive();
            return true;
        }

        return false;
    }

    private void ResolveLanding()
    {
        int count = Physics.OverlapSphereNonAlloc(transform.position, landingRadius, hitBuffer, enemyMask, QueryTriggerInteraction.Ignore);
        var hitThisLanding = new HashSet<ChessPieceFPSController>();

        for (int i = 0; i < count; i++)
        {
            Collider col = hitBuffer[i];
            ChessPieceFPSController otherFps = SkillTargeting.PieceOf(col);
            if (!SkillTargeting.IsAffectable(fps, otherFps) || !hitThisLanding.Add(otherFps))
                continue;

            SkillTargeting.TryDealDamage(fps, otherFps.GetComponent<IDamageable>(), new DamageInfo
            {
                amount = pullSpeed * landingDamageScale,
                hitPoint = col.ClosestPoint(transform.position),
                hitDirection = Vector3.down, // slammed from above
                source = gameObject,
                type = DamageType.Melee
            });

            otherFps.AddExternalImpulse(Vector3.up * landingKnockUp);
        }
    }

    private void ReleaseHookedEnemy()
    {
        if (hookedEnemyFps == null)
            return;

        hookedEnemyFps.ReleaseMovementSuppression(this);
        hookedEnemyFps.SetGravityEnabled(this, true);
        hookedEnemyFps.EndForcedMove(this);
        hookedEnemyFps = null;
        hookedEnemyController = null;
    }

    private void Cleanup()
    {
        ReleaseHookedEnemy();

        if (!active) return;
        active = false;

        // While the bishop is being disabled/destroyed its pools go with it: do not touch them.
        if (!IsTearingDown)
        {
            if (anchorPool && anchor)
                anchorPool.Return(anchor);
            if (trailPool && trail)
                trailPool.Return(trail);
        }

        anchor = null;
        trail = null;
        trailLR = null;
        hookedWall = null;

        if (fps != null)
            fps.SetGravityEnabled(this, true);
    }
}
