using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ROOK — charge. Dashes straight ahead, following the ground like walking (steps, walkable
/// slopes; off a ledge it keeps its height and falls after the dash). Leaves wall segments
/// behind it. Rams pieces in the way: damage + billiard-style knock-back (pushed along the line
/// of impact, with a small bounce). Smashes through enemy walls it can break; anything else
/// that is too high stops it. Two charging pieces (Rook/Knight) that meet "clash": both are
/// hurt, both charges stop, both bounce off like billiard balls.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class RookSkill : ActiveSkill, IMovementOverride
{
    [Header("Dash Settings")]
    [SerializeField] private float dashDistance = 8f;
    [SerializeField] private float dashSpeed = 20f;
    [SerializeField] private float impactDamage = 10f;
    [SerializeField] private float pushForce = 15f;
    [Tooltip("Upward bounce of a collision = this x the impact strength (glancing hits barely bounce).")]
    [SerializeField] private float bounceFactor = 0.35f;

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
    private readonly List<WallSegment> allSegments = new();

    // =========================
    // IMovementOverride
    // =========================
    public bool IsActive => dashActive;
    public Vector3 GetMovementDelta(float deltaTime) => Vector3.zero;
    public Vector3 GetChargeDirection() => dashDir;
    public void ForceCancelMovement() => StopDash();

    /// <summary>Damage this charge deals in a clash.</summary>
    public float ImpactDamage => impactDamage;

    /// <summary>Knock-back strength of this charge (used by the billiard-style clash).</summary>
    public float PushForce => pushForce;

    // =========================
    // INITIALIZATION
    // =========================
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

        // Old walls go first, so they never block the new dash.
        ClearActiveSegments();

        // No direction or no room to start: nothing happens and no cooldown is spent.
        // (An enemy wall ahead does not count: the charge rams it when it gets there.)
        if (dashDir.sqrMagnitude < 0.01f ||
            (BlockedAhead(controller.transform.position, dashDir, segmentLength, out RaycastHit startBlocker) && !IsEnemyWall(startBlocker)))
        {
            AbortActivation();
            return;
        }

        traveled = 0f;
        distanceSinceLastSegment = 0f;
        currentSegmentHeadPos = controller.transform.position;

        damagedThisDash.Clear();

        dashActive = true;
        fps?.SetGravityEnabled(this, false);
    }

    protected override void TickActive(in SkillContext ctx)
    {
        if (!dashActive || ctx.controller == null)
            return;

        float step = dashSpeed * ctx.deltaTime;

        // Blocked: an enemy wall this charge can break is smashed and the dash goes on;
        // anything else (or a wall too strong to break, which still takes the hit) stops it.
        if (BlockedAhead(ctx.transform.position, dashDir, step, out RaycastHit blocker) && !TrySmashThrough(blocker))
        {
            FinishActive(); // stops the dash; the cooldown starts right away
            return;
        }

        ctx.controller.Move(dashDir * step);
        FollowGround(step);
        traveled += step;
        distanceSinceLastSegment += step;

        HandlePieceCollision();
        HandleWallSpawn();

        if (traveled >= dashDistance)
            FinishActive(); // stops the dash; the cooldown starts right away
    }

    protected override void OnExitActive(in SkillContext ctx) => StopDash();
    protected override void OnForceCancel(in SkillContext ctx) => StopDash();

    // =========================
    // ENEMY COLLISION
    // =========================
    private void ResolveSkillClash(ChessPieceFPSController otherFps, ActiveSkill opposingSkill)
    {
        if (!dashActive || otherFps == null)
            return;

        Vector3 otherDirection = Vector3.zero;
        float otherImpact = impactDamage;
        float otherForce = pushForce;

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

        // Both bounce off like billiard balls (head-on: both back; at an angle: each deflected).
        SkillTargeting.BilliardCollision(
            fps.transform.position, dashDir * pushForce,
            otherFps.transform.position, otherDirection.normalized * otherForce,
            bounceFactor, out Vector3 myBounce, out Vector3 otherBounce);
        fps.AddExternalImpulse(myBounce);
        otherFps.AddExternalImpulse(otherBounce);
    }

    private void HandlePieceCollision()
    {
        if (!dashActive)
            return;

        Collider[] hits = Physics.OverlapSphere(
            controller.transform.position, 1f, enemyMask, QueryTriggerInteraction.Ignore);
        int count = hits.Length;

        for (int i = 0; i < count; i++)
        {
            if (!dashActive)
                return;

            Collider col = hits[i];
            if (col == null || col.GetComponent<WallSegment>() != null)
                continue;

            ChessPieceFPSController otherFps = SkillTargeting.PieceOf(col);
            if (!SkillTargeting.IsAffectable(fps, otherFps))
                continue;

            // Is the other piece charging too (Knight/Rook)?
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
            IDamageable dmg = otherFps.GetComponent<IDamageable>();
            if (dmg == null || damagedThisDash.Contains(dmg))
                continue;

            // Billiard-style: pushed along the line from the rook to the piece, strength by the angle.
            float impact = SkillTargeting.BilliardCollision(
                controller.transform.position, dashDir * pushForce,
                otherFps.transform.position, Vector3.zero,
                bounceFactor, out _, out Vector3 knock);
            if (impact <= 0f)
                continue; // behind the charge: not hit
            damagedThisDash.Add(dmg);

            // Knock-back applies to everyone (allies too); damage only to enemies.
            otherFps.InterruptActiveMovementSkills();
            otherFps.AddExternalImpulse(knock);

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

        Collider[] hits = Physics.OverlapSphere(centerPos, obstacleCheckRadius, obstacleMask, QueryTriggerInteraction.Ignore);
        int count = hits.Length;
        for (int i = 0; i < count; i++)
        {
            // Floors are fine; anything upright (a wall) blocks the segment.
            if (Vector3.Dot(hits[i].transform.up, Vector3.up) > 0.35f)
                continue;
            return;
        }

        WallSegment seg = GetFromPool();
        if (seg == null) return;

        activeSegments.Add(seg);
        seg.Activate(centerPos, Quaternion.LookRotation(dashDir, Vector3.up), segmentLifetime);
        MovePiecesOutOf(seg);
    }

    /// <summary>A wall never spawns inside a piece: anyone standing there is moved out to the nearest side.</summary>
    private void MovePiecesOutOf(WallSegment seg)
    {
        var box = seg.GetComponent<BoxCollider>();
        if (box == null)
            return;

        Transform t = seg.transform;
        Vector3 s = t.lossyScale;
        Vector3 center = t.TransformPoint(box.center);
        Vector3 half = Vector3.Scale(box.size * 0.5f, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
        Vector3 right = t.right;

        var moved = new HashSet<ChessPieceFPSController>();
        foreach (Collider c in Physics.OverlapBox(center, half, t.rotation, enemyMask, QueryTriggerInteraction.Ignore))
        {
            ChessPieceFPSController piece = SkillTargeting.PieceOf(c);
            if (piece == null || piece == fps || !moved.Add(piece))
                continue;

            var pieceCc = piece.GetComponent<CharacterController>();
            float radius = pieceCc != null ? pieceCc.radius : 0.5f;
            float across = Vector3.Dot(piece.transform.position - center, right); // signed distance across the wall
            float side = across >= 0f ? 1f : -1f;

            Vector3 target = TargetBeside(piece, right, across, side, half.x, radius);
            if (!IsFreeFor(pieceCc, target))
            {
                Vector3 other = TargetBeside(piece, right, across, -side, half.x, radius);
                if (IsFreeFor(pieceCc, other))
                    target = other;
            }
            piece.WarpPosition(target);
        }
    }

    private static Vector3 TargetBeside(ChessPieceFPSController piece, Vector3 right, float across, float side, float halfWidth, float radius) =>
        piece.transform.position + right * (side * (halfWidth + radius + 0.05f) - across);

    private bool IsFreeFor(CharacterController cc, Vector3 pivot)
    {
        if (cc == null)
            return true;
        Vector3 c = pivot + cc.center + Vector3.up * 0.05f; // just off the floor it stands on
        float half = Mathf.Max(0f, cc.height * 0.5f - cc.radius);
        return !Physics.CheckCapsule(c + Vector3.up * half, c - Vector3.up * half, cc.radius, obstacleMask, QueryTriggerInteraction.Ignore);
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

    // =========================
    // OBSTACLE CHECK
    // =========================
    /// <summary>
    /// True when nothing taller than a step blocks the way. The sphere is cast just above
    /// step height (measured from the bottom of the collider, whatever the pivot), so floors
    /// and small bumps pass underneath while walls and pieces are hit.
    /// (Before: the hit point of a sphere cast from the pivot is always at pivot height, so
    /// every wall looked like a "step" and never stopped the charge.)
    /// </summary>
    private bool HasSpaceAhead(Vector3 origin, Vector3 dir, float distance) =>
        !BlockedAhead(origin, dir, distance, out _);

    /// <summary>Something too high to pass (not a step, not a walkable slope) is ahead; <paramref name="hit"/> says what.</summary>
    private bool BlockedAhead(Vector3 origin, Vector3 dir, float distance, out RaycastHit hit)
    {
        float stepOffset = controller != null ? controller.stepOffset : 0.3f;
        float feetY = controller != null ? controller.bounds.min.y : origin.y;
        Vector3 castOrigin = new Vector3(origin.x, feetY + stepOffset + obstacleCheckRadius, origin.z);

        if (!Physics.SphereCast(castOrigin, obstacleCheckRadius, dir, out hit, distance, obstacleMask, QueryTriggerInteraction.Ignore))
            return false;

        // A walkable slope is climbed like when walking.
        float slopeLimit = controller != null ? controller.slopeLimit : 45f;
        return Vector3.Angle(hit.normal, Vector3.up) > slopeLimit;
    }

    private bool IsEnemyWall(RaycastHit hit)
    {
        WallSegment wall = hit.collider != null ? hit.collider.GetComponentInParent<WallSegment>() : null;
        return wall != null && !SkillTargeting.IsAlly(fps, wall);
    }

    /// <summary>
    /// The charge hits an enemy wall: the wall always takes the impact damage; returns true when
    /// that breaks it (the dash goes on), false when it holds (the dash stops).
    /// </summary>
    private bool TrySmashThrough(RaycastHit hit)
    {
        if (!IsEnemyWall(hit))
            return false;

        WallSegment wall = hit.collider.GetComponentInParent<WallSegment>();
        bool breaks = impactDamage >= wall.CurrentHealth;
        SkillTargeting.TryDealDamage(fps, wall, new DamageInfo
        {
            amount = impactDamage,
            hitPoint = hit.point,
            hitDirection = dashDir,
            source = gameObject,
            type = DamageType.Melee
        });
        return breaks;
    }

    /// <summary>
    /// Over ground the charge follows it like walking (down steps and walkable slopes; up is
    /// handled by the controller). With no ground within reach (off a ledge, in the air) it keeps
    /// its height and falls once the dash is over.
    /// </summary>
    private void FollowGround(float step)
    {
        if (controller == null)
            return;

        float reach = controller.stepOffset + step * Mathf.Tan(controller.slopeLimit * Mathf.Deg2Rad);
        float r = controller.radius * 0.9f;
        Vector3 feet = new Vector3(transform.position.x, controller.bounds.min.y, transform.position.z);

        if (Physics.SphereCast(feet + Vector3.up * (r + 0.05f), r, Vector3.down, out RaycastHit ground, reach + 0.05f, obstacleMask, QueryTriggerInteraction.Ignore)
            && Vector3.Angle(ground.normal, Vector3.up) <= controller.slopeLimit)
        {
            controller.Move(Vector3.down * ground.distance);
        }
    }

    // =========================
    // SEGMENT LENGTH CACHE
    // =========================
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

