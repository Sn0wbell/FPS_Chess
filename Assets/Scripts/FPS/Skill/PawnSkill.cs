using System.Collections.Generic;
using UnityEngine;

public class PawnCloneMarker : MonoBehaviour
{
    public bool IsClone;
}
public class PawnSkill : ActiveSkill
{
    [Header("Clone Settings")]
    [SerializeField] private GameObject pawnClonePrefab;
    [SerializeField] private int cloneCount = 5;
    [Tooltip("Lifetime of each clone in seconds")]
    [SerializeField] private float cloneLifetime = 3f;

    [Header("Scatter")]
    [SerializeField] private float scatterForce = 6f;
    [SerializeField] private float upwardForce = 2.5f;
    [SerializeField] private float spawnRadius = 1f;
    [SerializeField] private int maxSpawnAttempts = 10;

    [Header("VFX")]
    [SerializeField] private GameObject spawnVFX;
    [SerializeField] private GameObject destroyVFX;

    [Header("Timing")]
    [SerializeField] private float dissolveTime = 0.25f;

    private readonly List<PawnCloneHealth> activeClones = new();
    private PawnClonePool pool;
    private void OnDestroy()
    {
        if (pool != null)
        {
            ClearExistingClones();
        }
    }
    private void Awake()
    {
        pool = new PawnClonePool(pawnClonePrefab, transform);
        blockMask = SkillBlockMask.None;
    }

    // Active duration = maximum lifetime of clones
    protected override float GetActiveDuration(in SkillContext ctx)
    {
        // ActiveSkill stays active while clones exist
        return cloneLifetime;
    }

    protected override void OnEnterActive(in SkillContext ctx)
    {
        ClearExistingClones();

        for (int i = 0; i < cloneCount; i++)
        {
            SpawnClone(ctx);
        }

        if (ctx.fps != null)
        {
            Vector3 dir = Random.onUnitSphere;
            dir.y = Mathf.Abs(dir.y);

            Vector3 impulse = dir.normalized * scatterForce + Vector3.up * upwardForce;

            ctx.fps.AddExternalImpulse(impulse);
        }
    }

    protected override void TickActive(in SkillContext ctx)
    {
        // Remove destroyed clones from tracking
        activeClones.RemoveAll(c => c == null || !c.gameObject.activeInHierarchy);
    }

    protected override void OnExitActive(in SkillContext ctx)
    {
        // Cleanup all remaining clones safely
        ClearExistingClones();
    }

    protected override void OnForceCancel(in SkillContext ctx)
    {
        // Force cleanup
        ClearExistingClones();
        base.OnForceCancel(ctx);
    }

    private void SpawnClone(SkillContext ctx)
    {
        Vector3 pos = Vector3.zero;
        bool validPos = false;

        for (int attempt = 0; attempt < maxSpawnAttempts; attempt++)
        {
            Vector2 circle = Random.insideUnitCircle * spawnRadius;
            Vector3 candidate =
                transform.position +
                transform.right * circle.x +
                transform.forward * circle.y;

            candidate.y = transform.position.y;

            if (Physics.OverlapSphere(candidate, 0.3f).Length == 0)
            {
                pos = candidate;
                validPos = true;
                break;
            }
        }

        if (!validPos)
            pos = transform.position + Random.onUnitSphere * spawnRadius;

        PawnCloneHealth clone = pool.Get(pos, transform.rotation);

        Vector3 scatterDir = Random.onUnitSphere;
        scatterDir.y = Mathf.Abs(scatterDir.y);
        Vector3 initialVelocity = scatterDir.normalized * scatterForce + Vector3.up * upwardForce;

        clone.Activate(
            cloneLifetime,
            dissolveTime,
            destroyVFX,
            pool,
            initialVelocity
        );

        clone.gameObject.SetActive(true);

        if (spawnVFX)
            Object.Instantiate(spawnVFX, pos, Quaternion.identity);

        activeClones.Add(clone);
    }

    private void ClearExistingClones()
    {
        foreach (var clone in activeClones)
        {
            if (clone)
                clone.ForceDespawn();
        }
        activeClones.Clear();
    }
}
