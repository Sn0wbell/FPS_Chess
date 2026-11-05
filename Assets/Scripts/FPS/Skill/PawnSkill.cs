using System.Collections.Generic;
using UnityEngine;

public class PawnCloneMarker : MonoBehaviour
{
    public bool IsClone;
}

/// <summary>
/// PAWN — decoys. Spawns clones that scatter in random directions (the pawn itself is flung
/// the same way, so it mixes in), aimed to land inside the arena (a clone that still leaves
/// it falls off and is defeated; the pawn follows the arena's out-of-bounds rule). Clones carry a copy of the
/// pawn's weapon (visual only), have a little health and deal no damage.
/// Whoever kills a clone is revealed to the pawn's side through walls for a few seconds.
/// The skill stays active while clones live; its cooldown starts when the last clone is gone
/// (killed or expired).
/// </summary>
public class PawnSkill : ActiveSkill
{
    [Header("Clone Settings")]
    [SerializeField] private GameObject pawnClonePrefab;
    [SerializeField] private int cloneCount = 5;
    [Tooltip("Lifetime of each clone in seconds")]
    [SerializeField] private float cloneLifetime = 3f;
    [Tooltip("Clone health: it takes a few hits to tell a decoy from the real pawn.")]
    [SerializeField] private float cloneHealth = 30f;

    [Header("Scatter (clones and the pawn itself)")]
    [SerializeField] private float scatterForce = 6f;
    [SerializeField] private float upwardForce = 2.5f;
    [SerializeField] private float spawnRadius = 1f;
    [SerializeField] private int maxSpawnAttempts = 10;
    [Tooltip("Thrown bodies must land at least this far inside the arena edge (m).")]
    [SerializeField] private float arenaMargin = 1f;

    [Header("Spawn Checks")]
    [Tooltip("Ground / walls used to place clones (exclude the pieces layer).")]
    [SerializeField] private LayerMask groundMask = 1; // Default
    [SerializeField] private float cloneRadius = 0.35f;

    [Header("Reveal The Killer")]
    [Tooltip("Seconds the piece that killed a clone stays visible to the pawn's side, through walls.")]
    [SerializeField] private float revealSeconds = 2f;
    [Tooltip("Material made from the 'FPSChess/XRayReveal' shader (a runtime fallback is created if empty).")]
    [SerializeField] private Material revealMaterial;

    [Header("VFX")]
    [SerializeField] private GameObject spawnVFX;
    [SerializeField] private GameObject destroyVFX;

    [Header("Timing")]
    [SerializeField] private float dissolveTime = 0.25f;

    private const string WeaponVisualName = "CloneWeaponVisual";

    private readonly List<PawnCloneHealth> activeClones = new();
    private PawnClonePool pool;
    private ChessPieceFPSController fps;

    private bool hasArena;
    private Bounds arena;

    // Weapon look-alike for the clones (stripped copy of the pawn's weapon).
    private GameObject weaponTemplate;
    private GameObject weaponTemplateSource;
    private Vector3 weaponLocalPos;
    private Quaternion weaponLocalRot;

    private void Awake()
    {
        pool = new PawnClonePool(pawnClonePrefab, transform);
        if (revealMaterial != null)
            RevealSystem.XRayMaterial = revealMaterial;
    }

    private void OnDestroy()
    {
        ClearExistingClones();
        if (weaponTemplate != null)
            Destroy(weaponTemplate);
    }

    public override void Initialize(in SkillContext ctx)
    {
        base.Initialize(ctx);
        fps = ctx.fps;
    }

    // Active duration = maximum lifetime of clones
    // ActiveSkill stays active while clones exist
    protected override float GetActiveDuration(in SkillContext ctx) => cloneLifetime;

    protected override void OnEnterActive(in SkillContext ctx)
    {
        ClearExistingClones();
        hasArena = ArenaController.Current != null && ArenaController.Current.TryGetBounds(out arena);
        PrepareWeaponTemplate();

        for (int i = 0; i < cloneCount; i++)
            SpawnClone(ctx);

        // The pawn is flung exactly like its clones (random direction), so it mixes in.
        if (ctx.fps != null)
            ctx.fps.AddExternalImpulse(PickScatterVelocity(transform.position));
    }

    protected override void TickActive(in SkillContext ctx)
    {
        // Remove destroyed clones from tracking
        activeClones.RemoveAll(c => c == null || !c.gameObject.activeInHierarchy);

        // Every clone has been killed before its time ran out: the cooldown starts now.
        bool anyAlive = false;
        foreach (var c in activeClones)
            if (!c.IsDying) { anyAlive = true; break; }

        if (!anyAlive)
            FinishActive();
    }

    // Cleanup all remaining clones safely
    protected override void OnExitActive(in SkillContext ctx) => ClearExistingClones();

    // Force cleanup
    protected override void OnForceCancel(in SkillContext ctx) => ClearExistingClones();

    // =========================
    // SCATTER (random, but always landing inside the arena)
    // =========================

    /// <summary>Random upward-hemisphere throw whose landing point stays inside the arena.</summary>
    private Vector3 PickScatterVelocity(Vector3 from)
    {
        Vector3 v = Vector3.zero;
        for (int attempt = 0; attempt < maxSpawnAttempts; attempt++)
        {
            Vector3 dir = Random.onUnitSphere;
            dir.y = Mathf.Abs(dir.y);
            v = dir.normalized * scatterForce + Vector3.up * upwardForce;

            if (!hasArena || ArenaController.ContainsXZ(arena, ArenaController.PredictLanding(from, v, ChessPieceFPSController.ScatterGravity), arenaMargin))
                return v;
        }

        // Near the edge and unlucky: throw towards the arena centre (same strength, shorter).
        Vector3 toCenter = arena.center - from;
        toCenter.y = 0f;
        if (toCenter.sqrMagnitude < 1e-4f)
            return Vector3.up * (scatterForce + upwardForce);
        float flat = Mathf.Min(scatterForce, toCenter.magnitude * 0.5f);
        return toCenter.normalized * flat + Vector3.up * (upwardForce + scatterForce * 0.5f);
    }

    private bool TryFindClonePosition(out Vector3 pos)
    {
        for (int attempt = 0; attempt < maxSpawnAttempts; attempt++)
        {
            Vector2 circle = Random.insideUnitCircle * spawnRadius;
            Vector3 above = transform.position + transform.right * circle.x + transform.forward * circle.y + Vector3.up * 2f;

            if (!Physics.Raycast(above, Vector3.down, out RaycastHit ground, 6f, groundMask, QueryTriggerInteraction.Ignore))
                continue;

            // The clone's real body (its CharacterController), lifted off the ground along the surface normal
            // so a slope does not count as "blocked"; only the ground / obstacles block it.
            CloneBody(out float radius, out float height);
            Vector3 bottom = ground.point + ground.normal * (radius + 0.05f);
            Vector3 top = bottom + Vector3.up * Mathf.Max(0f, height - 2f * radius);
            if (Physics.CheckCapsule(bottom, top, radius * 0.95f, groundMask, QueryTriggerInteraction.Ignore))
                continue;

            pos = bottom - Vector3.up * radius; // the clone's feet
            return true;
        }

        pos = transform.position;
        return false;
    }

    /// <summary>Size of the clone's body (its prefab's CharacterController); cloneRadius when it has none.</summary>
    private void CloneBody(out float radius, out float height)
    {
        CharacterController body = pawnClonePrefab != null ? pawnClonePrefab.GetComponent<CharacterController>() : null;
        radius = body != null ? body.radius : cloneRadius;
        height = body != null ? Mathf.Max(body.height, 2f * radius) : 2f * cloneRadius;
    }

    private void SpawnClone(SkillContext ctx)
    {
        TryFindClonePosition(out Vector3 pos);

        PawnCloneHealth clone = pool.Get(pos, transform.rotation);
        if (clone == null)
            return;

        var cloneFps = clone.GetComponent<ChessPieceFPSController>();
        if (cloneFps != null && ctx.fps != null)
            cloneFps.SetTeam(ctx.fps.Team);

        AttachWeaponVisual(clone);

        clone.Activate(cloneLifetime, dissolveTime, destroyVFX, pool, PickScatterVelocity(pos), cloneHealth, OnCloneKilled);
        clone.gameObject.SetActive(true);

        if (spawnVFX)
            Instantiate(spawnVFX, pos, Quaternion.identity);

        activeClones.Add(clone);
    }

    // =========================
    // REVEAL THE KILLER
    // =========================
    private void OnCloneKilled(PawnCloneHealth clone, DamageInfo info)
    {
        if (fps == null || info.source == null)
            return;

        ChessPieceFPSController killer = info.source.GetComponentInParent<ChessPieceFPSController>();
        if (killer == null || killer == fps || killer.IsPawnClone || SkillTargeting.IsAlly(fps, killer))
            return;

        RevealSystem.Reveal(killer, fps.Team, revealSeconds);
    }

    // =========================
    // WEAPON LOOK-ALIKE
    // =========================
    private GameObject FindOwnerWeapon()
    {
        if (fps == null) return null;
        WeaponController w = fps.GetCurrentWeapon();
        if (w == null) w = fps.GetComponentInChildren<WeaponController>();
        return w != null ? w.gameObject : null;
    }

    /// <summary>Builds (once per weapon) an inactive, script-free, collider-free copy of the pawn's weapon.</summary>
    private void PrepareWeaponTemplate()
    {
        GameObject source = FindOwnerWeapon();
        if (source == weaponTemplateSource && (source == null || weaponTemplate != null))
            return;

        if (weaponTemplate != null)
            Destroy(weaponTemplate);
        weaponTemplate = null;
        weaponTemplateSource = source;
        if (source == null)
            return;

        // Copy under an INACTIVE parent: the copy's scripts never Awake.
        var holder = new GameObject(WeaponVisualName + "Template");
        holder.SetActive(false);
        holder.transform.SetParent(transform, false);

        GameObject copy = Instantiate(source, holder.transform);
        copy.name = WeaponVisualName;
        foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true)) DestroyImmediate(mb);
        foreach (var col in copy.GetComponentsInChildren<Collider>(true)) DestroyImmediate(col);
        foreach (var rb in copy.GetComponentsInChildren<Rigidbody>(true)) DestroyImmediate(rb);
        foreach (var audio in copy.GetComponentsInChildren<AudioSource>(true)) DestroyImmediate(audio);

        weaponTemplate = holder;
        // Pose relative to the pawn's root, reused on every clone.
        weaponLocalPos = transform.InverseTransformPoint(source.transform.position);
        weaponLocalRot = Quaternion.Inverse(transform.rotation) * source.transform.rotation;
    }

    private void AttachWeaponVisual(PawnCloneHealth clone)
    {
        Transform old = clone.transform.Find(WeaponVisualName);
        if (old != null)
            Destroy(old.gameObject);

        if (weaponTemplate == null || weaponTemplate.transform.childCount == 0)
            return;

        GameObject visual = Instantiate(weaponTemplate.transform.GetChild(0).gameObject, clone.transform);
        visual.name = WeaponVisualName;
        visual.transform.position = clone.transform.TransformPoint(weaponLocalPos);
        visual.transform.rotation = clone.transform.rotation * weaponLocalRot;
        visual.SetActive(true);
    }

    private void ClearExistingClones()
    {
        foreach (var clone in activeClones)
        {
            if (clone == null) continue;

            if (IsTearingDown)
                Destroy(clone.gameObject); // the pool lives under this dying pawn: never re-parent into it
            else if (clone.IsDying)
                continue;                  // already dissolving: it returns to the pool by itself
            else if (pool != null)
                clone.ForceDespawn();
        }
        activeClones.Clear();
    }
}
