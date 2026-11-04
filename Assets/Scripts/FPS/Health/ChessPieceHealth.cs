using UnityEngine;
using System;

public class ChessPieceHealth : MonoBehaviour, IDamageable
{
    [Header("Damage Feedback")]
    [SerializeField] private GameObject damageEffect;
    [SerializeField] private AudioSource hitSound;
    [SerializeField] private AudioSource deathSound;

    [Header("Base Health")]
    [SerializeField] private float maxHealth = 100f;

    private float currentHealth;
    private bool isDead;
    private float baseMaxHealth;
    private IDamageModifier[] damageModifiers = System.Array.Empty<IDamageModifier>();

    public float MaxHealth => maxHealth;
    public float CurrentHealth => currentHealth;
    public bool IsAlive => !isDead && currentHealth > 0f;

    public event Action OnDeath;
    public event Action<DamageInfo, float> OnDamaged; // actual damage applied

    // =========================
    // REGEN SYSTEM
    // =========================

    [Serializable]
    public class RegenProfile
    {
        public float regenPerSecond = 5f;
        public float regenDelayAfterHit = 10f;
        public AnimationCurve regenCurve = AnimationCurve.Linear(0, 1, 1, 1);
    }

    [Header("Regen Health")]
    [SerializeField] private RegenProfile activeRegen;
    private bool regenEnabled;
    private float lastDamageTime;
    private float regenTime;
    private float regenMultiplier = 1f;

    // =========================
    // UNITY
    // =========================

    protected virtual void Awake()
    {
        baseMaxHealth = maxHealth;
        currentHealth = maxHealth;
        damageModifiers = GetComponents<IDamageModifier>(); // cached: no allocation per hit
        isDead = false;
    }

    /// <summary>Max health from the prefab (ResetHealth may change maxHealth temporarily).</summary>
    public float BaseMaxHealth => baseMaxHealth;

    void Update()
    {
        if (!regenEnabled || activeRegen == null || !IsAlive)
            return;

        if (Time.time - lastDamageTime < activeRegen.regenDelayAfterHit)
            return;

        regenTime += Time.deltaTime;

        float curveFactor = activeRegen.regenCurve.Evaluate(regenTime);
        float regenAmount =
            activeRegen.regenPerSecond *
            curveFactor *
            regenMultiplier *
            Time.deltaTime;

        HealInternal(regenAmount);
    }

    public void ForceKill()
    {
        TryDie();
    }    

    // =========================
    // DAMAGE INTERFACE (FPS SHARED)
    // =========================

    public void TakeDamage(DamageInfo info)
    {
        if (!IsAlive) return;
        if (info.amount <= 0f) return;

        // Damage modifiers on this piece (e.g. the Knight takes less damage while leaping).
        for (int i = 0; i < damageModifiers.Length; i++)
            if (damageModifiers[i] != null)
                damageModifiers[i].ModifyDamage(ref info);
        if (info.amount <= 0f) return;

        float finalDamage = ModifyIncomingDamage(info);
        if (finalDamage <= 0f) return;

        CommitDamage(finalDamage);
        OnDamaged?.Invoke(info, finalDamage);

        if (hitSound != null)
            hitSound.Play();
    }

    protected virtual float ModifyIncomingDamage(DamageInfo info)
    {
        return info.amount;
    }

    /// <summary>
    /// A hit fully absorbed by a mechanic (e.g. a King stack) still gives hit feedback
    /// (UI flash, hit sound) without changing health.
    /// </summary>
    protected void NotifyAbsorbedHit(DamageInfo info, float absorbedAmount)
    {
        lastDamageTime = Time.time;
        regenTime = 0f;
        OnDamaged?.Invoke(info, absorbedAmount);
        if (hitSound != null)
            hitSound.Play();
    }

    /// <summary>Back to the prefab's max health, with current health at the given fraction of it.</summary>
    public void RestoreBaseMaxHealth(float currentFraction)
    {
        maxHealth = baseMaxHealth;
        currentHealth = Mathf.Clamp(baseMaxHealth * currentFraction, 1f, baseMaxHealth);
        isDead = false;
    }

    private void CommitDamage(float damage)
    {
        currentHealth -= damage;
        lastDamageTime = Time.time;
        regenTime = 0f;

        if (currentHealth <= 0f)
        {
            currentHealth = 0f;
            TryDie();
        }
    }

    private void TryDie()
    {
        if (isDead) return;

        isDead = true;
        regenEnabled = false;
        OnDeath?.Invoke();

        if (deathSound != null)
            deathSound.Play();
    }

    // =========================
    // HEAL / RESET
    // =========================

    private void HealInternal(float amount)
    {
        if (!IsAlive) return;
        currentHealth = Mathf.Min(currentHealth + amount, maxHealth);
    }

    public void ResetHealth(float newMaxHealth)
    {
        maxHealth = newMaxHealth;
        currentHealth = maxHealth;
        regenTime = 0f;
        regenEnabled = false;
        regenMultiplier = 1f;
        isDead = false;
    }

    // =========================
    // REGEN CONTROL (CHESS SIDE)
    // =========================

    public void EnableRegen(RegenProfile profile)
    {
        if (profile == null) return;

        activeRegen = profile;
        regenEnabled = true;
        regenTime = 0f;
    }

    public void DisableRegen()
    {
        regenEnabled = false;
    }

    public void SetRegenMultiplier(float multiplier)
    {
        regenMultiplier = Mathf.Max(0f, multiplier);
    }
}
