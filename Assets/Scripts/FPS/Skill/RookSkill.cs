using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ROOK — charge. Dashes straight ahead (gravity off), leaves wall segments behind it,
/// rams pieces in the way (damage + knock-back). Two charging pieces (Rook/Knight) that meet
/// "clash": both are hurt, both charges stop, both bounce back.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class RookSkill : ActiveSkill, IMovementOverride
{
    [Header("Dash Settings")]
    [SerializeField] private float dashDistance = 8f;
    [SerializeField] private float dashSpeed = 20f;
    [SerializeField] private float impactDamage = 10f;
    [SerializeField] private float pushForce = 15f;

    [Header("Collision")]
    [SerializeField] private float obstacleCheckRadius = 0.5f;
    [SerializeField] private LayerMask obstacleMask;
    [SerializeField] private LayerMask enemyMask;

    [Header("Wall Segments")]
    [SerializeField] private WallSegment segmentPrefab;
    [SerializeField] private int segmentPool = 8;
    [SerializeField] private float segmentSpacing = 0.5f;
    [SerializeField] private float segmentLifetime = 10f;

    private CharacterController controller;
    private ChessPieceFPSController fps;

    private Vector3 dashDir;
    private float traveled;
    private bool dashActive;

    private float segmentLength;
    private Vector3 currentSegmentHeadPos;
    private float distanceSinceLastSegment;

    private readonly HashSet<IDamageable> damagedThisDash = new();
    private readonly List<WallSegment> activeSegments = new();
    private readonly Queue<WallSegment> pool = new();
    private readonly List<WallSegment> allSegments = new();
    private readonly Collider[] overlapBuffer = new Collider[16];

    // =========================
    // IMovementOverride
    // =========================
    public bool IsActive => dashActive;
    public Vector3 GetMovementDelta(float deltaTime) => Vector3.zero;
    public Vector3 GetChargeDirection() => dashDir;
    public void ForceCancelMovement() => StopDash();

    /// <summary>Damage this charge deals in a clash.</summary>
    public float ImpactDamage => impactDamage;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        InitPool();
    }

    private void OnDestroy()
    {
        // Walls live at the scene root: remove them with their Rook.
        foreach (var seg in allSegments)
            if (seg != null)
                Destroy(seg.gameObject);
        allSegments.Clear();
    }

    protected override float GetActiveDuration(in SkillContext ctx) => dashDistance / Mathf.Max(0.01f, dashSpeed);

    protected override void OnEnterActive(in SkillContext ctx)
    {
        fps = ctx.fps;

        Vector3 forward = ctx.transform != null ? ctx.transform.forward : transform.forward;
        forward.y = 0f;
        dashDir = forward.normalized;

        CacheSegmentLength();

        // No direction or no room to start: nothing happens and no cooldown is spent.
        if (dashDir.sqrMagnitude < 0.01f || !HasSpaceAhead(controller.transform.position, dashDir, segmentLength))
        {
            AbortActivation();
            return;
        }

        traveled = 0f;
        distanceSinceLastSegment = 0f;
        currentSegmentHeadPos = controller.transform.position;

        damagedThisDash.Clear();
        ClearActiveSegments();

        dashActive = true;
        fps?.SetGravityEnabled(this, false);
    }

    protected override void TickActive(in SkillContext ctx)
    {
        if (!dashActive || ctx.controller == null)
            return;

        float step = dashSpeed * ctx.deltaTime;

        if (!HasSpaceAhead(ctx.transform.position, dashDir, step))
        {
            StopDash();
            return;
        }

        ctx.controller.Move(dashDir * step);
        traveled += step;
        distanceSinceLastSegment += step;

        HandlePieceCollision();
        HandleWallSpawn();

        if (traveled >= dashDistance)
            StopDash();
    }

    protected override void OnExitActive(in SkillContext ctx) => StopDash();
    protected override void OnForceCancel(in SkillContext ctx) => StopDash();

    // =========================
    // CLASH (two charges meet)
    // =========================
    private void ResolveSkillClash(ChessPieceFPSController otherFps, ActiveSkill opposingSkill)
    {
        if (!dashActive || otherFps == null)
            return;

        Vector3 otherDirection = Vector3.zero;
        float otherImpact = impactDamage;

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

        // Each side takes the OTHER charge's impact (never between allies).
        SkillTargeting.TryDealDamage(fps, otherFps.GetComponent<IDamageable>(), new DamageInfo
        {
            amount = impactDamage,
            hitPoint = otherFps.transform.position,
            hitDirection = dashDir,
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

        opposingSkill.ForceCancel();

        fps.InterruptActiveMovementSkills();
        otherFps.InterruptActiveMovementSkills();

        fps.AddExternalImpulse(otherDirection.normalized * pushForce);
        otherFps.AddExternalImpulse(dashDir.normalized * pushForce);
    }

    private void HandlePieceCollision()
    {
        if (!dashActive)
            return;

        int count = Physics.OverlapSphereNonAlloc(
            controller.transform.position, 1f, overlapBuffer, enemyMask, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
        {
            if (!dashActive)
                return;

            Collider col = overlapBuffer[i];
            if (col == null || col.GetComponent<WallSegment>() != null)
                continue;

            ChessPieceFPSController otherFps = SkillTargeting.PieceOf(col);
            if (!SkillTargeting.IsAffectable(fps, otherFps))
                continue;

            // Is the other piece charging too (Knight/Rook)?
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

            if (opposingMovementSkill != null)
            {
                ResolveSkillClash(otherFps, opposingMovementSkill);
                return;
            }

            IDamageable dmg = otherFps.GetComponent<IDamageable>();
            if (dmg == null || !damagedThisDash.Add(dmg))
                continue;

            // Knock-back applies to everyone (allies too); damage only to enemies.
            otherFps.InterruptActiveMovementSkills();
            otherFps.AddExternalImpulse(dashDir * pushForce);

            SkillTargeting.TryDealDamage(fps, dmg, new DamageInfo
            {
                amount = impactDamage,
                hitPoint = col.ClosestPoint(controller.transform.position),
                hitDirection = dashDir,
                source = gameObject,
                type = DamageType.Melee
            });
        }
    }

    // =========================
    // WALL SPAWN
    // =========================
    private void HandleWallSpawn()
    {
        if (!dashActive)
            return;

        float required = segmentLength + segmentSpacing;
        if (distanceSinceLastSegment < required)
            return;

        Vector3 center = currentSegmentHeadPos + dashDir * (segmentLength * 0.5f);
        SpawnSegmentAt(center);

        currentSegmentHeadPos = center + dashDir * ((segmentLength * 0.5f) + segmentSpacing);
        distanceSinceLastSegment = 0f;
    }

    private void SpawnSegmentAt(Vector3 centerPos)
    {
        if (!segmentPrefab) return;

        float deltaH = controller.transform.position.y - centerPos.y;
        centerPos += Vector3.up * deltaH;

        int count = Physics.OverlapSphereNonAlloc(centerPos, obstacleCheckRadius, overlapBuffer, obstacleMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            // Floors are fine; anything upright (a wall) blocks the segment.
            if (Vector3.Dot(overlapBuffer[i].transform.up, Vector3.up) > 0.35f)
                continue;
            return;
        }

        WallSegment seg = GetFromPool();
        if (seg == null) return;

        activeSegments.Add(seg);
        seg.Activate(centerPos, Quaternion.LookRotation(dashDir, Vector3.up), segmentLifetime);
    }

    // =========================
    // DASH STOP / CLEANUP
    // =========================
    private void StopDash()
    {
        if (!dashActive) return;

        dashActive = false;
        if (fps != null)
            fps.SetGravityEnabled(this, true);

        foreach (var seg in activeSegments)
            if (seg != null)
                seg.EnableGravity();

        traveled = 0f;
        distanceSinceLastSegment = 0f;
        damagedThisDash.Clear();
    }

    /// <summary>
    /// True when nothing taller than a step blocks the way. The sphere is cast just above
    /// step height (measured from the bottom of the collider, whatever the pivot), so floors
    /// and small bumps pass underneath while walls and pieces are hit.
    /// (Before: the hit point of a sphere cast from the pivot is always at pivot height, so
    /// every wall looked like a "step" and never stopped the charge.)
    /// </summary>
    private bool HasSpaceAhead(Vector3 origin, Vector3 dir, float distance)
    {
        float stepOffset = controller != null ? controller.stepOffset : 0.3f;
        float feetY = controller != null ? controller.bounds.min.y : origin.y;
        Vector3 castOrigin = new Vector3(origin.x, feetY + stepOffset + obstacleCheckRadius, origin.z);

        return !Physics.SphereCast(castOrigin, obstacleCheckRadius, dir, out _, distance, obstacleMask, QueryTriggerInteraction.Ignore);
    }

    private void CacheSegmentLength()
    {
        segmentLength = segmentPrefab ? Mathf.Max(0.1f, segmentPrefab.transform.localScale.z) : 1f;
    }

    // =========================
    // WALL POOL
    // =========================
    private void InitPool()
    {
        if (segmentPrefab == null)
            return;

        for (int i = 0; i < segmentPool; i++)
        {
            var seg = CreateSegment();
            seg.gameObject.SetActive(false);
            pool.Enqueue(seg);
        }
    }

    private WallSegment CreateSegment()
    {
        var seg = Instantiate(segmentPrefab);
        allSegments.Add(seg);
        return seg;
    }

    private WallSegment GetFromPool()
    {
        if (segmentPrefab == null) return null;

        WallSegment seg = null;
        while (pool.Count > 0 && seg == null)
            seg = pool.Dequeue();
        if (seg == null)
            seg = CreateSegment();

        seg.Init(this, fps != null ? fps.Team : Team.White);
        seg.gameObject.SetActive(true);
        return seg;
    }

    public void ReturnToPool(WallSegment seg)
    {
        if (seg == null) return;

        activeSegments.Remove(seg);
        seg.gameObject.SetActive(false);
        if (!pool.Contains(seg))
            pool.Enqueue(seg);
    }

    private void ClearActiveSegments()
    {
        if (activeSegments.Count == 0) return;

        WallSegment[] copy = activeSegments.ToArray();
        activeSegments.Clear();

        foreach (var seg in copy)
            if (seg != null)
                seg.ReturnToPool();
    }
}

static class VectorExt
{
    public static Vector3 Abs(this Vector3 v)
        => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
}
