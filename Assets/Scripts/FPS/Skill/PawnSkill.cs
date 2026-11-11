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
/// pawn's weapon (in their WeaponHolder), have a little health and deal no damage.
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

    private readonly List<PawnCloneHealth> activeClones = new();
    private PawnClonePool pool;
    private ChessPieceFPSController fps;

    private bool hasArena;
    private Bounds arena;

    // Weapon look-alike for the clones (stripped copy of the pawn's weapon).

    private void Awake()
    {
        pool = new PawnClonePool(pawnClonePrefab, transform);
        if (revealMaterial != null)
            RevealSystem.XRayMaterial = revealMaterial;
    }

    private void OnDestroy()
    {
        ClearExistingClones();
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
        ClearExistingClones(shatter: true);
        hasArena = ArenaController.Current != null && ArenaController.Current.TryGetBounds(out arena);

        for (int i = 0; i < cloneCount; i++)
            SpawnClone(ctx);

        // The pawn is flung exactly like its clones (random direction), so it mixes in.
        if (ctx.fps != null)
            ctx.fps.AddExternalImpulse(PickScatterVelocity(transform.position, ctx.fps));
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
    protected override void OnExitActive(in SkillContext ctx) => ClearExistingClones(shatter: true);

    // Force cleanup
    protected override void OnForceCancel(in SkillContext ctx) => ClearExistingClones();

    // =========================
    // SCATTER (random, but always landing inside the arena)
    // =========================

    /// <summary>The clones' own controller (their prefab): their throw is predicted with its gravity and bounces.</summary>
    private ChessPieceFPSController CloneBodyController() =>
        pawnClonePrefab != null ? pawnClonePrefab.GetComponent<ChessPieceFPSController>() : null;

    /// <summary>
    /// Random upward-hemisphere throw whose resting point stays inside the arena
    /// (predicted with the thrown body's gravity and bounces).
    /// </summary>
    private Vector3 PickScatterVelocity(Vector3 from, ChessPieceFPSController body)
    {
        float gravity = body != null ? body.Gravity : -9.81f;
        float bounciness = body != null ? body.GroundBounciness : 0f;
        float minBounce = body != null ? body.MinBounceSpeed : float.PositiveInfinity;
        float friction = body != null ? body.GroundBounceFriction : 1f;
        int maxBounces = body != null ? body.MaxGroundBounces : 0;
        Vector3 v = Vector3.zero;
        for (int attempt = 0; attempt < maxSpawnAttempts; attempt++)
        {
            Vector3 dir = Random.onUnitSphere;
            dir.y = Mathf.Abs(dir.y);
            v = dir.normalized * scatterForce + Vector3.up * upwardForce;

            if (!hasArena || ArenaController.ContainsXZ(arena, ArenaController.PredictLanding(from, v, gravity, bounciness, minBounce, friction, maxBounces), arenaMargin))
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

        GiveCloneTheWeapon(cloneFps);
        GiveCloneTheAimPoint(cloneFps);

        clone.Activate(cloneLifetime, dissolveTime, destroyVFX, pool, PickScatterVelocity(pos, CloneBodyController()), cloneHealth, OnCloneKilled);
        clone.gameObject.SetActive(true);

        if (spawnVFX)
            Instantiate(spawnVFX, pos, Quaternion.identity);

        activeClones.Add(clone);
        if (ArenaController.Current != null && ctx.fps != null)
            ArenaController.Current.RegisterClone(clone, ctx.fps.Team); // removed with the arena
    }

    // =========================
    // REVEAL THE KILLER
    // =========================
    private void OnCloneKilled(PawnCloneHealth clone, DamageInfo info)
    {
        if (fps == null || info.source == null)
            return;

        ChessPieceFPSController killer = info.source.GetComponentInParent<ChessPieceFPSController>();
        if (killer == null || killer == fps || killer.IsPawnClone || killer.IsDead || SkillTargeting.IsAlly(fps, killer))
            return; // (a dead killer, e.g. a dead queen's grenade or a shot still flying, is not revealed)

        RevealSystem.Reveal(killer, fps.Team, revealSeconds);
    }

    // =========================
    // CLONE WEAPON
    // =========================
    /// <summary>
    /// The clone holds the pawn's own weapon, a real one in its prefab's WeaponHolder (bound like an AI
    /// piece's, ready for the AI). Kept while the pooled clone is reused; swapped if the pawn's weapon changed.
    /// </summary>
    private void GiveCloneTheWeapon(ChessPieceFPSController cloneFps)
    {
        GameObject weapon = fps != null ? fps.BoundWeaponPrefab : null;
        if (cloneFps == null || weapon == null || cloneFps.BoundWeaponPrefab == weapon)
            return;

        WeaponController old = cloneFps.GetCurrentWeapon();
        if (old != null)
            Destroy(old.gameObject);
        cloneFps.BindWeapon(weapon, null, null, null, null, null);
    }

    private const string AimPointName = "AimPoint";

    /// <summary>
    /// The clone gets an empty AimPoint (no camera) right where the pawn's camera point is, looking level, and the
    /// pawn's weapon holder offset: its weapon sits in the hands like the pawn's and works like a real piece's.
    /// </summary>
    private void GiveCloneTheAimPoint(ChessPieceFPSController cloneFps)
    {
        if (cloneFps == null || fps == null || fps.cameraPoint == null)
            return;

        Transform aim = cloneFps.transform.Find(AimPointName);
        if (aim == null)
        {
            aim = new GameObject(AimPointName).transform;
            aim.SetParent(cloneFps.transform, false);
        }
        aim.localPosition = transform.InverseTransformPoint(fps.cameraPoint.position);
        aim.localRotation = Quaternion.identity;
        cloneFps.UseAimPoint(aim, fps.WeaponHolderOffset);
    }

    private void ClearExistingClones(bool shatter = false)
    {
        foreach (var clone in activeClones)
        {
            if (clone == null) continue;

            if (IsTearingDown)
                Destroy(clone.gameObject); // the pool lives under this dying pawn: never re-parent into it
            else if (clone.IsDying)
                continue;                  // already dissolving: it returns to the pool by itself
            else if (shatter)
                clone.Vanish();            // breaks apart, then returns to the pool by itself
            else if (pool != null)
                clone.ForceDespawn();
        }
        activeClones.Clear();
    }
}
