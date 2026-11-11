using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// QUEEN — shockwave + grenade barrage, one sequence:
///   1. Push: a sphere expands (PushWave, on its own: once cast it is never cut short, even if the
///      queen dies); every piece it reaches is knocked away physically the moment it touches it
///      (full force at the centre fading to pushMinForceShare at the edge, small bounce; pieces
///      collide and slide), even one moved by another skill. Rook walls fall over sideways.
///      Effect only: allies are pushed too.
///   2. Barrage: grenades mark red zones around up to 3 nearest ENEMIES within range; every
///      zone sits inside the arena, dropped onto the first surface below it.
///   3. After the warning, each zone explodes: damage falls off with distance (enemies only)
///      and a blast knocks everyone around (like the knight's landing); walls and terrain between
///      a zone and a piece block both. Grenades already fired explode even if the queen dies
///      (GrenadeVolley).
/// The queen cannot shoot (weapon held blocked) during push + firing; the skill ends when the
/// grenades have exploded.
/// </summary>
public class QueenSkill : ActiveSkill
{
    private ChessPieceFPSController fps;

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
    [Tooltip("Only enemies within this distance of the queen are targeted.")]
    public float targetRange = 40f;
    [Tooltip("The landing spot is searched from this high above the target's feet: a zone lands on the first surface below (ground, a rook wall), not on tree tops / roofs higher up.")]
    public float grenadeDropHeight = 3f;
    [Tooltip("How far below the target's feet a zone can still land (lower ground around the target).")]
    public float grenadeMaxDrop = 30f;

    // =========================
    // PUSH
    // =========================
    [Header("Push Settings")]
    public float pushRadius = 8f;
    [Tooltip("Knock-back right next to the queen; fades to pushMinForceShare of it at the edge of the push radius.")]
    public float pushForce = 20f;
    [Tooltip("Share of the push force left at the edge of the push radius.")]
    [Range(0f, 1f)] public float pushMinForceShare = 0.5f;
    [Tooltip("Upward bounce = this x the knock-back strength.")]
    public float bounceFactor = 0.35f;
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
    [Tooltip("Blast knock-back at the centre of an exploding zone; fades to nothing at its edge.")]
    public float explosionForce = 10f;

    // =========================
    // VISUAL
    // =========================
    [Header("Visual")]
    public GameObject redZonePrefab;
    public GameObject pushSpherePrefab;
    [Tooltip("Push sphere alpha when it starts expanding.")]
    [Range(0f, 1f)]
    public float sphereMaxAlpha = 0.65f;
    [Tooltip("Push sphere alpha when it reaches the full push radius.")]
    [Range(0f, 1f)]
    public float sphereMinAlpha = 0f;

    // =========================
    // MARK
    // =========================
    [Header("Mark")]
    [Tooltip("Enemies pushed, targeted by the grenades or caught in a blast are revealed to the queen's side (through walls) this long.")]
    public float markSeconds = 3f;
    [Tooltip("Material made from the 'FPSChess/XRayReveal' shader (a runtime fallback is created if empty).")]
    public Material revealMaterial;

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
    private const float MaxWaitForLanding = 3f; // safety: a piece knocked off the map never blocks the barrage

    private readonly List<RedZone> activeRedZones = new();
    private readonly Queue<RedZone> redZonePool = new();

    private readonly List<ChessPieceFPSController> pushedPieces = new();
    private float waitForLanding;

    private PushWave wave; // the shockwave still expanding
    private Coroutine barrageRoutine;
    private GrenadeVolley volley;
    private bool barrageStarted;
    private bool attackLocked;

    protected override float GetActiveDuration(in SkillContext ctx) =>
        // Safety cap only: the sequence ends itself via FinishActive().
        activeDuration + fireAnimationTime + redZoneWarningTime + MaxWaitForLanding + 3f;

    public override void Initialize(in SkillContext ctx)
    {
        base.Initialize(ctx);
        fps = ctx.fps;
        if (revealMaterial != null)
            RevealSystem.XRayMaterial = revealMaterial;
    }

    protected override void OnEnterActive(in SkillContext ctx)
    {
        barrageStarted = false;
        waitForLanding = 0f;
        LockAttack(true);
        ExecutePushPhase();
    }

    protected override void TickActive(in SkillContext ctx)
    {
        // Không có enemy hợp lệ để push → spawn red zone trực tiếp
        // (otherwise: once the shockwave is over and every pushed piece is back on the ground).
        if (!barrageStarted && wave == null && AllPushedPiecesLanded(ctx.deltaTime))
            StartBarrage();
    }

    protected override void OnExitActive(in SkillContext ctx)
    {
        if (barrageRoutine != null)
        {
            StopCoroutine(barrageRoutine); // no more grenades are fired
            barrageRoutine = null;
        }

        // Grenades already fired still explode on their own (even if the queen is dying).
        if (volley != null)
        {
            volley.Detach();
            volley = null;
            activeRedZones.Clear();
        }

        LockAttack(false);
        CleanupRedZones();
        AbortAllPushes();
    }

    // =========================
    // PUSH PHASE
    // =========================
    /// <summary>
    /// Something to push (a piece or a rook wall) in the radius: the shockwave starts here and runs on its own
    /// (PushWave). Nothing in it: no sphere, the barrage comes straight away.
    /// </summary>
    private void ExecutePushPhase()
    {
        Vector3 center = transform.position;
        bool any = false;

        // Pieces (enemy layer) and rook walls (their collider sits on the Default layer).
        foreach (var hit in Physics.OverlapSphere(center, pushRadius, enemyLayer | obstacleLayer, QueryTriggerInteraction.Ignore))
        {
            ChessPieceFPSController target = SkillTargeting.PieceOf(hit);
            if (target != null ? SkillTargeting.IsAffectable(fps, target) : hit.GetComponentInParent<WallSegment>() != null)
            {
                any = true;
                break;
            }
        }
        if (!any)
            return;

        PushWave created = null;
        created = PushWave.Create(fps, center, new PushWave.Settings
        {
            radius = pushRadius,
            force = pushForce,
            minForceShare = pushMinForceShare,
            bounceFactor = bounceFactor,
            scaleSpeed = pushSphereScaleSpeed,
            mask = enemyLayer | obstacleLayer,
            spherePrefab = pushSpherePrefab,
            maxAlpha = sphereMaxAlpha,
            minAlpha = sphereMinAlpha,
            markSeconds = markSeconds
        }, p => pushedPieces.Add(p), () => { if (wave == created) wave = null; });
        wave = created;
    }

    private bool AllPushedPiecesLanded(float dt)
    {
        foreach (var p in pushedPieces)
            if (SkillTargeting.IsAlive(p) && p.IsKnockedBack)
            {
                waitForLanding += dt;
                return waitForLanding >= MaxWaitForLanding;
            }
        return true;
    }

    private void AbortAllPushes()
    {
        // A shockwave already cast goes on by itself (it is never cut short).
        if (wave != null)
        {
            wave.Detach();
            wave = null;
        }
        pushedPieces.Clear();
    }

    // =========================
    // RED ZONE LOGIC
    // =========================
    private void StartBarrage()
    {
        barrageStarted = true;

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

        // Every grenade explodes together, after the warning that follows the last one fired.
        volley = GrenadeVolley.Create(fps, new GrenadeVolley.Settings
        {
            radius = redZoneRadius,
            heightRange = heightRange,
            minDamage = minDamage,
            maxDamage = maxDamage,
            damageCurve = damageCurve,
            explosionForce = explosionForce,
            bounceFactor = bounceFactor,
            obstacleLayer = obstacleLayer,
            markSeconds = markSeconds
        }, Time.time + (positions.Count - 1) * interval + redZoneWarningTime, ReturnRedZone, OnVolleyExploded);

        for (int i = 0; i < positions.Count; i++)
        {
            // Every zone lives exactly until the common explosion.
            float untilExplosion = (positions.Count - 1 - i) * interval + redZoneWarningTime;

            var rz = GetRedZone();
            if (rz != null)
            {
                rz.HeightRange = heightRange; // drawn exactly as big and as high as it hurts
                rz.Activate(positions[i], redZoneRadius, untilExplosion);
                activeRedZones.Add(rz);
                volley.Add(rz);
            }

            yield return new WaitForSeconds(interval);
        }

        // Firing done: the queen can fight again while the zones count down.
        LockAttack(false);
        barrageRoutine = null;
    }

    private void OnVolleyExploded()
    {
        volley = null;
        activeRedZones.Clear(); // the volley gave them back to the pool
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

        Bounds arena = default;
        bool hasArena = ArenaController.Current != null && ArenaController.Current.TryGetBounds(out arena);

        List<ChessPieceFPSController> enemies = new();
        foreach (var u in Object.FindObjectsByType<ChessPieceFPSController>(FindObjectsSortMode.None))
        {
            if (!SkillTargeting.IsAffectable(fps, u))
                continue;
            if (SkillTargeting.IsAlly(fps, u))
                continue; // grenades target enemies only
            Vector3 d = u.transform.position - fps.transform.position;
            if (new Vector2(d.x, d.z).magnitude > targetRange)
                continue; // out of range
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
            RevealSystem.Reveal(enemies[i], fps.Team, markSeconds); // targeted: marked for the queen's side
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
            if (hasArena && !ArenaController.ContainsXZ(arena, pos))
                return false; // only inside the arena

            // The grenade falls from above: the zone sits on the first surface below
            // (the ground under the target, higher terrain, or the top of a rook wall).
            Vector3 from = new Vector3(pos.x, pos.y + grenadeDropHeight, pos.z);
            if (!Physics.Raycast(from, Vector3.down, out RaycastHit ground, grenadeDropHeight + grenadeMaxDrop, obstacleLayer, QueryTriggerInteraction.Ignore))
                return false;
            pos = ground.point;

            if (Vector3.Distance(pos, fps.transform.position) < queenSafeRadius)
                return false;
            foreach (var p in result)
                if (Vector3.Distance(p, pos) < minSpacing)
                    return false;
            result.Add(pos);
            return true;
        }
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
