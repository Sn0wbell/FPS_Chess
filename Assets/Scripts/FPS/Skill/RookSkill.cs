using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

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

    // =========================
    // RUNTIME
    // =========================
    private CharacterController controller;
    private ChessPieceFPSController fps;

    private Vector3 dashDir;
    private float traveled;
    private bool dashActive;

    // WALL GEOMETRY
    private float segmentLength;
    private Vector3 currentSegmentHeadPos;
    private float distanceSinceLastSegment;

    private readonly HashSet<IDamageable> damagedThisDash = new();
    private readonly List<WallSegment> activeSegments = new();
    private readonly Queue<WallSegment> pool = new();

    // =========================
    // IMovementOverride
    // =========================
    public bool IsActive => dashActive;
    public Vector3 GetMovementDelta(float deltaTime) => Vector3.zero;
    public Vector3 GetChargeDirection() => dashDir;
    public void ForceCancelMovement() => StopDash();

    // =========================
    // INITIALIZATION
    // =========================
    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        blockMask = SkillBlockMask.Movement;
        InitPool();
    }

    protected override float GetActiveDuration(in SkillContext ctx)
        => dashDistance / dashSpeed;

    protected override void OnEnterActive(in SkillContext ctx)
    {
        fps = ctx.fps;
        dashDir = ctx.transform.forward.normalized;

        if (dashDir.sqrMagnitude < 0.01f)
            return;

        CacheSegmentLength();

        if (!HasSpaceAhead(controller.transform.position, dashDir, segmentLength))
            return;

        traveled = 0f;
        distanceSinceLastSegment = 0f;
        currentSegmentHeadPos = controller.transform.position;

        damagedThisDash.Clear();
        ClearActiveSegments();

        dashActive = true;

        fps?.SetGravityEnabled(false);
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

        HandleEnemyCollision();
        HandleWallSpawn();

        if (traveled >= dashDistance)
            StopDash();
    }

    protected override void OnExitActive(in SkillContext ctx) => StopDash();
    protected override void OnForceCancel(in SkillContext ctx) => StopDash();

    // =========================
    // ENEMY COLLISION
    // =========================
    private void ResolveSkillClash(
    ChessPieceFPSController otherFps,
    ActiveSkill opposingSkill)
    {
        if (!dashActive || otherFps == null)
            return;

        Vector3 otherDirection = Vector3.zero;

        if (opposingSkill is KnightSkill otherKnight)
            otherDirection = otherKnight.GetChargeDirection();
        else if (opposingSkill is RookSkill otherRook)
            otherDirection = otherRook.GetChargeDirection();

        if (otherDirection.sqrMagnitude < 0.001f)
            return;

        IDamageable myHealth = fps.GetComponent<IDamageable>();
        IDamageable otherHealth = otherFps.GetComponent<IDamageable>();

        if (otherHealth != null)
        {
            otherHealth.TakeDamage(new DamageInfo
            {
                amount = impactDamage,
                hitPoint = otherFps.transform.position,
                hitDirection = -dashDir,
                source = gameObject,
                type = DamageType.Melee
            });
        }

        if (myHealth != null)
        {
            float myDamage = impactDamage;

            if (opposingSkill is KnightSkill)
                myDamage = (8f / 0.6f) * 1f; // Không nên dùng giá trị hard-code này.

            myHealth.TakeDamage(new DamageInfo
            {
                amount = impactDamage,
                hitPoint = fps.transform.position,
                hitDirection = -otherDirection,
                source = otherFps.gameObject,
                type = DamageType.Melee
            });
        }

        opposingSkill.ForceCancel();

        fps.InterruptActiveMovementSkills();
        otherFps.InterruptActiveMovementSkills();

        fps.AddExternalImpulse(-otherDirection.normalized * pushForce);
        otherFps.AddExternalImpulse(-dashDir.normalized * pushForce);
    }
    private void HandleEnemyCollision()
    {
        if (!dashActive)
            return;

        var hits = Physics.OverlapSphere(
            controller.transform.position,
            1f,
            enemyMask,
            QueryTriggerInteraction.Ignore);

        foreach (var col in hits)
        {
            if (!dashActive)
                return;

            if (col.GetComponent<WallSegment>() != null)
                continue;

            if (!col.TryGetComponent<ChessPieceFPSController>(out var otherFps))
                continue;

            if (otherFps == fps)
                continue;

            // Kiểm tra đối phương đang dùng Knight/Rook.
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

            // CLASH.
            if (opposingMovementSkill != null)
            {
                ResolveSkillClash(otherFps, opposingMovementSkill);
                return;
            }

            // Va chạm thông thường.
            IDamageable dmg = col.GetComponent<IDamageable>();

            if (dmg == null || !damagedThisDash.Add(dmg))
                continue;

            Vector3 knockbackDirection = -dashDir;

            otherFps.InterruptActiveMovementSkills();
            otherFps.AddExternalImpulse(
                knockbackDirection.normalized * pushForce);

            dmg.TakeDamage(new DamageInfo
            {
                amount = impactDamage,
                hitPoint = col.ClosestPoint(controller.transform.position),
                hitDirection = knockbackDirection,
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
        var hits = Physics.OverlapSphere(centerPos, obstacleCheckRadius, obstacleMask, QueryTriggerInteraction.Ignore);

        foreach (var col in hits)
        {
            if (Vector3.Dot(col.transform.up, Vector3.up) > 0.35f)
                continue;

            return;
        }

        WallSegment seg = GetFromPool();
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
        fps?.SetGravityEnabled(true);

        foreach (var seg in activeSegments)
        {
            if (seg != null)
                seg.EnableGravity();
        }

        traveled = 0f;
        distanceSinceLastSegment = 0f;
        damagedThisDash.Clear();
    }

    // =========================
    // OBSTACLE CHECK
    // =========================
    private bool HasSpaceAhead(Vector3 origin, Vector3 dir, float distance)
    {
        if (!Physics.SphereCast(
            origin,
            obstacleCheckRadius,
            dir,
            out RaycastHit hit,
            distance,
            obstacleMask,
            QueryTriggerInteraction.Ignore))
            return true;

        if (controller == null)
            return false;

        float stepHeight = hit.point.y - controller.transform.position.y;

        if (stepHeight <= controller.stepOffset)
            return true;

        return false;
    }

    // =========================
    // SEGMENT LENGTH CACHE
    // =========================
    private void CacheSegmentLength()
    {
        if (!segmentPrefab)
        {
            segmentLength = 1f;
            return;
        }

        segmentLength = segmentPrefab.transform.localScale.z;
    }

    // =========================
    // WALL POOL
    // =========================
    private void InitPool()
    {
        for (int i = 0; i < segmentPool; i++)
        {
            var seg = Instantiate(segmentPrefab);
            seg.Init(this);
            seg.gameObject.SetActive(false);
            pool.Enqueue(seg);
        }
    }

    private WallSegment GetFromPool()
    {
        WallSegment seg = pool.Count > 0
            ? pool.Dequeue()
            : Instantiate(segmentPrefab);

        seg.Init(this);
        seg.gameObject.SetActive(true);
        return seg;
    }

    public void ReturnToPool(WallSegment seg)
    {
        activeSegments.Remove(seg);
        seg.gameObject.SetActive(false);
        pool.Enqueue(seg);
    }

    private void ClearActiveSegments()
    {
        if (activeSegments.Count == 0) return;

        WallSegment[] copy = activeSegments.ToArray();
        activeSegments.Clear();

        foreach (var seg in copy)
            seg?.ReturnToPool();
    }
}

// =========================
// HELPER EXT
// =========================
static class VectorExt
{
    public static Vector3 Abs(this Vector3 v)
        => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
}
