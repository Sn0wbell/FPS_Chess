using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// KING — gravity well. Front shield on; two zones follow the king:
///   - pull zone : after a short delay, pieces inside are pulled towards the king on an arc,
///                 ending at a distance proportional to where they started (inside the gravity zone);
///   - gravity zone: pieces inside move and jump slower.
/// Effects only (no damage), so allies are affected too.
/// </summary>
public class KingSkill : ActiveSkill
{
    private ChessPieceFPSController fps;
    private CharacterController controller;

    [Header("References")]
    [SerializeField] private KingHealth kingHealth;

    [Header("Zones")]
    [SerializeField] private float pullRadius = 8f;
    [SerializeField] private float gravityRadius = 4f;
    [SerializeField] private float pullExposeDelay = 1f;

    [Header("Pull")]
    [Tooltip("Path points advanced per frame at 60 FPS (frame-rate independent).")]
    [SerializeField] private float pullSpeed = 0.1f;

    [Header("Gravity Debuff")]
    [SerializeField] private float moveMultiplier = 0.4f;
    [SerializeField] private float jumpMultiplier = 0.25f;

    [Header("Active")]
    [SerializeField] private float activeDuration = 4f;

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
    private bool pullExecuted;

    private readonly HashSet<ChessPieceFPSController> gravityAffected = new();
    private readonly HashSet<ChessPieceFPSController> gravityCurrent = new();
    private readonly List<ChessPieceFPSController> gravityStale = new();
    private readonly Dictionary<ChessPieceFPSController, PullSession> activePulls = new();
    private readonly List<ChessPieceFPSController> pullKeys = new();
    private readonly Collider[] overlapBuffer = new Collider[32];

    private struct PullSession
    {
        public List<Vector3> path;
        public float progress;
    }

    // =========================
    // AI Awareness
    // =========================
    public bool IsActivePullZone => pullZoneInstance != null && pullZoneInstance.gameObject.activeSelf;
    public bool IsActiveGravityZone => gravityZoneInstance != null && gravityZoneInstance.gameObject.activeSelf;
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
        controller = ctx.controller;
        if (kingHealth == null)
            kingHealth = GetComponent<KingHealth>();
    }

    protected override void OnEnterActive(in SkillContext ctx)
    {
        pullExecuted = false;
        if (kingHealth != null)
            kingHealth.ActivateShield();

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

        if (kingHealth != null)
            kingHealth.DeactivateShield();
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

    private float CageHalfHeight => pullZonePrefab != null ? pullZonePrefab.CageHeight * 0.5f : 2f;

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

        float height = Mathf.Min(CageHalfHeight, horizontal);

        var path = new List<Vector3> { start };
        Vector3 prev = start;

        for (float u = PathStep; u <= 1f + 1e-4f; u += PathStep)
        {
            float uc = Mathf.Min(u, 1f);
            Vector3 pos = Vector3.Lerp(start, end, uc);
            pos.y += 4f * height * uc * (1f - uc);

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
        float half = CageHalfHeight;
        int count = Physics.OverlapCapsuleNonAlloc(center - Vector3.up * half, center + Vector3.up * half,
            pullRadius * 1.15f, overlapBuffer, enemyLayer, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
        {
            ChessPieceFPSController target = SkillTargeting.PieceOf(overlapBuffer[i]);
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

            if (!target.TryBeginForcedMove(this))
                continue; // already pushed/pulled by another skill

            activePulls[target] = new PullSession { path = path, progress = 0f };
            target.SuppressMovement(this);
            target.ForceStopAllMotion();
            target.SetGravityEnabled(this, false);
        }
    }

    private void UpdatePullMotion(float dt)
    {
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
                Vector3 delta = s.path[nextIndex] - target.transform.position;
                Vector3 bodyOffset = cc.bounds.center - target.transform.position; // cast at body height
                int subSteps = Mathf.Max(1, Mathf.CeilToInt(delta.magnitude / 0.1f));
                Vector3 step = delta / subSteps;
                float stepDist = step.magnitude;

                for (int j = 0; j < subSteps && stepDist > 0f; j++)
                {
                    if (Physics.SphereCast(target.transform.position + bodyOffset, cc.radius, step / stepDist, out RaycastHit hit,
                            stepDist, obstacleLayer, QueryTriggerInteraction.Ignore))
                    {
                        blocked = true;
                        break;
                    }
                    cc.Move(step);
                }
            }

            if (blocked || nextIndex >= last)
                ReleasePulled(target);
            else
                activePulls[target] = s;
        }
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

        int count = Physics.OverlapSphereNonAlloc(transform.position, gravityRadius, overlapBuffer, enemyLayer, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            ChessPieceFPSController target = SkillTargeting.PieceOf(overlapBuffer[i]);
            if (!SkillTargeting.IsAffectable(fps, target) || !gravityCurrent.Add(target))
                continue;

            if (gravityAffected.Add(target))
                target.OverrideMovement(this, moveMultiplier, jumpMultiplier); // keyed by this king
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
