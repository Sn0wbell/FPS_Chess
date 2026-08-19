using NUnit;
using System.Collections;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;
using static UnityEditor.PlayerSettings;

public class KingSkill : ActiveSkill
{
    // =========================
    // REFERENCES
    // =========================
    private ChessPieceFPSController fps;
    private CharacterController controller;

    [Header("References")]
    [SerializeField] private KingHealth kingHealth;

    // =========================
    // ZONES
    // =========================
    [Header("Zones")]
    [SerializeField] private float pullRadius = 8f;
    [SerializeField] private float gravityRadius = 4f;

    [SerializeField] private float pullExposeDelay = 1f;

    // =========================
    // PULL
    // =========================
    [Header("Pull")]
    [SerializeField] private float pullSpeed = 0.1f;

    // =========================
    // GRAVITY DEBUFF
    // =========================
    [Header("Gravity Debuff")]
    [SerializeField] private float moveMultiplier = 0.4f;
    [SerializeField] private float jumpMultiplier = 0.25f;

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

    private Zone pullZoneInstance;
    private Zone gravityZoneInstance;

    // =========================
    // STATE
    // =========================
    private bool pullExecuted;

    private readonly HashSet<ChessPieceFPSController> gravityAffected = new();
    private readonly Dictionary<ChessPieceFPSController, PullSession> activePulls = new();

    private readonly List<ChessPieceFPSController> pullKeys = new();
    private readonly List<ChessPieceFPSController> pullRemoveBuffer = new();

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
    public bool IsActivePullZone => pullZoneInstance != null;
    public bool IsActiveGravityZone => gravityZoneInstance != null;

    public float PullZoneRadius => pullRadius;
    public float GravityZoneRadius => gravityRadius;

    public Vector3 PullZoneCenter => pullZoneInstance ? pullZoneInstance.transform.position : transform.position;
    public Vector3 GravityZoneCenter => gravityZoneInstance ? gravityZoneInstance.transform.position : transform.position;

    public Vector3[] GetActiveZones()
    {
        List<Vector3> zones = new();

        if (pullZoneInstance)
            zones.Add(PullZoneCenter);

        if (gravityZoneInstance)
            zones.Add(GravityZoneCenter);

        return zones.ToArray();
    }

    public bool IsPositionInDanger(Vector3 pos)
    {
        if (pullZoneInstance && Vector3.Distance(pos, PullZoneCenter) <= pullRadius)
            return true;

        if (gravityZoneInstance && Vector3.Distance(pos, GravityZoneCenter) <= gravityRadius)
            return true;

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
    }

    protected override void OnEnterActive(in SkillContext ctx)
    {
        pullExecuted = false;
        kingHealth.ActivateShield();

        SpawnZones();
        StartCoroutine(PullDelayRoutine());
    }

    protected override void TickActive(in SkillContext ctx)
    {
        UpdateZoneFollow();
        UpdatePullMotion();
        UpdateGravityZone();
    }

    protected override void OnExitActive(in SkillContext ctx)
    {
        AbortAllPulls();
        ClearGravityDebuffs();

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
            pullZoneInstance = Instantiate(pullZonePrefab);
            pullZoneInstance.gameObject.SetActive(true);
            pullZoneInstance.transform.position = pos;

            pullZoneInstance.Activate(pos, pullRadius, pullExposeDelay * 2f);
        }

        if (gravityZonePrefab)
        {
            gravityZoneInstance = Instantiate(gravityZonePrefab);
            gravityZoneInstance.gameObject.SetActive(true);
            gravityZoneInstance.transform.position = pos;

            gravityZoneInstance.Activate(pos, gravityRadius, activeDuration);
        }
    }

    private void UpdateZoneFollow()
    {
        Vector3 pos = transform.position;

        if (pullZoneInstance != null)
            pullZoneInstance.transform.position = pos;

        if (gravityZoneInstance != null)
            gravityZoneInstance.transform.position = pos;
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
        ExecutePull();
    }
    private List<Vector3> BuildPullPath(Vector3 start, Vector3 end)
    {
        List<Vector3> pullPath = new List<Vector3>();
        const float stepTime = 0.05f;
        Vector3 pos = start;
        Vector3 prev = pos;
        pullPath.Add(pos);

        float t = 0f;
        float distance = Vector2.Distance(new Vector2(start.x, start.z), new Vector2(end.x, end.z));
        float duration = distance / pullSpeed;

        float height = Mathf.Min((pullZonePrefab.CageHeight / 2), distance);

        Vector3 heading = end - start;
        heading.y = 0;
        Vector3 direction = heading.normalized;

        int stepLimit = (int)distance * 100;
        while (stepLimit-- > 0)
        {
            t += stepTime;
            float normalizedT = t / duration;
            float baseY = Mathf.Lerp(start.y, end.y, normalizedT);
            float yOffset = 4f * height * t * (1f - t); // parabol
            pos = start + direction * (pullSpeed * t * duration);
            pos.y = baseY + yOffset;

            Vector3 delta = pos - prev;
            float dist = delta.magnitude;

            prev = pos;
            pullPath.Add(pos);

            if (dist > 0f &&
                Physics.SphereCast(prev, (controller.radius / 2), delta.normalized, out RaycastHit hit, dist, obstacleLayer, QueryTriggerInteraction.Ignore))
            {
                break;
            }

            if (Vector3.Distance(pos, end) <= 0.01) break;
        }

        if (stepLimit <= 0) return null;
        return pullPath;
    }
    private void ExecutePull()
    {
        if (pullExecuted) return;
        pullExecuted = true;

        float halfHeight = pullZonePrefab.CageHeight/2;

        Vector3 center = transform.position;
        Vector3 bottom = center - Vector3.up * halfHeight;
        Vector3 top = center + Vector3.up * halfHeight;

        Collider[] hits = Physics.OverlapCapsule(bottom, top, pullRadius*1.15f, enemyLayer, QueryTriggerInteraction.Ignore);

        foreach (var hit in hits)
        {
            if (!hit.TryGetComponent(out ChessPieceFPSController enemy))
                continue;

            Vector3 offset = enemy.transform.position - center;
            float dist = offset.magnitude;
            if (dist <= 0.001f) continue;

            float ratio = Mathf.Clamp01(dist / pullRadius);
            float targetDist = ratio * gravityRadius;
            Vector3 target = center + offset.normalized * targetDist;

            Vector3 start = enemy.transform.position;
            Vector3 end = target;

            PullSession session = new PullSession
            {
                path = BuildPullPath(start, end),
                progress = 0f
            };

            activePulls[enemy] = session;

            enemy.SuppressMovement(MovementOverrideSource.Skill);
            enemy.ForceStopAllMotion();
            enemy.SetGravityEnabled(false);
        }
    }
    private void UpdatePullMotion()
    {
        if (activePulls.Count == 0) return;

        pullKeys.Clear();
        pullRemoveBuffer.Clear();

        foreach (var k in activePulls.Keys)
            pullKeys.Add(k);

        for (int i = 0; i < pullKeys.Count; i++)
        {
            var enemy = pullKeys[i];

            if (enemy == null || !activePulls.TryGetValue(enemy, out PullSession s))
            {
                pullRemoveBuffer.Add(enemy);
                continue;
            }

            CharacterController enemyController = enemy.GetComponent<CharacterController>();

            if (enemyController == null)
            {
                pullRemoveBuffer.Add(enemy);
                continue;
            }

            if (s.path == null)
            {
                pullRemoveBuffer.Add(enemy);
                continue;
            }

            int allStep = s.path.Count;
            int index = Mathf.FloorToInt(s.progress);
            Vector3 prevPos = s.path[index];

            if (allStep == 0 || index >= allStep - 1)
            {
                pullRemoveBuffer.Add(enemy);
                continue;
            }

            s.progress += pullSpeed;

            int nextIndex = Mathf.FloorToInt(s.progress);
            if (nextIndex == index)
            {
                activePulls[enemy] = s;
                continue;
            }
            Vector3 nextPos = s.path[nextIndex];

            Vector3 delta = nextPos - prevPos;

            int subSteps = Mathf.Max(1, Mathf.CeilToInt(delta.magnitude / 0.1f));
            Vector3 step = delta / subSteps;
            Vector3 stepDir = step.normalized;
            float stepDist = step.magnitude;

            for (int j = 0; j < subSteps; j++)
            {
                if (Physics.SphereCast(
                    enemy.transform.position,
                    enemyController.radius,
                    stepDir,
                    out RaycastHit hit,
                    stepDist,
                    obstacleLayer,
                    QueryTriggerInteraction.Ignore))
                {
                    enemy.WarpPosition(hit.point + hit.normal * 0.01f);
                    pullRemoveBuffer.Add(enemy);
                    break;
                }
                else enemyController.Move(step);
            }

            if (nextIndex >= allStep - 1)
            {
                pullRemoveBuffer.Add(enemy);
            }
            else
            {
                activePulls[enemy] = s;
            }
        }

        // CLEANUP
        for (int i = 0; i < pullRemoveBuffer.Count; i++)
        {
            var e = pullRemoveBuffer[i];
            if (e != null)
            {
                e.ReleaseMovementSuppression(MovementOverrideSource.Skill);
                e.SetGravityEnabled(true);
            }

            activePulls.Remove(e);
        }
    }

    private void AbortAllPulls()
    {
        foreach (var e in activePulls.Keys)
        {
            if (e != null)
            {
                e.ReleaseMovementSuppression(MovementOverrideSource.Skill);
                e.SetGravityEnabled(true);
            }    
        }

        activePulls.Clear();
    }

    // =========================
    // GRAVITY ZONE
    // =========================
    private void UpdateGravityZone()
    {
        Vector3 center = transform.position;
        Collider[] hits = Physics.OverlapSphere(center, gravityRadius, enemyLayer);

        HashSet<ChessPieceFPSController> current = new();

        foreach (var hit in hits)
        {
            if (!hit.TryGetComponent(out ChessPieceFPSController enemy))
                continue;

            if (enemy == fps) continue;
            current.Add(enemy);

            if (!gravityAffected.Contains(enemy))
            {
                enemy.OverrideMovement(
                    MovementOverrideSource.GravityZone,
                    moveMultiplier,
                    jumpMultiplier
                );
                gravityAffected.Add(enemy);
            }
        }

        foreach (var e in gravityAffected)
        {
            if (e == null || !current.Contains(e))
                e?.ClearOverrideMovement(MovementOverrideSource.GravityZone);
        }

        gravityAffected.RemoveWhere(e => e == null || !current.Contains(e));
    }

    private void ClearGravityDebuffs()
    {
        foreach (var e in gravityAffected)
            e?.ClearOverrideMovement(MovementOverrideSource.GravityZone);

        gravityAffected.Clear();
    }
}