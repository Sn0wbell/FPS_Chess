using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class FPSCombatUIController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ArenaController arena;

    [Header("Health UI")]
    [SerializeField] private Slider healthBar;
    [SerializeField] private TextMeshProUGUI healthText;

    [Header("Damage Indicator")]
    [SerializeField] private DamageIndicatorPoolManager damageIndicator;

    [Header("Damage Overlay")]
    [SerializeField] private DamageOverlayUI damageOverlay;
    [SerializeField] private float overlayMaxIntensity = 1f;

    // =========================
    // RUNTIME
    // =========================

    private ChessPieceFPSController currentPlayer;
    private ChessPieceHealth currentHealth;
    private Camera mainCam;

    // =========================
    // UNITY
    // =========================

    void Awake()
    {
        mainCam = Camera.main;
    }

    void Start()
    {
        BindFromArena();
    }

    void Update()
    {
        if (arena && arena.playerPiece != currentPlayer)
            BindFromArena();
    }

    void OnDestroy()
    {
        Unbind();
    }

    // =========================
    // BINDING
    // =========================

    private void BindFromArena()
    {
        Unbind();

        if (!arena || !arena.playerPiece)
            return;

        currentPlayer = arena.playerPiece;
        currentHealth = currentPlayer.GetComponent<ChessPieceHealth>();

        if (!currentHealth)
        {
            Debug.LogWarning("[FPSCombatUI] Player piece has no ChessPieceHealth");
            return;
        }

        currentHealth.OnDamaged += HandleDamaged;
        currentHealth.OnDeath += HandleDeath;
        currentHealth.OnHealthChanged += UpdateHealthUI; // heals, resets, regen, death

        if (damageIndicator && mainCam)
            damageIndicator.Initialize(currentPlayer.transform, mainCam);

        UpdateHealthUI();
    }
    public void Rebind()
    {
        BindFromArena();
    }
    private void Unbind()
    {
        if (currentHealth != null)
        {
            currentHealth.OnDamaged -= HandleDamaged;
            currentHealth.OnDeath -= HandleDeath;
            currentHealth.OnHealthChanged -= UpdateHealthUI;
        }

        currentPlayer = null;
        currentHealth = null;
    }

    // =========================
    // EVENT HANDLERS
    // =========================

    private void HandleDamaged(DamageInfo info, float finalDamage)
    {
        if (!currentHealth)
            return;

        // -------- Direction Indicator (PLAYER ONLY)
        if (damageIndicator != null)
        {
            // Where the hit came from: hitDirection is the way it travelled (shot / blast -> the player), so its source
            // lies behind it; without a direction, where the source object is.
            Vector3 from;
            if (info.hitDirection.sqrMagnitude > 1e-6f && currentPlayer != null)
                from = currentPlayer.transform.position - info.hitDirection.normalized;
            else if (info.source != null)
                from = info.source.transform.position;
            else
                from = info.hitPoint;
            damageIndicator.ShowHit(from, finalDamage);
        }

        // -------- Overlay (Option B – intensity)
        if (damageOverlay != null)
        {
            float intensity =
                Mathf.Clamp01(finalDamage / currentHealth.MaxHealth) *
                overlayMaxIntensity;

            damageOverlay.Flash(intensity);
        }

        UpdateHealthUI();
    }

    private void HandleDeath()
    {
        UpdateHealthUI();
    }

    // =========================
    // UI UPDATE
    // =========================

    private void UpdateHealthUI()
    {
        if (!currentHealth)
            return;

        if (healthBar)
            healthBar.value = currentHealth.MaxHealth > 0f ? currentHealth.CurrentHealth / currentHealth.MaxHealth : 0f;

        if (healthText)
            healthText.text =
                $"{Mathf.CeilToInt(currentHealth.CurrentHealth)}/{Mathf.CeilToInt(currentHealth.MaxHealth)}";
    }
}