using UnityEngine;
using System;

public class KingHealth : ChessPieceHealth
{
    // =========================
    // STACK HEALTH (LAST STAND ONLY)
    // =========================

    [Header("King Last Stand Stacks")]
    [SerializeField] private int maxStacks = 3;
    [SerializeField] private float healthPerStack = 100f;

    private int currentStacks;
    private bool lastStandActive;

    public int CurrentStacks => currentStacks;
    public bool IsLastStand => lastStandActive;

    public event Action<int> OnStackLost;
    public event Action<int> OnStackRestored;
    public event Action OnLastStandStarted;

    // =========================
    // DIRECTIONAL SHIELD
    // =========================
    // An unbreakable shield the king raises at will (held right mouse button, KingFPSController).
    // Raised, it covers the 90 degrees in front of the king:
    //   - bullets and weapon blows (sword...) are stopped completely;
    //   - explosions and skill impacts get through reduced.
    // Hits from the sides or behind are not covered.

    [Header("Directional Shield")]
    [SerializeField] private float shieldBlockAngle = 90f;

    [Tooltip("Share of explosion / skill-impact damage still taken through the raised shield (0.3 = takes 30%, i.e. 70% less).")]
    [Range(0f, 1f)]
    [SerializeField] private float shieldReducedDamageTaken = 0.3f;

    public bool ShieldActive { get; private set; }

    // =========================
    // UNITY
    // =========================

    // Awake (not Start): the arena may call StartLastStand right after Instantiate,
    // and a later Start() would wipe that Last Stand.
    protected override void Awake()
    {
        base.Awake();
        ResetKing();
    }

    // =========================
    // DAMAGE PIPELINE
    // =========================

    protected override float ModifyIncomingDamage(DamageInfo info)
    {
        float damage = info.amount;

        // -------------------------
        // 1. SHIELD (raised, front 90 degrees)
        // -------------------------

        if (ShieldActive && IsInFrontOfTheShield(info.hitDirection))
        {
            if (info.type == DamageType.Bullet || IsWeaponBlow(info))
                return 0f;                         // stopped by the shield
            damage *= shieldReducedDamageTaken;    // explosion / skill impact: reduced
        }

        if (damage <= 0f)
            return 0f;

        // -------------------------
        // 2. LAST STAND STACK LOGIC
        // -------------------------

        if (!lastStandActive)
        {
            // FPS bình thường: KHÔNG dùng stack
            return damage;
        }

        // Đang Last Stand
        if (damage >= CurrentHealth)
        {
            if (currentStacks > 1)
            {
                ConsumeStack();
                NotifyAbsorbedHit(info, damage); // hit feedback even though a stack took it
                return 0f;
            }

            // Stack cuối cùng → cho phép chết
            return damage;
        }

        return damage;
    }

    // =========================
    // STACK LOGIC
    // =========================

    private void ConsumeStack()
    {
        currentStacks--;

        ResetHealth(healthPerStack);
        DisableRegen();

        OnStackLost?.Invoke(currentStacks);
    }

    // =========================
    // LAST STAND CONTROL
    // =========================

    public void StartLastStand()
    {
        if (lastStandActive)
            return;

        lastStandActive = true;
        currentStacks = maxStacks;

        ResetHealth(healthPerStack);
        DisableRegen();

        OnLastStandStarted?.Invoke();
    }

    /// <summary>
    /// Last Stand reward: the king defeated one of the pieces that mated it and gets
    /// back one lost stack (never above maxStacks). Health inside the current stack is
    /// kept, so the regained stack is a full extra layer. Health and stacks otherwise
    /// carry over between opponents: the whole Last Stand is ONE match on ONE map.
    /// Call it from the arena each time a Last Stand opponent dies.
    /// </summary>
    public void RestoreStack()
    {
        if (!lastStandActive || !IsAlive)
            return;

        if (currentStacks >= maxStacks)
            return;

        currentStacks++;
        OnStackRestored?.Invoke(currentStacks);
    }

    public void EndLastStand(bool survived)
    {
        if (!lastStandActive)
            return;

        lastStandActive = false;

        if (survived)
        {
            // Back to the normal health pool (not a 50-HP max forever), at half health.
            currentStacks = 0;
            RestoreBaseMaxHealth(0.5f);
        }
        // else: chết bằng pipeline base
    }

    // =========================
    // SHIELD CONTROL
    // =========================

    /// <summary>Raised while the king holds it up (right mouse button); lowered otherwise.</summary>
    public void SetShieldRaised(bool raised) => ShieldActive = raised;

    /// <summary>The hit travels against the king's facing, within the shield's angle.</summary>
    private bool IsInFrontOfTheShield(Vector3 hitDirection)
    {
        Vector3 flat = new Vector3(hitDirection.x, 0f, hitDirection.z);
        if (flat.sqrMagnitude < 1e-6f)
            return false; // straight from above / below: not covered
        Vector3 forward = new Vector3(transform.forward.x, 0f, transform.forward.z);
        return Vector3.Angle(forward, -flat) <= shieldBlockAngle * 0.5f;
    }

    /// <summary>A blow from a weapon (sword, knife...), as opposed to a skill impact.</summary>
    private static bool IsWeaponBlow(DamageInfo info) =>
        info.type == DamageType.Melee && info.source != null && info.source.GetComponentInParent<WeaponController>() != null;

    // =========================
    // RESET
    // =========================

    public void ResetKing()
    {
        lastStandActive = false;
        currentStacks = 0; // stack chỉ tồn tại trong Last Stand
        ShieldActive = false;
        // KHÔNG reset health ở đây → dùng base HP như quân khác
    }
}
