using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// QUEEN — shockwave + grenade barrage, one sequence:
///   1. Push: a sphere expands; every piece inside is thrown outwards on an arc
///      (farther pieces later). Effect only: allies are pushed too.
///   2. Barrage: grenades mark red zones around up to 3 nearest ENEMIES.
///   3. After the warning, each zone explodes (damage falls off with distance; enemies only).
/// The queen cannot shoot during push + firing; the skill ends when the grenades have exploded.
/// </summary>
public class QueenSkill : ActiveSkill
{
    private ChessPieceFPSController fps;
    private CharacterController controller;

    // =========================
    // TIMING
    // =========================
    [Header("Timing")]
    [Tooltip("Base duration; the skill always lasts until its grenades have exploded.")]
    public float activeDuration = 4f;
    public float fireAnimationTime = 1f;
    public float redZoneWarningTime = 3f;

    // =========================
    // GRENADE / ZONE
    // =========================
    [Header("Grenade Launcher Settings")]
    public int grenadeCount = 8;
    public float redZoneRadius = 3f;

    // =========================
    // PUSH
    // =========================
    [Header("Push Settings")]
    public float pushRadius = 8f;
    public float maxPushHeight = 3.5f;
    [SerializeField, Range(0.1f, 5f)]
    public float pushSpeedMultiplier = 1f;
    [SerializeField, Range(1f, 5f)]
    public float pushDistanceMultiplier = 1.5f;
    public float pushSphereScaleSpeed = 25f;
    public LayerMask enemyLayer;
    public LayerMask obstacleLayer;

    // =========================
    // DAMAGE
    // =========================
    [Header("Damage")]
    public AnimationCurve damageCurve;
    public float maxDamage = 50f;
    public float minDamage = 10f;
    public float heightRange = 4f;

    // =========================
    // VISUAL
    // =========================
    [Header("Visual")]
    public GameObject redZonePrefab;
    public GameObject pushSpherePrefab;
    [Range(0f, 1f)]
    public float sphereFadeStartNormalized = 0.55f;

    // =========================
    // AI AWARENESS (UNCHANGED)
    // =========================
    public bool IsActiveRedZone => activeRedZones.Count > 0;
    public float RedZoneRadius => redZoneRadius;

    public List<Vector3> RedZoneCenters
    {
        get
        {
            List<Vector3> centers = new();
            foreach (var rz in activeRedZones)
                if (rz != null)
                    centers.Add(rz.transform.position);
            return centers;
        }
    }

    public Vector3[] GetPredictedRedZones(int steps = 10) => RedZoneCenters.ToArray();

    public bool IsPositionInRedZone(Vector3 pos)
    {
        foreach (var center in RedZoneCenters)
            if (Vector3.Distance(pos, center) <= redZoneRadius)
                return true;
        return false;
    }

    // =========================
    // RUNTIME
    // =========================
    private const float PathStep = 0.05f; // parametric step
    private const float MaxPathParameter = 2.5f; // bounded: never loops forever

    private readonly List<RedZone> activeRedZones = new();
    private readonly Queue<RedZone> redZonePool = new();

    private readonly Dictionary<ChessPieceFPSController, PushSession> activePushes = new();
    private readonly List<DelayedPush> pendingPushes = new();
    private readonly List<ChessPieceFPSController> pushKeys = new();

    private GameObject pushSphereInstance;
    private readonly List<Material> sphereMaterials = new();
    private Coroutine sphereRoutine;
    private Coroutine barrageRoutine;
    private bool barrageStarted;
    private bool attackLocked;

    // =========================
    // INTERNAL STRUCT
    // =========================
    private struct PushSession
    {
        public Vector3[] path;
        public float progress;
    }

    private struct DelayedPush
    {
        public ChessPieceFPSController target;
        public Vector3 direction; // horizontal, away from the queen
        public float delay;
    }

    protected override float GetActiveDuration(in SkillContext ctx) =>
        // Safety cap only: the sequence ends itself via FinishActive().
        activeDuration + fireAnimationTime + redZoneWarningTime + 3f;

    public override void Initialize(in SkillContext ctx)
    {
        base.Initialize(ctx);
        fps = ctx.fps;
        controller = ctx.controller;
    }

    protected override void OnEnterActive(in SkillContext ctx)
    {
        barrageStarted = false;
        LockAttack(true);
        ExecutePushPhase();
    }

    protected override void TickActive(in SkillContext ctx)
    {
        HandleDelayedPushes(ctx.deltaTime);
        UpdatePushMotion(ctx.deltaTime);

        // Không có enemy hợp lệ để push → spawn red zone trực tiếp
        if (!barrageStarted && pendingPushes.Count == 0 && activePushes.Count == 0)
            StartBarrage();
    }

    protected override void OnExitActive(in SkillContext ctx)
    {
        if (barrageRoutine != null)
        {
            StopCoroutine(barrageRoutine);
            barrageRoutine = null;
        }

        LockAttack(false);
        CleanupRedZones();
        AbortAllPushes();
        DespawnSphere();
    }

    // =========================
    // PUSH PHASE
    // =========================
    private void ExecutePushPhase()
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, pushRadius, enemyLayer, QueryTriggerInteraction.Ignore);
        Vector3 center = transform.position;
        var seen = new HashSet<ChessPieceFPSController>();

        foreach (var hit in hits)
        {
            ChessPieceFPSController target = SkillTargeting.PieceOf(hit);
            if (!SkillTargeting.IsAffectable(fps, target) || !seen.Add(target))
                continue;

            Vector3 dir = target.transform.position - center;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f)
                continue;

            // The sphere reaches farther pieces later.
            float delay = (Vector3.Distance(center, target.transform.position) / pushSphereScaleSpeed) * 3.46f;

            pendingPushes.Add(new DelayedPush
            {
                target = target,
                direction = dir.normalized,
                delay = delay
            });
        }

        if (pendingPushes.Count > 0)
            SpawnSphere();
    }

    private void HandleDelayedPushes(float dt)
    {
        for (int i = pendingPushes.Count - 1; i >= 0; i--)
        {
            var p = pendingPushes[i];
            p.delay -= dt;

            if (p.delay > 0f)
            {
                pendingPushes[i] = p;
                continue;
            }

            pendingPushes.RemoveAt(i);

            // Dead, or already moved by another skill: skip it.
            if (!SkillTargeting.IsAlive(p.target) || !p.target.TryBeginForcedMove(this))
                continue;

            // Path from where the piece is NOW, sized to ITS collider (not the queen's).
            Vector3 start = p.target.transform.position;
            Vector3 desiredEnd = start + p.direction * pushRadius * pushDistanceMultiplier;
            Vector3[] path = BuildParabolicPath(start, desiredEnd, p.target.GetComponent<CharacterController>());

            p.target.SuppressMovement(this);
            p.target.ForceStopAllMotion();
            activePushes[p.target] = new PushSession { path = path, progress = 0f };
        }
    }

    private void UpdatePushMotion(float dt)
    {
        if (activePushes.Count == 0)
            return;

        pushKeys.Clear();
        pushKeys.AddRange(activePushes.Keys);

        foreach (var target in pushKeys)
        {
            if (!SkillTargeting.IsAlive(target))
            {
                ReleasePushed(target);
                continue;
            }

            PushSession session = activePushes[target];
            session.progress += dt * pushSpeedMultiplier * 20f; // 20 path points per second

            int index = Mathf.FloorToInt(session.progress);
            if (index >= session.path.Length)
            {
                // Landed: free it right away (not only when the last one lands).
                ReleasePushed(target);
                continue;
            }

            target.WarpPosition(session.path[index]);
            activePushes[target] = session;
        }
    }

    private void ReleasePushed(ChessPieceFPSController target)
    {
        activePushes.Remove(target);
        if (target == null) return;
        target.ReleaseMovementSuppression(this);
        target.EndForcedMove(this);
    }

    private void AbortAllPushes()
    {
        pushKeys.Clear();
        pushKeys.AddRange(activePushes.Keys);
        foreach (var t in pushKeys)
            ReleasePushed(t);

        activePushes.Clear();
        pendingPushes.Clear();
    }

    // =========================
    // RED ZONE LOGIC
    // =========================
    private void StartBarrage()
    {
        barrageStarted = true;
        DespawnSphere();

        List<Vector3> positions = CalculateGrenadePositions();
        if (positions.Count == 0)
        {
            FinishActive(); // nobody to target
            return;
        }

        barrageRoutine = StartCoroutine(BarrageRoutine(positions));
    }

    private IEnumerator BarrageRoutine(List<Vector3> positions)
    {
        float interval = fireAnimationTime / Mathf.Max(1, positions.Count);

        for (int i = 0; i < positions.Count; i++)
        {
            // Every zone lives exactly until the common explosion.
            float untilExplosion = (positions.Count - 1 - i) * interval + redZoneWarningTime;

            var rz = GetRedZone();
            if (rz != null)
            {
                rz.Activate(positions[i], redZoneRadius * 1.19f, untilExplosion);
                activeRedZones.Add(rz);
            }

            yield return new WaitForSeconds(interval);
        }

        // Firing done: the queen can fight again while the zones count down.
        LockAttack(false);

        yield return new WaitForSeconds(Mathf.Max(0f, redZoneWarningTime - interval));

        ApplyGrenadeDamage();
        CleanupRedZones();
        barrageRoutine = null;
        FinishActive();
    }

    // =========================
    // POSITION CALCULATION
    // =========================
    private List<Vector3> CalculateGrenadePositions()
    {
        List<Vector3> result = new();

        int maxTargets = 3;
        int maxAttemptsPerZone = 10;
        int budget = Mathf.Min(grenadeCount, Mathf.Max(0, ZoneOverlaySystem.FreeSlots)); // never more zones than can be shown

        float minSpacing = redZoneRadius * 2.0f;
        float queenSafeRadius = redZoneRadius * 2.0f;
        float ringRadius = redZoneRadius * 5.0f;

        List<ChessPieceFPSController> enemies = new();
        foreach (var u in Object.FindObjectsByType<ChessPieceFPSController>(FindObjectsSortMode.None))
        {
            if (!SkillTargeting.IsAffectable(fps, u))
                continue;
            if (SkillTargeting.IsAlly(fps, u))
                continue; // grenades target enemies only
            enemies.Add(u);
        }

        if (enemies.Count == 0 || budget == 0)
            return result;

        enemies.Sort((a, b) =>
            Vector3.Distance(fps.transform.position, a.transform.position)
            .CompareTo(Vector3.Distance(fps.transform.position, b.transform.position)));

        int targetCount = Mathf.Min(maxTargets, enemies.Count);
        int ringCount = budget / targetCount;

        for (int i = 0; i < targetCount && result.Count < budget; i++)
        {
            Vector3 center = enemies[i].transform.position;
            // Core zone directly at enemy position
            TryAddZone(center);

            // Ring zones around enemy (same Y as enemy)
            for (int r = 0; r < ringCount && result.Count < budget; r++)
            {
                for (int attempt = 0; attempt < maxAttemptsPerZone; attempt++)
                {
                    float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
                    Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * Random.Range(minSpacing, ringRadius);
                    if (TryAddZone(center + offset))
                        break;
                }
            }
        }

        return result;

        bool TryAddZone(Vector3 pos)
        {
            if (result.Count >= budget)
                return false;
            if (Vector3.Distance(pos, fps.transform.position) < queenSafeRadius)
                return false;
            foreach (var p in result)
                if (Vector3.Distance(p, pos) < minSpacing)
                    return false;
            result.Add(pos);
            return true;
        }
    }

    private void ApplyGrenadeDamage()
    {
        var targets = Object.FindObjectsByType<ChessPieceFPSController>(FindObjectsSortMode.None);
        Dictionary<ChessPieceFPSController, float> damageMap = new();

        foreach (var rz in activeRedZones)
        {
            if (rz == null) continue;
            Vector3 center = rz.transform.position;

            foreach (var target in targets)
            {
                if (!SkillTargeting.IsAffectable(fps, target))
                    continue;

                Vector3 delta = target.transform.position - center;
                if (Mathf.Abs(delta.y) > heightRange)
                    continue;

                float dist = new Vector2(delta.x, delta.z).magnitude;
                if (dist > redZoneRadius)
                    continue;

                float t = Mathf.Clamp01(dist / redZoneRadius);
                float curveValue = damageCurve != null && damageCurve.length > 0
                    ? Mathf.Clamp01(damageCurve.Evaluate(1f - t))
                    : 1f - t;

                float dmg = Mathf.Lerp(minDamage, maxDamage, curveValue);
                if (!damageMap.TryGetValue(target, out var cur) || dmg > cur)
                    damageMap[target] = dmg;
            }
        }

        foreach (var pair in damageMap)
        {
            SkillTargeting.TryDealDamage(fps, pair.Key.GetComponent<IDamageable>(), new DamageInfo
            {
                amount = pair.Value,
                hitPoint = Vector3.zero,
                hitDirection = Vector3.up,
                source = gameObject,
                type = DamageType.Explosive
            });
        }
    }

    // =========================
    // SPHERE VISUAL
    // =========================
    private void SpawnSphere()
    {
        if (pushSpherePrefab == null)
            return;

        pushSphereInstance = Instantiate(pushSpherePrefab, transform.position, Quaternion.identity);
        sphereMaterials.Clear();
        foreach (var r in pushSphereInstance.GetComponentsInChildren<Renderer>())
            foreach (var mat in r.materials) // instanced once here, not every frame
            {
                mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                sphereMaterials.Add(mat);
            }

        pushSphereInstance.transform.localScale = Vector3.zero;
        sphereRoutine = StartCoroutine(ExpandSphere());
    }

    private IEnumerator ExpandSphere()
    {
        float currentScale = 0f;
        float targetScale = pushRadius * 2f;

        while (currentScale < targetScale && pushSphereInstance != null)
        {
            currentScale += Time.deltaTime * pushSphereScaleSpeed;
            float clamped = Mathf.Min(currentScale, targetScale);
            float alpha = Mathf.Lerp(sphereFadeStartNormalized, 0.0f, clamped / targetScale);

            foreach (var mat in sphereMaterials)
            {
                if (mat == null) continue;
                Color c = mat.color;
                c.a = alpha;
                mat.color = c;
            }

            pushSphereInstance.transform.localScale = Vector3.one * clamped;
            yield return null;
        }

        sphereRoutine = null;
        DespawnSphere();
    }

    private void DespawnSphere()
    {
        if (sphereRoutine != null)
        {
            StopCoroutine(sphereRoutine);
            sphereRoutine = null;
        }

        foreach (var mat in sphereMaterials)
            if (mat != null)
                Destroy(mat);
        sphereMaterials.Clear();

        if (pushSphereInstance)
            Destroy(pushSphereInstance);
        pushSphereInstance = null;
    }

    // =========================
    // PARABOLA
    // =========================
    private Vector3[] BuildParabolicPath(Vector3 start, Vector3 desiredEnd, CharacterController shape)
    {
        List<Vector3> points = new() { start };

        Vector3 flat = desiredEnd - start;
        flat.y = 0f;
        Vector3 forward = flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward;
        float totalDistance = flat.magnitude;

        float radius = shape != null ? shape.radius : 0.4f;
        float height = shape != null ? shape.height : 2f;
        Vector3 centerOffset = shape != null ? shape.center : Vector3.up;
        float halfHeight = Mathf.Max(0f, height * 0.5f - radius);

        Vector3 prev = start;
        int steps = Mathf.RoundToInt(MaxPathParameter / PathStep);
        int landingStep = Mathf.RoundToInt(1f / PathStep);
        int pointsAtLanding = -1;
        bool hitSomething = false;

        for (int i = 1; i <= steps; i++)
        {
            float t = i * PathStep;
            Vector3 pos = start + forward * (totalDistance * t);
            pos.y = start.y + 4f * maxPushHeight * t * (1f - t);

            Vector3 delta = pos - prev;
            float dist = delta.magnitude;

            if (dist > 0f)
            {
                // capsule tại vị trí PREV
                Vector3 worldCenter = prev + centerOffset;
                Vector3 p1 = worldCenter + Vector3.up * halfHeight;
                Vector3 p2 = worldCenter - Vector3.up * halfHeight;

                if (Physics.CapsuleCast(p1, p2, radius, delta / dist, out RaycastHit hit, dist, obstacleLayer, QueryTriggerInteraction.Ignore))
                {
                    // Stop just before the surface (the pivot, not the contact point).
                    points.Add(prev + delta / dist * Mathf.Max(0f, hit.distance - 0.05f));
                    hitSomething = true;
                    break;
                }
            }

            points.Add(pos);
            prev = pos;
            if (i == landingStep)
                pointsAtLanding = points.Count;
        }

        // Nothing hit at all: stop at the planned landing point, at the start height
        // (never keep falling far below the map).
        if (!hitSomething && pointsAtLanding > 0 && pointsAtLanding < points.Count)
            points.RemoveRange(pointsAtLanding, points.Count - pointsAtLanding);

        return points.ToArray();
    }

    // =========================
    // HELPERS (UNCHANGED)
    // =========================
    /// <summary>Keyed lock: never overrides the arena's own control flags (canAct).</summary>
    private void LockAttack(bool locked)
    {
        if (fps == null || attackLocked == locked)
            return;

        attackLocked = locked;
        fps.SetActionLocked(this, locked);
    }

    private void CleanupRedZones()
    {
        foreach (var rz in activeRedZones)
            if (rz != null)
                ReturnRedZone(rz);
        activeRedZones.Clear();
    }

    private RedZone GetRedZone()
    {
        while (redZonePool.Count > 0)
        {
            var pooled = redZonePool.Dequeue();
            if (pooled != null) return pooled;
        }

        if (redZonePrefab == null)
            return null;

        var go = Instantiate(redZonePrefab);
        var rz = go.GetComponent<RedZone>();
        if (rz == null)
            rz = go.AddComponent<RedZone>();
        return rz;
    }

    private void ReturnRedZone(RedZone rz)
    {
        rz.Deactivate();
        if (!redZonePool.Contains(rz))
            redZonePool.Enqueue(rz);
    }

    private void OnDestroy()
    {
        foreach (var rz in redZonePool)
            if (rz != null)
                Destroy(rz.gameObject);
        redZonePool.Clear();
    }
}
