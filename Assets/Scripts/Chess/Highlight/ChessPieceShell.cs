using UnityEngine;

public sealed class ChessPieceShell : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GameObject shell;
    [SerializeField] private MeshRenderer shellRenderer;

    [Header("Hover Style")]
    [SerializeField] private Color hoverColor = Color.cyan;
    [Range(0f, 1f)]
    [SerializeField] private float hoverAlpha = 0.45f;

    private MaterialPropertyBlock mpb;

    private static readonly int ColorId =
        Shader.PropertyToID("_Color");

    private bool isInitialized;

    // ======================================================
    // UNITY
    // ======================================================

    private void Awake()
    {
        if (shell == null || shellRenderer == null)
        {
            Debug.LogError(
                "ChessPieceShell: Missing references.",
                this);
            enabled = false;
            return;
        }

        mpb = new MaterialPropertyBlock();
        ForceHide();

        isInitialized = true;
    }

    private void OnDisable()
    {
        // Absolute invariant: disabled => hidden & clean
        ForceHide();
    }

    // ======================================================
    // PUBLIC VISUAL API
    // ======================================================

    public void ShowHover()
    {
        if (!enabled || !isInitialized)
            return;

        ApplyVisual(hoverColor, hoverAlpha);
        shell.SetActive(true);
    }

    public void Show(Color color, float alpha)
    {
        if (!enabled || !isInitialized)
            return;

        ApplyVisual(color, alpha);
        shell.SetActive(true);
    }

    public void Hide()
    {
        if (!enabled || !isInitialized)
            return;

        ForceHide();
    }

    // ======================================================
    // INTERNAL
    // ======================================================

    private void ForceHide()
    {
        if (shell != null)
            shell.SetActive(false);

        if (shellRenderer != null)
        {
            // Reset ALL overrides safely
            shellRenderer.SetPropertyBlock(null);
        }
    }

    private void ApplyVisual(Color color, float alpha)
    {
        if (shellRenderer == null)
            return;

        Color c = color;
        c.a = Mathf.Clamp01(alpha);

        shellRenderer.GetPropertyBlock(mpb);
        mpb.SetColor(ColorId, c);
        shellRenderer.SetPropertyBlock(mpb);
    }
}
