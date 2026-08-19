using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class QueenSkill : ActiveSkill
{
    private ChessPieceFPSController fps;
    private CharacterController controller;
    private WeaponController weapon;

    // =========================
    // TIMING
    // =========================
    [Header("Timing")]
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
            {
                if (rz != null)
                    centers.Add(rz.transform.position);
            }
            return centers;
        }
    }

    public Vector3[] GetPredictedRedZones(int steps = 10)
    {
        List<Vector3> points = new();
        foreach (var center in RedZoneCenters)
            points.Add(center);

        return points.ToArray();
    }

    public bool IsPositionInRedZone(Vector3 pos)
    {
        foreach (var center in RedZoneCenters)
        {
            if (Vector3.Distance(pos, center) <= redZoneRadius)
                return true;
        }
        return false;
    }

    // =========================
    // RUNTIME
    // =========================
    private readonly List<RedZone> activeRedZones = new();
    private readonly Queue<RedZone> redZonePool = new();

    private readonly Dictionary<ChessPieceFPSController, PushSession> activePushes = new();
    private readonly HashSet<ChessPieceFPSController> awaitingLanding = new();
    private readonly List<DelayedPush> pendingPushes = new();

    private GameObject pushSphereInstance;
    private Coroutine sphereRoutine;

    // =========================
    // INTERNAL STRUCT
    // =========================
    private struct PushSession
    {
        public Vector3[] path;
        public int index;
        public float progress;
    }
    private struct DelayedPush
    {
        public ChessPieceFPSController enemy;
        public Vector3[] path;
        public float delay;
        public bool started;
    }
    protected override float GetActiveDuration(in SkillContext ctx) => activeDuration;

    public override void Initialize(in SkillContext ctx)
    {
        base.Initialize(ctx);
        fps = ctx.fps;
        controller = ctx.controller;
        weapon = ctx.weapon;
    }

    protected override void OnEnterActive(in SkillContext ctx)
    {
        LockAttack(true);
        ExecutePushPhase();
    }

    protected override void TickActive(in SkillContext ctx)
    {
        HandleDelayedPushes();
        UpdatePushMotion();
    }

    protected override void OnExitActive(in SkillContext ctx)
    {
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
        Collider[] hits = Physics.OverlapSphere(transform.position, pushRadius, enemyLayer);

        // Không có enemy hoặc chỉ có chính Queen → bỏ push nhưng vẫn spawn red zone
        if (hits.Length <= 1)
        {
            SpawnRedZones();
            return;
        }

        SpawnSphere();

        Vector3 center = transform.position;
        bool hasValidEnemy = false;

        foreach (var hit in hits)
        {
            if (!hit.TryGetComponent(out ChessPieceFPSController enemy))
                continue;

            if (enemy == fps)
                continue;

            Vector3 dir = enemy.transform.position - center;
            if (dir.sqrMagnitude < 0.01f)
                continue;

            hasValidEnemy = true;

            Vector3 target = enemy.transform.position + dir.normalized * pushRadius * pushDistanceMultiplier;

            float delay = (Vector3.Distance(center, enemy.transform.position) / pushSphereScaleSpeed) * 3.46f;

            Vector3[] path = BuildParabolicPath(enemy.transform.position, target);

            pendingPushes.Add(new DelayedPush
            {
                enemy = enemy,
                path = path,
                delay = delay,
                started = false
            });
        }

        // Không có enemy hợp lệ để push → spawn red zone trực tiếp
        if (!hasValidEnemy)
        {
            DespawnSphere();
            SpawnRedZones();
        }
    }
    private void HandleDelayedPushes()
    {
        for (int i = pendingPushes.Count - 1; i >= 0; i--)
        {
            var p = pendingPushes[i];

            if (!p.started)
            {
                p.started = true;
                pendingPushes[i] = p;
                continue;
            }

            p.delay -= Time.deltaTime;

            if (p.delay > 0f)
            {
                pendingPushes[i] = p;
                continue;
            }

            if (p.enemy == null)
            {
                pendingPushes.RemoveAt(i);
                continue;
            }

            p.enemy.SuppressMovement(MovementOverrideSource.Skill);
            p.enemy.ForceStopAllMotion();

            activePushes[p.enemy] = new PushSession
            {
                path = p.path,
                index = 0,
                progress = 0f
            };

            awaitingLanding.Add(p.enemy);
            pendingPushes.RemoveAt(i);
        }
    }

    private void UpdatePushMotion()
    {
        if (activePushes.Count == 0)
            return;

        var keys = new List<ChessPieceFPSController>(activePushes.Keys);

        var landed = new List<ChessPieceFPSController>();

        foreach (var enemy in keys)
        {
            if (enemy == null)
            {
                activePushes.Remove(enemy);
                continue;
            }

            PushSession session = activePushes[enemy];
            session.progress += Time.deltaTime * pushSpeedMultiplier * 20f;

            int index = Mathf.FloorToInt(session.progress);
            if (index >= session.path.Length)
            {
                activePushes.Remove(enemy);
                landed.Add(enemy);
                continue;
            }

            Vector3 next = session.path[index];

            enemy.WarpPosition(next);
            activePushes[enemy] = session;
        }

        if (activePushes.Count == 0)
        {
            foreach (var e in landed)
            {
                e.ReleaseMovementSuppression(MovementOverrideSource.Skill);
                awaitingLanding.Remove(e);
            }
            SpawnRedZones();
        }
    }

    private void AbortAllPushes()
    {
        foreach (var e in activePushes.Keys)
            e?.ReleaseMovementSuppression(MovementOverrideSource.Skill);

        activePushes.Clear();
        awaitingLanding.Clear();
        pendingPushes.Clear();
    }

    // =========================
    // RED ZONE LOGIC
    // =========================
    private void SpawnRedZones()
    {
        var positions = CalculateGrenadePositions();
        float interval = fireAnimationTime / grenadeCount;

        StartCoroutine(RedZoneRoutine(positions, interval));
    }

    private IEnumerator RedZoneRoutine(List<Vector3> positions, float interval)
    {
        foreach (var pos in positions)
        {
            var rz = GetRedZone();
            rz.Activate(pos, redZoneRadius * 1.19f, redZoneWarningTime);
            activeRedZones.Add(rz);
            yield return new WaitForSeconds(interval);
        }

        yield return new WaitForSeconds(redZoneWarningTime);
        ApplyGrenadeDamage();
        CleanupRedZones();
    }

    // =========================
    // POSITION CALCULATION
    // =========================
    private List<Vector3> CalculateGrenadePositions()
    {
        List<Vector3> result = new();

        int maxTargets = 3;
        int maxAttemptsPerZone = 10;

        float minSpacing = redZoneRadius * 2.0f;
        float queenSafeRadius = redZoneRadius * 2.0f;
        float ringRadius = redZoneRadius * 5.0f;

        var allUnits = Object.FindObjectsByType<ChessPieceFPSController>(FindObjectsSortMode.None);

        List<ChessPieceFPSController> enemies = new();
        foreach (var u in allUnits)
        {
            if (u == null || u == fps)
                continue;

            enemies.Add(u);
        }

        if (enemies.Count == 0)
            return result;

        enemies.Sort((a, b) =>
            Vector3.Distance(fps.transform.position, a.transform.position)
            .CompareTo(Vector3.Distance(fps.transform.position, b.transform.position)));

        int targetCount = Mathf.Min(maxTargets, enemies.Count);
        int ringCount = grenadeCount / targetCount;

        for (int i = 0; i < targetCount && result.Count < grenadeCount; i++)
        {
            var target = enemies[i];
            Vector3 center = target.transform.position;

            // Core zone directly at enemy position
            TryAddZone(center);

            // Ring zones around enemy (same Y as enemy)
            for (int r = 0; r < ringCount && result.Count < grenadeCount; r++)
            {
                for (int attempt = 0; attempt < maxAttemptsPerZone; attempt++)
                {
                    float angle = Random.Range(0f, 360f);
                    Vector3 offset = new Vector3(
                        Mathf.Cos(angle * Mathf.Deg2Rad),
                        0f,
                        Mathf.Sin(angle * Mathf.Deg2Rad)
                    ) * Random.Range(minSpacing, ringRadius);

                    Vector3 candidate = center + offset;

                    if (TryAddZone(candidate))
                        break;
                }
            }
        }

        return result;

        bool TryAddZone(Vector3 pos)
        {
            if (result.Count >= grenadeCount)
                return false;

            if (Vector3.Distance(pos, fps.transform.position) < queenSafeRadius)
                return false;

            foreach (var p in result)
            {
                if (Vector3.Distance(p, pos) < minSpacing)
                    return false;
            }

            result.Add(pos);
            return true;
        }
    }
    private void ApplyGrenadeDamage()
    {
        var targets = Object.FindObjectsByType<ChessPieceFPSController>(FindObjectsSortMode.None);
        Dictionary<IDamageable, float> damageMap = new();

        foreach (var rz in activeRedZones)
        {
            Vector3 center = rz.transform.position;

            foreach (var target in targets)
            {
                if (target == fps)
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

                if (!target.TryGetComponent<IDamageable>(out var dmgable))
                    continue;

                if (!damageMap.ContainsKey(dmgable) || dmg > damageMap[dmgable])
                    damageMap[dmgable] = dmg;
            }
        }

        foreach (var pair in damageMap)
        {
            pair.Key.TakeDamage(new DamageInfo
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
        var renderers = pushSphereInstance.GetComponentsInChildren<Renderer>();
        foreach (var r in renderers)
        {
            foreach (var mat in r.materials)
            {
                mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            }
        }
        pushSphereInstance.transform.localScale = Vector3.zero;
        sphereRoutine = StartCoroutine(ExpandSphere());
    }

    private IEnumerator ExpandSphere()
    {
        float currentScale = 0f;
        float targetScale = pushRadius * 2f;

        while (currentScale < targetScale)
        {
            currentScale += Time.deltaTime * pushSphereScaleSpeed;
            float clamped = Mathf.Min(currentScale, targetScale);
            float progress = clamped / targetScale;

            float alpha = Mathf.Lerp(sphereFadeStartNormalized, 0.0f, progress);

            var renderers = pushSphereInstance.GetComponentsInChildren<Renderer>();
            foreach (var r in renderers)
            {
                foreach (var mat in r.materials)
                {
                    Color c = mat.color;
                    c.a = alpha;
                    mat.color = c;
                }
            }

            pushSphereInstance.transform.localScale = Vector3.one * clamped;
            yield return null;
        }

        DespawnSphere();
    }

    private void DespawnSphere()
    {
        if (sphereRoutine != null)
        {
            StopCoroutine(sphereRoutine);
            sphereRoutine = null;
        }

        if (pushSphereInstance)
            Destroy(pushSphereInstance);
    }

    // =========================
    // PARABOLA
    // =========================
    private Vector3[] BuildParabolicPath(Vector3 start, Vector3 desiredEnd)
    {
        const float stepT = 0.05f; // parametric step

        List<Vector3> points = new();
        points.Add(start);

        Vector3 forward = (desiredEnd - start).normalized;
        float totalDistance = Vector3.Distance(start, desiredEnd);

        Vector3 prev = start;
        float t = 0f;

        float radius = controller.radius;
        float height = controller.height;
        Vector3 centerOffset = controller.center;

        while (true)
        {
            t += stepT;

            Vector3 pos = start + forward * (totalDistance * t);
            float yOffset = 4f * maxPushHeight * t * (1f - t);
            pos.y = start.y + yOffset;

            Vector3 delta = pos - prev;
            float dist = delta.magnitude;

            if (dist > 0f)
            {
                // capsule tại vị trí PREV
                Vector3 worldCenter = prev + centerOffset;

                float halfHeight = Mathf.Max(0f, (height * 0.5f) - radius);

                Vector3 p1 = worldCenter + Vector3.up * halfHeight;
                Vector3 p2 = worldCenter - Vector3.up * halfHeight;

                if (Physics.CapsuleCast(
                    p1,
                    p2,
                    radius,
                    delta.normalized,
                    out RaycastHit hit,
                    dist,
                    obstacleLayer,
                    QueryTriggerInteraction.Ignore))
                {
                    points.Add(hit.point);
                    break;
                }
            }

            points.Add(pos);
            prev = pos;
        }

        return points.ToArray();
    }

    // =========================
    // HELPERS (UNCHANGED)
    // =========================
    private void LockAttack(bool locked)
    {
        if (fps == null)
            return;

        if (locked) fps.DisableAction();
        else fps.EnableAction();
    }

    private void CleanupRedZones()
    {
        foreach (var rz in activeRedZones)
            ReturnRedZone(rz);

        activeRedZones.Clear();
    }

    private RedZone GetRedZone()
    {
        if (redZonePool.Count > 0)
            return redZonePool.Dequeue();

        var go = Instantiate(redZonePrefab);
        var rz = go.GetComponent<RedZone>();
        if (rz == null)
            rz = go.AddComponent<RedZone>();
        return rz;
    }

    private void ReturnRedZone(RedZone rz)
    {
        rz.Deactivate();
        redZonePool.Enqueue(rz);
    }
}
