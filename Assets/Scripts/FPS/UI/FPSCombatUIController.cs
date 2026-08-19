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
            damageIndicator.ShowHit(info.hitDirection, finalDamage);
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
            healthBar.value = currentHealth.CurrentHealth / currentHealth.MaxHealth;

        if (healthText)
            healthText.text =
                $"{Mathf.CeilToInt(currentHealth.CurrentHealth)}/{Mathf.CeilToInt(currentHealth.MaxHealth)}";
    }
}