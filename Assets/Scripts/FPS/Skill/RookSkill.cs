using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ROOK — charge. Dashes straight ahead in a level line: it climbs steps and walkable slopes
/// but never goes down (downhill or off a ledge it keeps its height; it and its walls fall once
/// the dash is over). Leaves wall segments
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
    [Tooltip("A hit piece with a wall / steep terrain this close behind it (along the knock) cannot be knocked: the charge stops there (landing) and takes a push-back.")]
    [SerializeField] private float pushCheckDistance = 0.5f;
    [Tooltip("Push-back taken then: this share of the knock it gave (sideways reversed, upward the same).")]
    [Range(0f, 1f)]
    [SerializeField] private float blockedSelfKnockShare = 0.7f;

    [Header("Stop Blast")]
    [Tooltip("Stopped by terrain, a wall it cannot break or a piece it cannot knock: pieces within this radius (edge to edge) are knocked (pushForce) and hurt (once per dash). Not when the dash runs its full distance.")]
    [SerializeField] private float stopBlastRadius = 2.5f;
    [Tooltip("Pieces this close (share of the radius, edge to edge) take the full knock.")]
    [Range(0f, 1f)]
    [SerializeField] private float landingFullForceShare = 0.5f;
    [Tooltip("Share of the knock still taken at the very edge of the radius (fades linearly to it from the full zone).")]
    [Range(0f, 1f)]
    [SerializeField] private float landingMinForceShare = 0.5f;

    [Header("Collision")]
    [Tooltip("A piece is rammed when the gap between its body and the rook's is this small or less (edge to edge).")]
    [SerializeField] private float impactRadius = 0.5f;
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
    private Vector3 dashStartPos;   // where the line of walls starts
    private int segmentSlots;       // wall places used along the line so far (a blocked place counts too)

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
        // Only the first step is checked: an obstacle further ahead just stops the dash there.
        float firstStep = Mathf.Max(dashSpeed * ctx.deltaTime, 0.05f);
        if (dashDir.sqrMagnitude < 0.01f ||
            (BlockedAhead(controller.transform.position, dashDir, firstStep, out RaycastHit startBlocker) && !IsEnemyWall(startBlocker)))
        {
            AbortActivation();
            return;
        }

        traveled = 0f;
        dashStartPos = controller.transform.position;
        segmentSlots = 0;

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
        // A rook wall that stops it (either side) is knocked over, away from the rook.
        if (BlockedAhead(ctx.transform.position, dashDir, step, out RaycastHit blocker) && !TrySmashThrough(blocker))
        {
            WallSegment hitWall = blocker.collider != null ? blocker.collider.GetComponentInParent<WallSegment>() : null;
            if (hitWall != null && hitWall.gameObject.activeInHierarchy)
                hitWall.KnockOver(dashDir);
            StopBlast(); // forced to stop: the momentum knocks the pieces around (like a landing)
            FinishActive(); // stops the dash; the cooldown starts right away
            return;
        }

        ctx.controller.Move(dashDir * step); // level: never follows the ground down
        traveled += step;

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

        Collider[] hits = SkillTargeting.OverlapBody(controller, impactRadius, enemyMask); // edge to edge
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

            // Against a wall it cannot be knocked: the dash stops here (stop blast; hurt only once, by this hit)
            // and the rook takes a share of the knock back.
            bool blocked = SkillTargeting.CannotBePushed(otherFps, knock, pushCheckDistance);

            // Knock-back applies to everyone (allies too); damage only to enemies.
            if (!blocked)
            {
                otherFps.InterruptActiveMovementSkills();
                otherFps.AddExternalImpulse(knock);
            }

            SkillTargeting.TryDealDamage(fps, dmg, new DamageInfo
            {
                amount = impactDamage,
                hitPoint = SkillTargeting.ClosestPointOnBody(col, controller.transform.position),
                hitDirection = dashDir,
                source = gameObject,
                type = DamageType.Melee
            });

            if (blocked)
            {
                StopBlast();
                FinishActive();
                fps.AddExternalImpulse(SkillTargeting.BlockedReaction(knock, blockedSelfKnockShare), this);
                return;
            }
        }
    }

    // =========================
    // WALL SPAWN
    // =========================
    /// <summary>
    /// The charge is forced to stop (terrain, a wall, a piece it cannot knock): pieces around (edge to edge) are
    /// knocked away from the rook with pushForce, full close by and down to landingMinForceShare at stopBlastRadius
    /// (same rule as the landings), and hurt once per dash.
    /// </summary>
    private void StopBlast()
    {
        float myRadius = controller != null ? controller.radius : 0f;
        Vector3 from = transform.position;
        var knocked = new HashSet<ChessPieceFPSController>();
        foreach (Collider col in Physics.OverlapSphere(from, stopBlastRadius + myRadius + 2f, enemyMask, QueryTriggerInteraction.Ignore))
        {
            if (col == null || col.GetComponent<WallSegment>() != null)
                continue;
            ChessPieceFPSController otherFps = SkillTargeting.PieceOf(col);
            if (!SkillTargeting.IsAffectable(fps, otherFps) || knocked.Contains(otherFps))
                continue;

            float edgeDistance = SkillTargeting.EdgeDistanceXZ(from, myRadius, otherFps);
            if (edgeDistance > stopBlastRadius || Mathf.Abs(otherFps.transform.position.y - from.y) > stopBlastRadius + 2f)
                continue; // outside the blast
            knocked.Add(otherFps);

            Vector3 flat = otherFps.transform.position - from;
            flat.y = 0f;
            flat = flat.sqrMagnitude > 1e-6f ? flat.normalized : dashDir;
            float strength = pushForce * SkillTargeting.BlastForceShare(edgeDistance, stopBlastRadius, landingFullForceShare, landingMinForceShare);
            otherFps.InterruptActiveMovementSkills();
            otherFps.AddExternalImpulse(flat * strength + Vector3.up * (bounceFactor * strength));

            IDamageable dmg = otherFps.GetComponent<IDamageable>();
            if (dmg != null && damagedThisDash.Add(dmg)) // already rammed this dash: knocked again, not hurt again
                SkillTargeting.TryDealDamage(fps, dmg, new DamageInfo
                {
                    amount = impactDamage,
                    hitPoint = SkillTargeting.ClosestPointOnBody(col, from),
                    hitDirection = flat,
                    source = gameObject,
                    type = DamageType.Melee
                });
        }
    }

    /// <summary>
    /// Fills the way the rook really covered with walls, end to end (no limit on their number): every place a whole
    /// wall fits behind the rook gets one; a last bit too short for a whole wall stays empty.
    /// </summary>
    private void HandleWallSpawn()
    {
        if (!dashActive || controller == null)
            return;

        // Every wall sits a body-radius behind the rook, so it never spawns inside him
        // (the walls stay end to end; the whole line is just shifted back).
        float clearance = controller.radius + controller.skinWidth;
        float pitch = segmentLength + segmentSpacing;
        float covered = Vector3.Dot(controller.transform.position - dashStartPos, dashDir); // really travelled along the dash
        const float tolerance = 0.01f; // rounding: 29.9999 m of a 30 m dash still fits the last wall

        while (covered + tolerance >= segmentSlots * pitch + segmentLength)
        {
            Vector3 center = dashStartPos + dashDir * (segmentSlots * pitch + segmentLength * 0.5f);
            SpawnSegmentAt(center - dashDir * clearance);
            segmentSlots++;
        }
    }

    private void SpawnSegmentAt(Vector3 centerPos)
    {
        if (!segmentPrefab) return;

        float deltaH = controller.transform.position.y - centerPos.y;
        centerPos += Vector3.up * deltaH;

        // On a slope the ground under the wall's middle can be higher than the rook (dashing downhill):
        // stand the wall on it, so it does not sink into the slope.
        if (TryGroundUnder(centerPos, out float groundY) && groundY > centerPos.y)
            centerPos.y = groundY;

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

        HandleWallSpawn(); // the last whole wall that fits, if this frame's step made room for it
        dashActive = false;
        if (fps != null)
            fps.SetGravityEnabled(this, true);

        foreach (var seg in activeSegments)
            if (seg != null)
                seg.EnableGravity();

        traveled = 0f;
        segmentSlots = 0;
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
    /// The walkable ground under this point, close to the rook's height
    /// (walls and steep faces are skipped; of several surfaces, e.g. a tree top above, the one nearest in height wins).
    /// </summary>
    private bool TryGroundUnder(Vector3 point, out float groundY)
    {
        groundY = point.y;
        float reach = segmentLength;
        RaycastHit[] hits = Physics.RaycastAll(point + Vector3.up * reach, Vector3.down, reach * 2f, obstacleMask, QueryTriggerInteraction.Ignore);
        float slopeLimit = controller != null ? controller.slopeLimit : 45f;
        bool found = false;
        float best = float.MaxValue;
        foreach (var h in hits)
        {
            if (h.collider == null || h.collider.GetComponentInParent<WallSegment>() != null)
                continue;
            if (Vector3.Angle(h.normal, Vector3.up) > slopeLimit)
                continue;
            float gap = Mathf.Abs(h.point.y - point.y);
            if (gap < best)
            {
                best = gap;
                groundY = h.point.y;
                found = true;
            }
        }
        return found;
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
                seg.Break(); // a new dash: the old walls break apart
    }
}

