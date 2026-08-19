using System.Collections.Generic;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

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

    [Header("Landing")]
    [SerializeField] private float landingRadius = 1.5f;
    [SerializeField] private float landingDamageScale = 6f;
    [SerializeField] private float landingKnockUp = 6f;

    [Header("Pools")]
    [SerializeField] private BishopAnchorPool anchorPool;
    [SerializeField] private BishopTrailPool trailPool;

    private CharacterController controller;
    private ChessPieceFPSController fps;

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

    private Transform anchor;
    private Transform trail;
    private LineRenderer trailLR;

    private Transform hookedEnemy;
    private WallSegment hookedWall;

    private readonly HashSet<IDamageable> hitCache = new();
    private readonly Collider[] hitBuffer = new Collider[16];

    private Bounds arenaBounds;
    private bool hasArenaBounds;
    private static Bounds cachedArenaBounds;
    private static bool arenaBoundsCached;

    private bool hasValidTarget;

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

    private void ResolveArenaBounds()
    {
        if (arenaBoundsCached)
        {
            arenaBounds = cachedArenaBounds;
            hasArenaBounds = true;
            return;
        }

        var arenaController = FindFirstObjectByType<ArenaController>();
        if (!arenaController || !arenaController.arenaBoundary)
        {
            hasArenaBounds = false;
            return;
        }

        Transform t = arenaController.arenaBoundary;
        Vector3 size = new Vector3(
            Mathf.Abs(t.lossyScale.x),
            Mathf.Abs(t.lossyScale.y),
            Mathf.Abs(t.lossyScale.z)
        );

        arenaBounds = new Bounds(t.position, size);
        cachedArenaBounds = arenaBounds;
        arenaBoundsCached = true;
        hasArenaBounds = true;
    }

    protected override float GetActiveDuration(in SkillContext ctx) => maxDuration;

    protected override void OnEnterActive(in SkillContext ctx)
    {
        if (!AcquireTarget(out mode, out targetPos, out hookedEnemy, out hookedWall))
        {
            ForceCancel();
            return;
        }

        Transform visualOrigin = ropeStart ? ropeStart : transform;

        // === FIX: snapshot LOGIC origin ===
        ropeLogicOriginWS = visualOrigin.position;

        ropeTraveled = 0f;
        ropeArrived = false;
        isRetracting = false;

        traveled = 0f;
        pullStartPos = transform.position;

        totalDistance = Vector3.Distance(ropeLogicOriginWS, targetPos);
        if (totalDistance < 0.05f)
        {
            ForceCancel();
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

        // === KHÔNG khóa gravity / movement nữa ===
        fps.ClearOverrideMovement(MovementOverrideSource.Skill);
    }

    private bool IsInsideArena(Vector3 position)
    {
        if (!hasArenaBounds) return true;

        return
            position.x >= arenaBounds.min.x &&
            position.x <= arenaBounds.max.x &&
            position.z >= arenaBounds.min.z &&
            position.z <= arenaBounds.max.z;
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

                if (hasValidTarget)
                {
                    anchor = anchorPool ? anchorPool.Get() : null;
                    if (anchor)
                    {
                        anchor.position = targetPos;
                        fps.SetGravityEnabled(false);
                    }    
                }
                else
                {
                    isRetracting = true;
                }
            }

            return;
        }

        // =========================
        // ROPE RETRACT (MISS)
        // =========================
        if (isRetracting && !hasValidTarget)
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
            {
                ForceCancel();
            }

            return;
        }

        // =========================
        // PULL PHASE
        // =========================
        if (!hasValidTarget)
            return;

        float step = pullSpeed * ctx.deltaTime;
        traveled += step;

        float pullT = Mathf.Clamp01(traveled / totalDistance);

        if (mode == GrappleMode.PullEnemy && hookedEnemy != null)
        {
            Vector3 enemyNext = Vector3.Lerp(hookedEnemy.position, transform.position, pullT);
            if (!Physics.CheckSphere(enemyNext, 0.4f, obstacleMask))
            {
                hookedEnemy.position = enemyNext;
            }
        }
        else if (mode == GrappleMode.PullSelf)
        {
            Vector3 nextPos = Vector3.Lerp(pullStartPos, targetPos, pullT);
            Vector3 delta = nextPos - transform.position;
            Vector3 move = Vector3.Lerp(Vector3.zero, delta, 16f * ctx.deltaTime);
            controller.Move(move);
        }

        if (trailLR)
        {
            trailLR.SetPosition(0, visualOrigin);
            trailLR.SetPosition(1, targetPos);
        }

        HandleImpact();

        if (pullT >= 1f)
        {
            ResolveLanding();
            ForceCancel();
        }
    }

    protected override void OnForceCancel(in SkillContext ctx) => Cleanup();
    protected override void OnExitActive(in SkillContext ctx) => Cleanup();

    // =========================
    // TARGET ACQUIRE
    // =========================
    private bool AcquireTarget(
        out GrappleMode outMode,
        out Vector3 outTarget,
        out Transform outEnemy,
        out WallSegment outWall)
    {
        outEnemy = null;
        outWall = null;

        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);

        if (!Physics.Raycast(ray, out RaycastHit hit, maxRange))
        {
            outMode = GrappleMode.PullSelf;
            outTarget = ray.origin + ray.direction * maxRange;
            hasValidTarget = false;
            return true;
        }

        hasValidTarget = true;

        int layer = 1 << hit.collider.gameObject.layer;

        if ((layer & enemyMask) != 0)
        {
            outMode = GrappleMode.PullEnemy;
            outTarget = hit.collider.transform.position;
            outEnemy = hit.collider.transform;
            return true;
        }

        if ((layer & wallMask) != 0 && hit.collider.TryGetComponent(out outWall))
        {
            outMode = GrappleMode.PullWall;
            outTarget = hit.point;
            return true;
        }

        if ((layer & mapMask) != 0)
        {
            outMode = GrappleMode.PullSelf;
            outTarget = hit.point;
            return true;
        }

        outMode = GrappleMode.PullSelf;
        outTarget = hit.point;
        hasValidTarget = false;
        return true;
    }

    // =========================
    // DAMAGE
    // =========================
    private void HandleImpact()
    {
        int count = Physics.OverlapSphereNonAlloc(
            transform.position,
            impactRadius,
            hitBuffer,
            enemyMask);

        for (int i = 0; i < count; i++)
        {
            var col = hitBuffer[i];
            if (!col) continue;
            if (col.GetComponentInParent<ChessPieceFPSController>() == fps) continue;
            if (!col.TryGetComponent<IDamageable>(out var dmg)) continue;
            if (!hitCache.Add(dmg)) continue;

            dmg.TakeDamage(new DamageInfo
            {
                amount = pullSpeed * impactDamageScale,
                hitPoint = col.ClosestPoint(transform.position),
                hitDirection = (col.transform.position - transform.position).normalized,
                source = gameObject,
                type = DamageType.Melee
            });

            if (col.TryGetComponent<CharacterController>(out var cc))
                cc.Move((col.transform.position - transform.position).normalized * landingKnockUp * Time.deltaTime);
        }
    }

    private void ResolveLanding()
    {
        int count = Physics.OverlapSphereNonAlloc(
            transform.position,
            landingRadius,
            hitBuffer,
            enemyMask);

        for (int i = 0; i < count; i++)
        {
            var col = hitBuffer[i];
            if (col.GetComponentInParent<ChessPieceFPSController>() == fps) continue;
            if (!col.TryGetComponent<IDamageable>(out var dmg)) continue;

            dmg.TakeDamage(new DamageInfo
            {
                amount = pullSpeed * landingDamageScale,
                hitPoint = col.ClosestPoint(transform.position),
                hitDirection = Vector3.up,
                source = gameObject,
                type = DamageType.Melee
            });

            if (col.TryGetComponent<CharacterController>(out var cc))
                cc.Move(Vector3.up * landingKnockUp * Time.deltaTime);
        }
    }

    private void Cleanup()
    {
        if (!active) return;

        active = false;

        if (anchorPool && anchor)
            anchorPool.Return(anchor);

        if (trailPool && trail)
            trailPool.Return(trail);

        anchor = null;
        trail = null;
        trailLR = null;
        hookedEnemy = null;
        hookedWall = null;

        fps.SetGravityEnabled(true);
    }
}
