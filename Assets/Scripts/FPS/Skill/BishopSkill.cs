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

    // Rope phase
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

        if (mode == GrappleMode.PullEnemy && hookedEnemyFps != null)
            // Luôn thao tác trên root của enemy.
            hookedEnemyController = hookedEnemyFps.GetComponent<CharacterController>();

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

        // =========================
        // ROPE SHOOT
        // =========================
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

                fps.SetGravityEnabled(this, false);
            }

            return;
        }

        // =========================
        // ROPE RETRACT (MISS)
        // =========================
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

        // =========================
        // PULL PHASE
        // =========================

        // Enemy pull follows rope retract speed.
        // Self pull keeps its original pull speed.
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

            if (moveDelta.sqrMagnitude > 0.0001f && !Physics.CheckSphere(enemyNext, 0.4f, obstacleMask))
                hookedEnemyController.Move(moveDelta);
        }
        else // PullSelf
        {
            Vector3 nextPos = Vector3.Lerp(pullStartPos, targetPos, pullT);
            Vector3 delta = nextPos - transform.position;
            controller.Move(Vector3.Lerp(Vector3.zero, delta, Mathf.Clamp01(16f * ctx.deltaTime)));
        }

        if (trailLR)
        {
            trailLR.SetPosition(0, visualOrigin);
            trailLR.SetPosition(1, targetPos);
        }

        if (HandleImpact())
            return; // impact ends the skill

        if (pullT >= 1f)
        {
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

        // Chỉ ngắt skill trực tiếp điều khiển movement.
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

        RaycastHit[] hits = Physics.RaycastAll(ray, maxRange);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        WeaponController heldWeapon = fps != null ? fps.GetCurrentWeapon() : null;
        bool touchedSomething = false;

        foreach (RaycastHit hit in hits)
        {
            // Skip our own colliders and the weapon we are holding.
            if (hit.collider.transform.IsChildOf(transform) ||
                (heldWeapon != null && hit.collider.transform.IsChildOf(heldWeapon.transform)))
                continue;

            // The first other thing the rope touches decides (anything else is a miss).
            touchedSomething = true;
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
                if (piece == null || piece == fps || !SkillTargeting.IsAlive(piece))
                    break; // nothing to pull: miss

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

            break; // touched something that is not a target: miss
        }

        // Miss: the rope goes to what it touched (or to max range) and retracts.
        mode = GrappleMode.PullSelf;
        if (!touchedSomething)
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
            // Đảm bảo mỗi enemy chỉ nhận impact một lần trong một lần kích hoạt.
            if (dmg == null || !hitCache.Add(dmg))
                continue;

            Vector3 travel = SkillTargeting.TravelDirection(transform.position, otherFps.transform.position, transform.forward);

            // Giữ nguyên damage formula hiện tại.
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
            // Ngắt riêng các skill đang trực tiếp điều khiển chuyển động.
            InterruptMovementSkills(otherFps);
            // Dừng chuyển động hiện tại trước khi áp dụng lực đẩy.
            otherFps.ForceStopAllMotion();

            // Đẩy enemy ra xa Bishop theo phương ngang.
            Vector3 push = new Vector3(travel.x, 0f, travel.z);
            if (push.sqrMagnitude < 1e-4f) push = new Vector3(transform.forward.x, 0f, transform.forward.z);
            otherFps.AddExternalImpulse(push.normalized * impactImpulseForce);

            // Bishop không tiếp tục kéo enemy sau khi impact đã xảy ra.
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
                hitDirection = Vector3.up,
                source = gameObject,
                type = DamageType.Melee
            });

            CharacterController cc = otherFps.GetComponent<CharacterController>();
            if (cc != null)
                cc.Move(Vector3.up * landingKnockUp * Time.deltaTime);
        }
    }

    private void ReleaseHookedEnemy()
    {
        if (hookedEnemyFps == null)
            return;

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
