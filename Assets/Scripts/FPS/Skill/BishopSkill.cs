using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// BISHOP — grapple hook, aimed with the piece's own camera. The rope only touches the
/// Default, ChessPieces and Wall layers (never triggers).
///  - Map hit   : the bishop is pulled to the point; pieces met on the way are hit once
///                (billiard knock-back) and stop the pull; on arrival a landing blast
///                (fading with distance, small bounce) hits the pieces around.
///  - Piece hit : the piece (any side) loses its leap / dash, cannot move and has no gravity
///                while it is pulled to the bishop; when it reaches the bishop both bounce
///                off like billiard balls and only the pulled piece takes damage.
///  - Rook wall : the wall is pulled over (lies flat) to open a line of fire.
///  - Miss / out of the arena / the piece left the hook point: the rope retracts.
/// Whatever is pulled slides along the ground and walls; hitting them nearly head-on
/// (angle >= headOnAngle) stops the skill. A rook wall raised across a hooked rope cuts it.
/// A new obstacle in front of the flying rope is hooked instead of the original target.
/// Every piece is hit (and damaged) at most once per use: the first hit ends the pull.
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
    [Tooltip("Damage of a hit: on the way, when a pulled piece arrives and by the landing blast (each piece takes it once per use).")]
    [SerializeField] private float impactDamage = 25f;
    [SerializeField] private float impactImpulseForce = 8f;
    [Tooltip("When a pulled piece reaches the bishop, the bishop takes this share of the piece's knock (sideways and upward).")]
    [Range(0f, 1f)]
    [SerializeField] private float arrivalSelfKnockShare = 0.7f;
    [Tooltip("Upward bounce of a hit = this x the impact strength (glancing hits barely bounce).")]
    [SerializeField] private float bounceFactor = 0.35f;

    [Header("Landing")]
    [SerializeField] private float landingRadius = 1.5f;

    [Header("Sliding")]
    [Tooltip("Hitting the ground or a wall at this angle or more (90 = straight into it) stops the pull; below it the pulled body slides along, slower the steeper the hit.")]
    [Range(0f, 90f)]
    [SerializeField] private float headOnAngle = 80f;

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
    private bool hookedEnemyHeld; // forced move claimed, movement suppressed, gravity off
    private GrappleMode mode;
    private bool active;

    private Vector3 targetPos;
    private Vector3 ropeLogicOriginWS;

    private float totalDistance;

    // Rope phase
    private float ropeTraveled;
    private bool ropeArrived;
    private bool isRetracting;
    private bool hasValidTarget;

    private Collider hookedSurface; // the map / wall collider the rope hooked (self pull)
    private bool hookedGround;      // ...and it is walkable ground: the bishop lands right on the hook point
    private readonly HashSet<ChessPieceFPSController> hitThisUse = new HashSet<ChessPieceFPSController>(); // damaged once per use
    private int stalledFrames; // self pull making no progress (pressed against a piece that cannot move)

    private Transform anchor;
    private Transform trail;
    private LineRenderer trailLR;
    private WallSegment hookedWall;


    private Bounds arenaBounds;
    private bool hasArenaBounds;

    public bool IsActive => active;
    public Vector3 GetMovementDelta(float deltaTime) => Vector3.zero;
    public void ForceCancelMovement() => ForceCancel();

    private int RopeMask => mapMask | enemyMask | wallMask;

    public override void Initialize(in SkillContext ctx)
    {
        base.Initialize(ctx);
        controller = ctx.controller;
        fps = ctx.fps;
    }

    protected override float GetActiveDuration(in SkillContext ctx) => maxDuration;

    protected override void OnEnterActive(in SkillContext ctx)
    {
        // Same arena area as the out-of-bounds rule (single source).
        hasArenaBounds = ArenaController.Current != null && ArenaController.Current.TryGetBounds(out arenaBounds);
        hitThisUse.Clear();
        stalledFrames = 0;

        AcquireTarget();

        Transform visualOrigin = ropeStart ? ropeStart : transform;
        ropeLogicOriginWS = visualOrigin.position;

        ropeTraveled = 0f;
        ropeArrived = false;
        isRetracting = false;

        totalDistance = Vector3.Distance(ropeLogicOriginWS, targetPos);
        if (totalDistance < 0.05f)
        {
            ReleaseHookedEnemy();
            AbortActivation();
            return;
        }

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
        return ArenaController.ContainsXZ(arenaBounds, position);
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
            float prevT = Mathf.Clamp01(ropeTraveled / totalDistance);
            ropeTraveled += ropeShootSpeed * ctx.deltaTime;
            float t = Mathf.Clamp01(ropeTraveled / totalDistance);
            Vector3 prevHead = Vector3.Lerp(ropeLogicOriginWS, targetPos, prevT);
            Vector3 ropeHead = Vector3.Lerp(ropeLogicOriginWS, targetPos, t);

            // Something new in front of the flying rope: it hooks that instead.
            if (t < 1f && TryFirstRopeHit(prevHead, ropeHead, out RaycastHit blocker))
            {
                ClassifyHit(blocker);
                ropeHead = targetPos;
                t = 1f;
            }

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

                // The piece is no longer where the rope was aimed: a miss.
                if (hasValidTarget && mode == GrappleMode.PullEnemy && !PieceStillAtHook())
                    hasValidTarget = false;

                if (!hasValidTarget)
                {
                    hookedEnemyFps = null;
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

                if (mode == GrappleMode.PullEnemy && !HoldHookedPiece())
                {
                    // Already being moved by another skill: the rope slips.
                    hasValidTarget = false;
                    isRetracting = true;
                    return;
                }

                anchor = anchorPool ? anchorPool.Get() : null;
                if (anchor)
                {
                    anchor.SetParent(null, true); // stays at the hook point in the world, whatever the bishop does
                    anchor.position = targetPos;
                }

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
        if (mode == GrappleMode.PullEnemy)
            TickPullPiece(ctx.deltaTime, visualOrigin);
        else
            TickPullSelf(ctx.deltaTime, visualOrigin);
    }

    private void TickPullSelf(float dt, Vector3 visualOrigin)
    {
        if (RopeCutByRookWall(visualOrigin, targetPos))
        {
            FinishActive();
            return;
        }

        Vector3 before = transform.position;
        if (!PullTowards(controller, targetPos, pullSpeed, dt, hookedSurface, out bool arrived, landOnGoal: hookedGround))
        {
            FinishActive(); // head-on into the ground / a wall: the pull stops
            return;
        }

        if (trailLR)
        {
            trailLR.SetPosition(0, ropeStart ? ropeStart.position : transform.position);
            trailLR.SetPosition(1, targetPos);
        }

        Vector3 velocity = dt > 0f ? (transform.position - before) / dt : Vector3.zero;
        HitPiecesOnTheWay(velocity); // pieces in the way are knocked aside; the pull goes on

        // Pressed head-on against a piece that cannot be pushed away (e.g. against a wall): stops like on terrain.
        float expected = pullSpeed * dt;
        stalledFrames = !arrived && expected > 1e-4f && (transform.position - before).magnitude < expected * 0.05f ? stalledFrames + 1 : 0;
        if (stalledFrames >= 3)
        {
            FinishActive();
            return;
        }

        if (arrived)
        {
            ResolveLanding();
            FinishActive();
        }
    }

    private void TickPullPiece(float dt, Vector3 visualOrigin)
    {
        if (!SkillTargeting.IsAlive(hookedEnemyFps) || hookedEnemyController == null)
        {
            FinishActive();
            return;
        }

        Transform piece = hookedEnemyFps.transform;
        if (RopeCutByRookWall(visualOrigin, hookedEnemyController.bounds.center))
        {
            FinishActive();
            return;
        }

        if (!PullTowards(hookedEnemyController, transform.position, ropeShootSpeed, dt, null, out _))
        {
            FinishActive(); // head-on into the ground / a wall: the pull stops
            return;
        }

        if (trailLR)
        {
            trailLR.SetPosition(0, visualOrigin);
            trailLR.SetPosition(1, hookedEnemyController.bounds.center);
        }

        // Reached the bishop: both bounce off like two charges meeting; only the pulled piece is hurt.
        if (Vector3.Distance(piece.position, transform.position) <= impactRadius + hookedEnemyController.radius)
            ResolvePulledPieceArrival();
    }

    protected override void OnForceCancel(in SkillContext ctx) => Cleanup();
    protected override void OnExitActive(in SkillContext ctx) => Cleanup();

    // =========================
    // PULL MOTION (slides along the ground and walls)
    // =========================

    /// <summary>
    /// Moves <paramref name="cc"/> one step towards <paramref name="goal"/>. Against the ground or a
    /// wall it slides along the surface, slower the steeper it hits; at headOnAngle or more it stops.
    /// Returns false when blocked head-on. The hooked surface itself is never "head-on": touching it
    /// near the hook point is the arrival, elsewhere it is slid along.
    /// </summary>
    private bool PullTowards(CharacterController cc, Vector3 goal, float speed, float dt, Collider goalSurface, out bool arrived, bool landOnGoal = false)
    {
        Transform t = cc.transform;
        Vector3 toGoal = goal - t.position;
        float distance = toGoal.magnitude;
        // Hooked ground: the feet land right on the hook point. Otherwise (a wall, a piece): the body reaches it.
        float arriveDistance = landOnGoal ? 0.1f + cc.skinWidth : cc.radius + 0.1f;
        arrived = distance <= arriveDistance;
        if (arrived || dt <= 0f)
            return true;

        Vector3 dir = toGoal / distance;
        float step = Mathf.Min(speed * dt, distance);

        Vector3 move = dir * step;
        Vector3 center = cc.bounds.center;
        float half = Mathf.Max(0f, cc.height * 0.5f - cc.radius);
        float castRadius = cc.radius * 0.95f;
        if (Physics.CapsuleCast(center + Vector3.up * half, center - Vector3.up * half, castRadius, dir, out RaycastHit surface,
                step + cc.skinWidth + 0.05f, obstacleMask, QueryTriggerInteraction.Ignore))
        {
            Vector3 n = surface.normal;
            bool hookedOne = goalSurface != null && surface.collider == goalSurface;
            // The hooked surface right ahead, next to the hook point (the gap left counts): arrived.
            // (Hooked ground: not yet, the feet go on to the hook point itself.)
            if (hookedOne && !landOnGoal && Vector3.Distance(SkillTargeting.ClosestPointOnBody(cc, goal), goal) <= surface.distance + 0.3f)
            {
                arrived = true; // reached the hook point
                return true;
            }

            float angle = Mathf.Asin(Mathf.Clamp01(Vector3.Dot(dir, -n))) * Mathf.Rad2Deg; // 0 = along it, 90 = straight in
            if (angle >= headOnAngle && !hookedOne)
                return false;

            Vector3 along = dir - Vector3.Dot(dir, n) * n; // length = cos(angle)
            move = along * step;                            // slides, speed x cos(angle)
        }

        Vector3 before = t.position;
        cc.Move(move);
        float left = Vector3.Distance(t.position, goal);
        arrived = left <= arriveDistance;
        // Landing on the ground but something keeps the feet off the exact point: close enough, it has landed.
        if (!arrived && landOnGoal && left <= cc.radius + 0.1f && (t.position - before).sqrMagnitude < 1e-4f)
            arrived = true;
        return true;
    }

    /// <summary>A rook wall raised across the hooked rope cuts it.</summary>
    private bool RopeCutByRookWall(Vector3 from, Vector3 to)
    {
        Vector3 d = to - from;
        float len = d.magnitude;
        if (len < 0.05f)
            return false;

        foreach (RaycastHit hit in Physics.RaycastAll(from, d / len, len - 0.05f, obstacleMask | wallMask, QueryTriggerInteraction.Ignore))
        {
            WallSegment wall = hit.collider.GetComponentInParent<WallSegment>();
            if (wall != null && wall != hookedWall)
                return true;
        }
        return false;
    }

    private bool PieceStillAtHook()
    {
        if (hookedEnemyFps == null || !SkillTargeting.IsAlive(hookedEnemyFps))
            return false;
        Collider c = hookedEnemyController != null ? hookedEnemyController : hookedEnemyFps.GetComponent<Collider>();
        Vector3 closest = c != null ? SkillTargeting.ClosestPointOnBody(c, targetPos) : hookedEnemyFps.transform.position;
        return Vector3.Distance(closest, targetPos) <= impactRadius;
    }

    /// <summary>The pulled piece loses its own leap / dash, cannot move and has no gravity while pulled.</summary>
    private bool HoldHookedPiece()
    {
        if (hookedEnemyFps == null || !hookedEnemyFps.TryBeginForcedMove(this))
            return false;

        hookedEnemyFps.SuppressMovement(this);
        hookedEnemyFps.SetGravityEnabled(this, false);
        hookedEnemyHeld = true;
        return true;
    }

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

        if (TryFirstRopeHit(ray.origin, ray.origin + ray.direction * maxRange, out RaycastHit hit))
        {
            ClassifyHit(hit);
            return;
        }

        // Miss: the rope goes to max range and retracts.
        mode = GrappleMode.PullSelf;
        targetPos = ray.origin + ray.direction * maxRange;
        hasValidTarget = false;
    }

    /// <summary>
    /// First thing the rope touches between two points: Default / ChessPieces / Wall layers only,
    /// no triggers, never the bishop itself or the weapon it is holding.
    /// </summary>
    private bool TryFirstRopeHit(Vector3 from, Vector3 to, out RaycastHit first)
    {
        first = default;
        Vector3 d = to - from;
        float len = d.magnitude;
        if (len < 1e-4f)
            return false;

        RaycastHit[] hits = Physics.RaycastAll(from, d / len, len, RopeMask, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        WeaponController heldWeapon = fps != null ? fps.GetCurrentWeapon() : null;

        foreach (RaycastHit hit in hits)
        {
            // Skip our own colliders and the weapon we are holding.
            if (hit.collider.transform.IsChildOf(transform) ||
                (heldWeapon != null && hit.collider.transform.IsChildOf(heldWeapon.transform)))
                continue;

            first = hit;
            return true;
        }
        return false;
    }

    /// <summary>What the rope touched decides the mode (and the hook point).</summary>
    private void ClassifyHit(RaycastHit hit)
    {
        hookedEnemyFps = null;
        hookedEnemyController = null;
        hookedWall = null;
        hookedSurface = hit.collider;
        hookedGround = Vector3.Angle(hit.normal, Vector3.up) <= (controller != null ? controller.slopeLimit : 45f);

        int layer = 1 << hit.collider.gameObject.layer;
        targetPos = hit.point;
        mode = GrappleMode.PullSelf;
        hasValidTarget = false;

        if (!IsInsideArena(hit.point))
            return; // outside the arena: miss

        // Rook walls are recognised by component (their collider sits on the Default layer).
        WallSegment wall = hit.collider.GetComponentInParent<WallSegment>();
        if (wall != null)
        {
            mode = GrappleMode.PullWall;
            hookedWall = wall;
            hasValidTarget = true;
            return;
        }

        // Any piece, either side.
        if ((layer & enemyMask) != 0)
        {
            ChessPieceFPSController piece = SkillTargeting.PieceOf(hit.collider);
            if (piece == null || piece == fps || !SkillTargeting.IsAlive(piece))
                return; // nothing to pull: miss

            mode = GrappleMode.PullEnemy;
            hookedEnemyFps = piece;
            // Luôn thao tác trên root của enemy.
            hookedEnemyController = piece.GetComponent<CharacterController>();
            hasValidTarget = true;
            return;
        }

        if ((layer & mapMask) != 0)
            hasValidTarget = true; // PullSelf
    }

    // =========================
    // DAMAGE
    // =========================

    /// <summary>Self pull: pieces met on the way are hit once (billiard). Returns true when the pull ended.</summary>
    /// <summary>
    /// Self pull: a piece in front of the bishop is knocked away like by a landing at contact (same knock in every
    /// case) and hurt once per use. The bishop is not pushed: it keeps following its rope (a piece that cannot be
    /// pushed away, e.g. against a wall, is slid along like terrain).
    /// </summary>
    private void HitPiecesOnTheWay(Vector3 velocity)
    {
        Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
        if (flat.sqrMagnitude < 1e-6f)
            return;

        foreach (Collider col in Physics.OverlapSphere(transform.position, impactRadius, enemyMask, QueryTriggerInteraction.Ignore))
        {
            ChessPieceFPSController otherFps = SkillTargeting.PieceOf(col);
            if (!SkillTargeting.IsAffectable(fps, otherFps) || hitThisUse.Contains(otherFps))
                continue;

            Vector3 toPiece = otherFps.transform.position - transform.position;
            toPiece.y = 0f;
            if (Vector3.Dot(toPiece, flat) <= 0f)
                continue; // not in front of the bishop: not hit
            hitThisUse.Add(otherFps);

            // Ngắt riêng các skill đang trực tiếp điều khiển chuyển động.
            InterruptMovementSkills(otherFps);
            otherFps.AddExternalImpulse(ContactKnock(toPiece, flat));
            Damage(otherFps, SkillTargeting.ClosestPointOnBody(col, transform.position), SkillTargeting.TravelDirection(transform.position, otherFps.transform.position, transform.forward));
        }
    }

    /// <summary>The knock a piece gets from the bishop's impacts at contact (same as a landing blast at contact).</summary>
    private Vector3 ContactKnock(Vector3 away, Vector3 fallback)
    {
        away.y = 0f;
        if (away.sqrMagnitude < 1e-6f)
            away = new Vector3(fallback.x, 0f, fallback.z);
        Vector3 dir = away.sqrMagnitude > 1e-6f ? away.normalized : Vector3.forward;
        return dir * impactImpulseForce + Vector3.up * (bounceFactor * impactImpulseForce);
    }

    /// <summary>The pulled piece reached the bishop: both bounce like two charges meeting; only the piece is hurt.</summary>
    private void ResolvePulledPieceArrival()
    {
        ChessPieceFPSController piece = hookedEnemyFps;
        Vector3 toPiece = piece.transform.position - transform.position;
        toPiece.y = 0f;
        Vector3 n = toPiece.sqrMagnitude > 1e-6f ? toPiece.normalized : transform.forward;

        // Same impact as when the bishop's own pull hits a piece (impactImpulseForce, not two charges adding up):
        // the piece is knocked away, the bishop takes a share of the same knock back.
        Vector3 pieceBounce = ContactKnock(n, n);
        Vector3 myBounce = ContactKnock(-n, -n) * arrivalSelfKnockShare;

        Damage(piece, piece.transform.position, -n);

        ReleaseHookedEnemy();   // its movement and gravity are given back before the bounce
        FinishActive();
        piece.AddExternalImpulse(pieceBounce);
        fps.AddExternalImpulse(myBounce); // e.g. the piece flies 8, the bishop 4 (and half as high)
    }

    /// <summary>
    /// Self pull arrival: blast around the bishop, measured from the bishop's body to the other body
    /// (edge to edge): full strength when touching, fading to nothing at landingRadius; damage once per use.
    /// </summary>
    private void ResolveLanding()
    {
        var hitThisLanding = new HashSet<ChessPieceFPSController>();
        float myRadius = controller != null ? controller.radius : 0f;

        foreach (Collider col in Physics.OverlapSphere(transform.position, landingRadius + myRadius + 2f, enemyMask, QueryTriggerInteraction.Ignore))
        {
            ChessPieceFPSController otherFps = SkillTargeting.PieceOf(col);
            if (!SkillTargeting.IsAffectable(fps, otherFps) || hitThisLanding.Contains(otherFps))
                continue;

            Vector3 flat = otherFps.transform.position - transform.position;
            flat.y = 0f;
            float distance = flat.magnitude;
            flat = distance > 1e-3f ? flat / distance : new Vector3(transform.forward.x, 0f, transform.forward.z).normalized;

            var otherCc = otherFps.GetComponent<CharacterController>();
            float edgeDistance = Mathf.Max(0f, distance - myRadius - (otherCc != null ? otherCc.radius : 0f));
            if (edgeDistance > landingRadius || Mathf.Abs(otherFps.transform.position.y - transform.position.y) > landingRadius + 2f)
                continue; // outside the blast
            hitThisLanding.Add(otherFps);

            float strength = impactImpulseForce * (1f - Mathf.Clamp01(edgeDistance / Mathf.Max(0.01f, landingRadius)));
            otherFps.AddExternalImpulse(flat * strength + Vector3.up * (bounceFactor * strength));

            if (hitThisUse.Add(otherFps)) // already hit on the way: knocked again, not hurt again
                Damage(otherFps, SkillTargeting.ClosestPointOnBody(col, transform.position), Vector3.down);
        }
    }

    private void Damage(ChessPieceFPSController target, Vector3 point, Vector3 direction)
    {
        SkillTargeting.TryDealDamage(fps, target.GetComponent<IDamageable>(), new DamageInfo
        {
            amount = impactDamage,
            hitPoint = point,
            hitDirection = direction,
            source = gameObject,
            type = DamageType.Melee
        });
    }

    private void ReleaseHookedEnemy()
    {
        if (hookedEnemyFps == null)
            return;

        if (hookedEnemyHeld)
        {
            hookedEnemyFps.ReleaseMovementSuppression(this);
            hookedEnemyFps.SetGravityEnabled(this, true);
            hookedEnemyFps.EndForcedMove(this);
            hookedEnemyHeld = false;
        }

        hookedEnemyFps = null;
        hookedEnemyController = null;
    }

    private void Cleanup()
    {
        ReleaseHookedEnemy();

        if (!active) return;
        active = false;

        if (anchor)
        {
            if (IsTearingDown)
                Destroy(anchor.gameObject); // it sits in the world now, outside the dying bishop
            else if (anchorPool)
                anchorPool.Return(anchor);
        }

        // While the bishop is being disabled/destroyed its pools go with it: do not touch them.
        if (!IsTearingDown && trailPool && trail)
            trailPool.Return(trail);

        anchor = null;
        trail = null;
        trailLR = null;
        hookedWall = null;

        if (fps != null)
            fps.SetGravityEnabled(this, true);
    }
}
