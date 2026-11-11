using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// KING — gravity well. Two zones follow the king (who keeps moving, jumping, attacking and
/// using his shield freely; the shield is his own, see KingFPSController):
///   - pull zone : after a short delay (tunable), every piece inside is pulled towards the king on
///                 an arc, ending at a distance proportional to where it started (inside the gravity
///                 zone). The pull takes pieces over whatever moved them (knock-back, a grapple, being
///                 pulled). Pulled pieces slide along the ground / walls (head-on stops them); pulled
///                 pieces that meet, or reach the king, bounce off lightly (billiard; the king never
///                 moves). Rook walls inside are pulled too, by momentum (heavy: they only shift or tip);
///   - gravity zone: pieces inside move and jump slower.
/// Effects only (no damage), so allies are affected too.
/// </summary>
public class KingSkill : ActiveSkill, IHeldPieceKnockHandler
{
    // =========================
    // REFERENCES
    // =========================
    private ChessPieceFPSController fps;

    [Header("Zones")]
    [SerializeField] private float pullRadius = 8f;
    [SerializeField] private float gravityRadius = 4f;
    [SerializeField] private float pullExposeDelay = 1f;

    [Header("Pull")]
    [Tooltip("Path points advanced per frame at 60 FPS (frame-rate independent).")]
    [SerializeField] private float pullSpeed = 0.1f;
    [Tooltip("A pulled piece hitting the ground or a wall at this angle or more (90 = straight in) is released; below it slides along.")]
    [Range(0f, 90f)]
    [SerializeField] private float headOnAngle = 80f;
    [Tooltip("Pulled pieces that collide (or reach the king) bounce with this share of their pull speed...")]
    [SerializeField] private float collisionSpeedScale = 0.5f;
    [Tooltip("...and this upward bounce factor (light: they stay in the gravity zone).")]
    [SerializeField] private float collisionBounce = 0.2f;
    [Tooltip("Rook walls in the pull zone get the momentum of a piece pulled at this speed (fading with distance).")]
    [SerializeField] private float wallPullForce = 10f;
    [Tooltip("Highest point of a pulled piece's arc above the straight line (lower for short pulls). Not tied to the zone's cage height.")]
    [SerializeField] private float pullArcHeight = 16f;

    // =========================
    // GRAVITY DEBUFF
    // =========================
    [Header("Gravity Debuff")]
    [SerializeField] private float moveMultiplier = 0.4f;
    [SerializeField] private float jumpMultiplier = 0.25f;
    [Tooltip("Knock-backs from other sources are scaled by this while in the gravity zone (the king's own bounces are not).")]
    [Range(0f, 1f)]
    [SerializeField] private float knockMultiplier = 0.5f;

    // =========================
    // ACTIVE
    // =========================
    [Header("Active")]
    [SerializeField] private float activeDuration = 4f;

    // =========================
    // MARK
    // =========================
    [Header("Mark")]
    [Tooltip("Enemies pulled, or in the gravity zone, are revealed to the king's side (through walls) meanwhile and this long after.")]
    [SerializeField] private float markSeconds = 3f;
    [Tooltip("Material made from the 'FPSChess/XRayReveal' shader (a runtime fallback is created if empty).")]
    [SerializeField] private Material revealMaterial;

    // =========================
    // VISUAL
    // =========================
    [Header("Visual Zones")]
    [SerializeField] private Zone pullZonePrefab;
    [SerializeField] private Zone gravityZonePrefab;

    [Header("Targeting")]
    [SerializeField] private LayerMask enemyLayer;
    [SerializeField] private LayerMask obstacleLayer;

    // pullSpeed counts arc steps (1 / PathStep per pull) per frame at 60 FPS: it sets how long a pull lasts.
    private const float PathStep = 0.05f;
    private const float ArcSearchStep = 0.025f;
    private const float MaxArcParameter = 1.5f;   // no surface found up to here: it falls on under gravity
    private const float LandingSearchBelow = 50f; // the landing surface is searched this far below the lower of piece / king
    private const float MoveSubStep = 0.1f;
    private const float MinArcRise = 0.5f;        // a pull down from high up still rises a little first

    private Zone pullZoneInstance;
    private Zone gravityZoneInstance;
    private Coroutine pullDelayRoutine;
    // =========================
    // STATE
    // =========================
    private bool pullExecuted;

    private readonly HashSet<ChessPieceFPSController> gravityAffected = new();
    private readonly HashSet<ChessPieceFPSController> gravityCurrent = new();
    private readonly List<ChessPieceFPSController> gravityStale = new();
    private readonly Dictionary<ChessPieceFPSController, PullSession> activePulls = new();
    private readonly List<ChessPieceFPSController> pullKeys = new();
    private readonly List<ChessPieceFPSController> pulledThisUse = new();
    private readonly HashSet<ChessPieceFPSController> landedThisFrame = new();
    private readonly Dictionary<ChessPieceFPSController, Vector3> pullVelocity = new();
    private readonly HashSet<(ChessPieceFPSController, ChessPieceFPSController)> bouncedPairs = new();

    // =========================
    // INTERNAL STRUCTS
    // =========================
    /// <summary>
    /// One pulled piece, flying like a knight's leap: u = elapsed / duration,
    /// pos(u) = start + (end - start) * u,  y(u) += 4 * height * u * (1 - u).
    /// end is the real surface at the landing spot, so it lands on it from any height.
    /// </summary>
    private struct PullSession
    {
        public Vector3 start;
        public Vector3 end;
        public float height;
        public float duration;
        public float elapsed;
        public float lastU;         // where it was on the arc last frame
        public float endU;          // where the arc meets a surface (precomputed)
        public bool hasEnd;
        public bool endIsWall;      // ...a wall / ceiling there: it stops and drops
        public bool freeFalling;    // past the arc with nothing under it: normal gravity
        public Vector3 fallVelocity;
        public Vector3 feetOffset;  // pivot -> centre of the lower sphere of its body
        public float castRadius;
        public float slopeLimit;
    }

    // =========================
    // AI Awareness
    // =========================
    public bool IsActivePullZone => pullZoneInstance != null && pullZoneInstance.gameObject.activeInHierarchy;
    public bool IsActiveGravityZone => gravityZoneInstance != null && gravityZoneInstance.gameObject.activeInHierarchy;
    public float PullZoneRadius => pullRadius;
    public float GravityZoneRadius => gravityRadius;
    public Vector3 PullZoneCenter => transform.position;
    public Vector3 GravityZoneCenter => transform.position;

    public Vector3[] GetActiveZones()
    {
        List<Vector3> zones = new();
        if (IsActivePullZone) zones.Add(PullZoneCenter);
        if (IsActiveGravityZone) zones.Add(GravityZoneCenter);
        return zones.ToArray();
    }

    public bool IsPositionInDanger(Vector3 pos)
    {
        if (IsActivePullZone && Vector3.Distance(pos, PullZoneCenter) <= pullRadius) return true;
        if (IsActiveGravityZone && Vector3.Distance(pos, GravityZoneCenter) <= gravityRadius) return true;
        return false;
    }

    // =========================
    // ACTIVE SKILL
    // =========================
    protected override float GetActiveDuration(in SkillContext ctx) => activeDuration;

    public override void Initialize(in SkillContext ctx)
    {
        base.Initialize(ctx);
        fps = ctx.fps;
        blockMask = SkillBlockMask.None; // the king moves, jumps, attacks and shields freely during his skill
        if (revealMaterial != null)
            RevealSystem.XRayMaterial = revealMaterial;
    }

    protected override void OnEnterActive(in SkillContext ctx)
    {
        pullExecuted = false;
        pulledThisUse.Clear();
        bouncedPairs.Clear();
        pullVelocity.Clear();

        SpawnZones();
        pullDelayRoutine = StartCoroutine(PullDelayRoutine());
    }

    protected override void TickActive(in SkillContext ctx)
    {
        UpdateZoneFollow();
        UpdatePullMotion(ctx.deltaTime);
        UpdateGravityZone();
    }

    protected override void OnExitActive(in SkillContext ctx)
    {
        // The delayed pull must never fire after the skill ended (it would freeze pieces for good).
        if (pullDelayRoutine != null)
        {
            StopCoroutine(pullDelayRoutine);
            pullDelayRoutine = null;
        }

        AbortAllPulls();
        ClearGravityDebuffs();
        DespawnZones();
    }

    // =========================
    // ZONES
    // =========================
    private void SpawnZones()
    {
        Vector3 pos = transform.position;

        if (pullZonePrefab)
        {
            pullZoneInstance = Instantiate(pullZonePrefab, pos, Quaternion.identity);
            pullZoneInstance.DecalDepth = PullColumnHalfHeight(pos); // marks every height it pulls from
            pullZoneInstance.Activate(pos, pullRadius, pullExposeDelay * 2f);
        }

        if (gravityZonePrefab)
        {
            gravityZoneInstance = Instantiate(gravityZonePrefab, pos, Quaternion.identity);
            gravityZoneInstance.DecalDepth = gravityRadius; // as high as its sphere
            gravityZoneInstance.Activate(pos, gravityRadius, activeDuration);
        }
    }

    /// <summary>The pull reaches the whole height of the arena (far up and down without one).</summary>
    private static float PullColumnHalfHeight(Vector3 center)
    {
        if (ArenaController.Current != null && ArenaController.Current.TryGetBounds(out Bounds arena))
            return Mathf.Max(1f, center.y - arena.min.y, arena.max.y - center.y);
        return 1000f;
    }

    private void UpdateZoneFollow()
    {
        Vector3 pos = transform.position;
        if (pullZoneInstance != null) pullZoneInstance.transform.position = pos;
        if (gravityZoneInstance != null) gravityZoneInstance.transform.position = pos;
    }

    private void DespawnZones()
    {
        if (pullZoneInstance != null)
        {
            pullZoneInstance.Deactivate();
            Destroy(pullZoneInstance.gameObject);
            pullZoneInstance = null;
        }

        if (gravityZoneInstance != null)
        {
            gravityZoneInstance.Deactivate();
            Destroy(gravityZoneInstance.gameObject);
            gravityZoneInstance = null;
        }
    }

    // =========================
    // PULL
    // =========================
    private IEnumerator PullDelayRoutine()
    {
        yield return new WaitForSeconds(pullExposeDelay);
        pullDelayRoutine = null;
        if (State == SkillState.Active)
            ExecutePull();
    }

    private float PullDuration => (1f / PathStep) / Mathf.Max(0.01f, pullSpeed * 60f);

    /// <summary>
    /// The arc to the landing spot (horizontally: the gravity radius x its share of the pull radius, toward the king),
    /// ending on the real surface there whether it is higher or lower than the piece or the king.
    /// False when there is nothing to do (right at the king, or blocked at once).
    /// </summary>
    private bool TryBuildPull(ChessPieceFPSController target, Vector3 center, out PullSession s)
    {
        s = default;
        Vector3 start = target.transform.position;
        Vector3 offset = start - center;
        Vector3 flat = new Vector3(offset.x, 0f, offset.z);
        if (offset.magnitude <= 0.001f || flat.sqrMagnitude < 1e-6f)
            return false;

        // Its share of the pull radius, measured flat (a piece high above / below the king is never pushed away).
        Vector3 end = center + flat.normalized * (Mathf.Clamp01(flat.magnitude / pullRadius) * gravityRadius);
        float horizontal = Vector2.Distance(new Vector2(start.x, start.z), new Vector2(end.x, end.z));
        if (horizontal < 0.05f)
            return false;
        float height = Mathf.Min(pullArcHeight, horizontal);

        // The surface it lands on (searched from above the arc down past the lower of the piece and the king).
        float top = Mathf.Max(start.y, center.y) + height + 2f;
        float bottom = Mathf.Min(start.y, center.y) - LandingSearchBelow;
        end.y = Physics.Raycast(new Vector3(end.x, top, end.z), Vector3.down, out RaycastHit ground, top - bottom, obstacleLayer, QueryTriggerInteraction.Ignore)
            ? ground.point.y
            : center.y;

        // Pulled down from high up close by: the arc still starts upward (never straight into what it stands on).
        float drop = start.y - end.y;
        if (drop > 0f)
            height = Mathf.Max(height, drop * 0.25f + MinArcRise);

        CharacterController cc = target.GetComponent<CharacterController>();
        s = new PullSession
        {
            start = start,
            end = end,
            height = height,
            duration = PullDuration,
            feetOffset = cc != null ? cc.bounds.center - start - Vector3.up * Mathf.Max(0f, cc.height * 0.5f - cc.radius) : Vector3.up * 0.4f,
            castRadius = cc != null ? cc.radius * 0.9f : 0.3f,
            slopeLimit = cc != null ? cc.slopeLimit : 45f
        };
        ComputePullEnd(ref s);
        return !(s.hasEnd && s.endU <= 1e-3f);
    }

    private static Vector3 ArcPoint(in PullSession s, float u)
    {
        Vector3 pos = s.start + (s.end - s.start) * u;
        pos.y += 4f * s.height * u * (1f - u); // parabol
        return pos;
    }

    /// <summary>Velocity along the arc at u (per second).</summary>
    private static Vector3 ArcVelocity(in PullSession s, float u) =>
        ((s.end - s.start) + Vector3.up * (4f * s.height * (1f - 2f * u))) / Mathf.Max(0.01f, s.duration);

    /// <summary>
    /// Walks the arc (with the lower sphere of the body) until it meets a surface: ground on the way down = the
    /// landing, a wall / ceiling = it stops there and drops. Walkable ground met on the way up is slid over.
    /// </summary>
    private void ComputePullEnd(ref PullSession s)
    {
        float u = 0f;
        Vector3 prev = ArcPoint(s, 0f);
        s.hasEnd = false;
        s.endIsWall = false;
        s.endU = float.PositiveInfinity;

        while (u < MaxArcParameter)
        {
            float nextU = Mathf.Min(u + ArcSearchStep, MaxArcParameter);
            Vector3 pos = ArcPoint(s, nextU);
            Vector3 delta = pos - prev;
            float dist = delta.magnitude;

            if (dist > 0f && Physics.SphereCast(prev + s.feetOffset, s.castRadius, delta / dist, out RaycastHit hit, dist, obstacleLayer, QueryTriggerInteraction.Ignore))
            {
                bool walkable = Vector3.Angle(hit.normal, Vector3.up) <= s.slopeLimit;
                if (!walkable || ArcVelocity(s, u).y <= 0f)
                {
                    s.endU = u + (nextU - u) * Mathf.Clamp01(hit.distance / dist);
                    s.hasEnd = true;
                    s.endIsWall = !walkable;
                    return;
                }
            }

            prev = pos;
            u = nextU;
        }
    }

    private void ExecutePull()
    {
        if (pullExecuted) return;
        pullExecuted = true;

        Vector3 center = transform.position;
        // The drawn radius, at EVERY height (the cage only marks where the zone meets the ground):
        // the whole height of the arena, or far up and down without one.
        float below = 1000f, above = 1000f;
        if (ArenaController.Current != null && ArenaController.Current.TryGetBounds(out Bounds arena))
        {
            below = Mathf.Max(0f, center.y - arena.min.y);
            above = Mathf.Max(0f, arena.max.y - center.y);
        }
        // Pieces (enemy layer) and rook walls (their collider sits on the Default layer).
        Collider[] hits = Physics.OverlapCapsule(center - Vector3.up * below, center + Vector3.up * above,
            pullRadius, enemyLayer | obstacleLayer, QueryTriggerInteraction.Ignore);
        int count = hits.Length;
        var walls = new HashSet<WallSegment>();

        for (int i = 0; i < count; i++)
        {
            ChessPieceFPSController target = SkillTargeting.PieceOf(hits[i]);
            if (target == null)
            {
                WallSegment wall = hits[i].GetComponentInParent<WallSegment>();
                if (wall != null && walls.Add(wall) && wall.gameObject.activeInHierarchy)
                    wall.Push(SkillTargeting.RadialKnock(wall.transform.position, center, pullRadius, wallPullForce, collisionBounce) * SkillTargeting.PieceMass);
                continue;
            }

            if (!SkillTargeting.IsAffectable(fps, target) || activePulls.ContainsKey(target))
                continue;

            if (!TryBuildPull(target, center, out PullSession session))
                continue;

            // Taken over whatever moves it now (knock-back, its own grapple, another skill's pull).
            if (!target.ClaimForcedMove(this))
                continue;

            activePulls[target] = session;
            pulledThisUse.Add(target);
            target.SuppressMovement(this);
            target.SetGravityEnabled(this, false);
            MarkIfEnemy(target);
        }
    }

    private void UpdatePullMotion(float dt)
    {
        landedThisFrame.Clear();
        if (activePulls.Count == 0) return;

        pullKeys.Clear();
        pullKeys.AddRange(activePulls.Keys);

        foreach (var target in pullKeys)
        {
            if (!SkillTargeting.IsAlive(target) || !activePulls.TryGetValue(target, out PullSession s))
            {
                ReleasePulled(target);
                continue;
            }

            MarkIfEnemy(target); // marked while it flies, and markSeconds after

            CharacterController cc = target.GetComponent<CharacterController>();
            if (cc == null)
            {
                ReleasePulled(target);
                continue;
            }

            s.elapsed += dt;
            float u = s.elapsed / Mathf.Max(0.01f, s.duration);
            Vector3 goal, velocity;
            if (!s.hasEnd && u >= MaxArcParameter)
            {
                // Past the arc with nothing under it: it falls on under its own gravity, keeping its momentum.
                if (!s.freeFalling)
                {
                    s.freeFalling = true;
                    s.fallVelocity = ArcVelocity(s, MaxArcParameter);
                }
                s.fallVelocity.y += target.Gravity * dt;
                velocity = s.fallVelocity;
                goal = target.transform.position + velocity * dt;
            }
            else
            {
                if (s.hasEnd) u = Mathf.Min(u, s.endU);
                velocity = ArcVelocity(s, u);
                goal = ArcPoint(s, u);
            }

            // Along the arc in small steps: touching down on the way down lands it there; head-on into
            // something new (a wall raised meanwhile) releases it; otherwise it slides along.
            // Sub-steps follow the arc itself (not the chord between two frames): the same flight at any frame rate.
            bool onArc = !s.freeFalling;
            Vector3 from = target.transform.position;
            Vector3 delta = goal - from;
            int subSteps = Mathf.Max(1, Mathf.CeilToInt(delta.magnitude / MoveSubStep));
            bool stopped = false;
            for (int i = 0; i < subSteps && !stopped; i++)
            {
                float k = (i + 1f) / subSteps;
                Vector3 next = onArc ? ArcPoint(s, Mathf.Lerp(s.lastU, u, k)) : from + delta * k;
                Vector3 step = next - target.transform.position;
                CollisionFlags flags = cc.Move(step);
                if ((flags & CollisionFlags.Below) != 0 && velocity.y < 0f)
                    stopped = true;
                else if ((flags & (CollisionFlags.Sides | CollisionFlags.Above)) != 0 && HeadOnObstacle(cc, step, s.slopeLimit))
                    stopped = true;
            }

            if (onArc)
                s.lastU = u;
            pullVelocity[target] = velocity; // real speed along the arc

            if (stopped || (s.hasEnd && s.elapsed / Mathf.Max(0.01f, s.duration) >= s.endU))
            {
                ReleasePulled(target);
                landedThisFrame.Add(target);
            }
            else
                activePulls[target] = s;
        }

        HandlePullCollisions();
    }

    /// <summary>Moving straight into a wall / ceiling (at headOnAngle or more), not onto walkable ground.</summary>
    private bool HeadOnObstacle(CharacterController cc, Vector3 step, float slopeLimit)
    {
        float length = step.magnitude;
        if (length < 1e-6f)
            return false;
        Vector3 dir = step / length;
        Vector3 center = cc.bounds.center;
        float half = Mathf.Max(0f, cc.height * 0.5f - cc.radius);
        if (!Physics.CapsuleCast(center + Vector3.up * half, center - Vector3.up * half, cc.radius * 0.95f, dir, out RaycastHit hit,
                length + cc.skinWidth + 0.05f, obstacleLayer, QueryTriggerInteraction.Ignore))
            return false;
        if (Vector3.Angle(hit.normal, Vector3.up) <= slopeLimit)
            return false;
        float angle = Mathf.Asin(Mathf.Clamp01(Vector3.Dot(dir, -hit.normal))) * Mathf.Rad2Deg; // 0 = along it, 90 = straight in
        return angle >= headOnAngle;
    }

    /// <summary>Revealed to the king's side (allies are slowed by the gravity zone too, never marked).</summary>
    private void MarkIfEnemy(ChessPieceFPSController target)
    {
        if (fps != null && target != null && target.Team != fps.Team)
            RevealSystem.Reveal(target, fps.Team, markSeconds);
    }

    /// <summary>
    /// Pulled pieces that touch (on the way or as they land) bounce off each other lightly, like
    /// billiard balls; one reaching the king bounces off him (the king never moves).
    /// </summary>
    private void HandlePullCollisions()
    {
        for (int i = 0; i < pulledThisUse.Count; i++)
        {
            var a = pulledThisUse[i];
            if (!SkillTargeting.IsAlive(a)) continue;
            bool aMoving = activePulls.ContainsKey(a) || landedThisFrame.Contains(a);

            if (aMoving && fps != null && Touching(a, fps) && bouncedPairs.Add((a, fps)))
                BounceOffTheKing(a);

            for (int j = i + 1; j < pulledThisUse.Count; j++)
            {
                var b = pulledThisUse[j];
                if (!SkillTargeting.IsAlive(b)) continue;
                bool bMoving = activePulls.ContainsKey(b) || landedThisFrame.Contains(b);
                if (!aMoving && !bMoving) continue;
                if (!Touching(a, b) || !bouncedPairs.Add((a, b))) continue;

                float impact = SkillTargeting.BilliardCollision(
                    a.transform.position, VelocityOf(a, aMoving),
                    b.transform.position, VelocityOf(b, bMoving),
                    collisionBounce, out Vector3 va, out Vector3 vb);
                if (impact <= 0f) continue;

                ReleasePulled(a);
                ReleasePulled(b);
                a.AddExternalImpulse(va, this);
                b.AddExternalImpulse(vb, this);
            }
        }
    }

    private Vector3 VelocityOf(ChessPieceFPSController piece, bool moving) =>
        moving && pullVelocity.TryGetValue(piece, out Vector3 v) ? v * collisionSpeedScale : Vector3.zero;

    private void BounceOffTheKing(ChessPieceFPSController piece)
    {
        Vector3 v = VelocityOf(piece, true);
        v.y = 0f;
        Vector3 n = piece.transform.position - transform.position;
        n.y = 0f;
        n = n.sqrMagnitude > 1e-6f ? n.normalized : -v.normalized;

        float inward = Vector3.Dot(v, -n);
        if (inward <= 0f)
            return; // moving away already

        Vector3 reflected = v + 2f * inward * n; // the king is immovable: the piece bounces straight back off him
        ReleasePulled(piece);
        piece.AddExternalImpulse(reflected + Vector3.up * (collisionBounce * inward), this);
    }

    private static bool Touching(ChessPieceFPSController a, ChessPieceFPSController b)
    {
        var ca = a.GetComponent<CharacterController>();
        var cb = b.GetComponent<CharacterController>();
        if (ca == null || cb == null) return false;

        Vector3 d = a.transform.position - b.transform.position;
        float horizontal = new Vector2(d.x, d.z).magnitude;
        float vertical = Mathf.Abs(ca.bounds.center.y - cb.bounds.center.y);
        return horizontal <= ca.radius + cb.radius + 0.15f && vertical <= (ca.height + cb.height) * 0.5f;
    }

    /// <summary>A piece knocked by another source while flying in the pull: it leaves the pull and flies normally.</summary>
    public void OnHeldPieceKnocked(ChessPieceFPSController piece)
    {
        if (piece != null && activePulls.ContainsKey(piece))
            ReleasePulled(piece);
    }

    /// <summary>Another skill takes a pulled piece over: only that one leaves the pull (the rest goes on).</summary>
    public void OnHeldPieceTakenOver(ChessPieceFPSController piece)
    {
        if (piece != null && activePulls.ContainsKey(piece))
            ReleasePulled(piece);
    }

    private void ReleasePulled(ChessPieceFPSController target)
    {
        activePulls.Remove(target);
        if (target == null) return;
        target.ReleaseMovementSuppression(this);
        target.SetGravityEnabled(this, true);
        target.EndForcedMove(this);
    }

    private void AbortAllPulls()
    {
        pullKeys.Clear();
        pullKeys.AddRange(activePulls.Keys);
        foreach (var t in pullKeys)
            ReleasePulled(t);
        activePulls.Clear();
    }

    // =========================
    // GRAVITY ZONE
    // =========================
    private void UpdateGravityZone()
    {
        gravityCurrent.Clear();

        Collider[] hits = Physics.OverlapSphere(transform.position, gravityRadius, enemyLayer);
        int count = hits.Length;
        for (int i = 0; i < count; i++)
        {
            ChessPieceFPSController target = SkillTargeting.PieceOf(hits[i]);
            if (!SkillTargeting.IsAffectable(fps, target) || !gravityCurrent.Add(target))
                continue;

            MarkIfEnemy(target); // always marked inside, and markSeconds after it leaves (or the zone ends)

            if (gravityAffected.Add(target))
                target.OverrideMovement(this, moveMultiplier, jumpMultiplier, knockMultiplier); // keyed by this king
        }

        gravityStale.Clear();
        foreach (var e in gravityAffected)
            if (e == null || !gravityCurrent.Contains(e))
                gravityStale.Add(e);

        foreach (var e in gravityStale)
        {
            if (e != null)
                e.ClearOverrideMovement(this);
            gravityAffected.Remove(e);
        }
    }

    private void ClearGravityDebuffs()
    {
        foreach (var e in gravityAffected)
            if (e != null)
                e.ClearOverrideMovement(this);
        gravityAffected.Clear();
    }
}
