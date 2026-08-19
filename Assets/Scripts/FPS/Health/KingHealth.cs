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
    public event Action OnLastStandStarted;

    // =========================
    // DIRECTIONAL SHIELD
    // =========================

    [Header("Directional Shield")]
    [SerializeField] private float maxShieldIntegrity = 150f;
    [SerializeField] private float shieldIntegrity;

    [Tooltip("Damage multiplier when shield blocks (0.3 = take 30% damage)")]
    [SerializeField] private float shieldDamageMultiplier = 0.3f;

    [SerializeField] private float shieldBlockAngle = 90f;

    public bool ShieldActive { get; private set; }

    public event Action<float> OnShieldDamaged;

    // =========================
    // UNITY
    // =========================

    void Start()
    {
        ResetKing();
    }

    // =========================
    // DAMAGE PIPELINE
    // =========================

    protected override float ModifyIncomingDamage(DamageInfo info)
    {
        float damage = info.amount;

        // -------------------------
        // 1. SHIELD (MULTIPLIER)
        // -------------------------

        if (ShieldActive && shieldIntegrity > 0f)
        {
            float angle = Vector3.Angle(
                transform.forward,
                -info.hitDirection.normalized
            );

            if (angle <= shieldBlockAngle * 0.5f)
            {
                float reduced = damage * shieldDamageMultiplier;
                float absorbed = damage - reduced;

                shieldIntegrity -= absorbed;
                OnShieldDamaged?.Invoke(absorbed);

                if (shieldIntegrity <= 0f)
                {
                    shieldIntegrity = 0f;
                    ShieldActive = false;
                }

                damage = reduced;
            }
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

    public void EndLastStand(bool survived)
    {
        if (!lastStandActive)
            return;

        lastStandActive = false;

        if (survived)
        {
            currentStacks = 1;
            ResetHealth(healthPerStack * 0.5f);
        }
        // else: chết bằng pipeline base
    }

    // =========================
    // SHIELD CONTROL
    // =========================

    public void ActivateShield()
    {
        if (shieldIntegrity <= 0f) return;
        ShieldActive = true;
    }

    public void DeactivateShield()
    {
        ShieldActive = false;
    }

    public void ResetShield()
    {
        shieldIntegrity = maxShieldIntegrity;
        ShieldActive = false;
    }

    // =========================
    // RESET
    // =========================

    public void ResetKing()
    {
        lastStandActive = false;
        currentStacks = 0; // stack chỉ tồn tại trong Last Stand
        ResetShield();
        // KHÔNG reset health ở đây → dùng base HP như quân khác
    }
}
