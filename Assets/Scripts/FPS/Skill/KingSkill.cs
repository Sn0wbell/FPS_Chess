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
    // VISUAL
    // =========================
    [Header("Visual Zones")]
    [SerializeField] private Zone pullZonePrefab;
    [SerializeField] private Zone gravityZonePrefab;

    [Header("Targeting")]
    [SerializeField] private LayerMask enemyLayer;
    [SerializeField] private LayerMask obstacleLayer;

    private const float PathStep = 0.05f;

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
    private struct PullSession
    {
        public List<Vector3> path;
        public float progress;
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
            pullZoneInstance.Activate(pos, pullRadius, pullExposeDelay * 2f);
        }

        if (gravityZonePrefab)
        {
            gravityZoneInstance = Instantiate(gravityZonePrefab, pos, Quaternion.identity);
            gravityZoneInstance.Activate(pos, gravityRadius, activeDuration);
        }
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

    /// <summary>
    /// Arc from start to end with one normalized parameter u in [0,1] (bounded: 1/PathStep points).
    /// Stops early in front of an obstacle. Returns null when there is nothing to do.
    /// </summary>
    /// <param name="bodyOffset">Pivot -> collider centre of the pulled piece (casts at body height, not at the feet).</param>
    private List<Vector3> BuildPullPath(Vector3 start, Vector3 end, Vector3 bodyOffset, float castRadius)
    {
        float horizontal = Vector2.Distance(new Vector2(start.x, start.z), new Vector2(end.x, end.z));
        if (horizontal < 0.05f)
            return null;

        float height = Mathf.Min(pullArcHeight, horizontal);

        var path = new List<Vector3> { start };
        Vector3 prev = start;

        for (float u = PathStep; u <= 1f + 1e-4f; u += PathStep)
        {
            float uc = Mathf.Min(u, 1f);
            Vector3 pos = Vector3.Lerp(start, end, uc);
            pos.y += 4f * height * uc * (1f - uc); // parabol

            Vector3 delta = pos - prev;
            float dist = delta.magnitude;
            if (dist > 0f && Physics.SphereCast(prev + bodyOffset, castRadius, delta / dist, out _, dist, obstacleLayer, QueryTriggerInteraction.Ignore))
                break;

            path.Add(pos);
            prev = pos;
        }

        return path.Count > 1 ? path : null;
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

            Vector3 offset = target.transform.position - center;
            float dist = offset.magnitude;
            if (dist <= 0.001f) continue;

            float targetDist = Mathf.Clamp01(dist / pullRadius) * gravityRadius;
            CharacterController tcc = target.GetComponent<CharacterController>();
            Vector3 bodyOffset = tcc != null ? tcc.bounds.center - target.transform.position : Vector3.up;
            float castRadius = tcc != null ? tcc.radius * 0.5f : 0.2f;
            List<Vector3> path = BuildPullPath(target.transform.position, center + offset.normalized * targetDist, bodyOffset, castRadius);
            if (path == null)
                continue;

            // Taken over whatever moves it now (knock-back, its own grapple, another skill's pull).
            if (!target.ClaimForcedMove(this))
                continue;

            activePulls[target] = new PullSession { path = path, progress = 0f };
            pulledThisUse.Add(target);
            target.SuppressMovement(this);
            target.SetGravityEnabled(this, false);
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

            CharacterController cc = target.GetComponent<CharacterController>();
            int last = s.path.Count - 1;
            int index = Mathf.Min(Mathf.FloorToInt(s.progress), last);

            s.progress += pullSpeed * 60f * dt; // frame-rate independent
            int nextIndex = Mathf.Min(Mathf.FloorToInt(s.progress), last);

            bool blocked = false;
            if (nextIndex > index && cc != null)
            {
                // Along the arc; against the ground / a wall it slides (checked at body height), head-on releases it.
                Vector3 delta = s.path[nextIndex] - target.transform.position;
                blocked = !SkillTargeting.SlideMove(cc, delta, obstacleLayer, headOnAngle);

                // Real speed along the arc (path points per second x spacing), not this frame's jump.
                pullVelocity[target] = (s.path[nextIndex] - s.path[index]) / (nextIndex - index) * (pullSpeed * 60f);
            }

            if (blocked || nextIndex >= last)
            {
                ReleasePulled(target);
                landedThisFrame.Add(target);
            }
            else
                activePulls[target] = s;
        }

        HandlePullCollisions();
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
