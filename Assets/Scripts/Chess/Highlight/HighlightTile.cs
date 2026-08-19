using UnityEngine;

[RequireComponent(typeof(Renderer))]
public sealed class HighlightTile : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Renderer rend;

    // ======================================================
    // STATE (CRITICAL FOR POOLING)
    // ======================================================

    public HighlightType Type { get; private set; }

    private Material defaultMaterial;

    // ======================================================
    // UNITY
    // ======================================================

    private void Awake()
    {
        if (rend == null)
            rend = GetComponent<Renderer>();

        defaultMaterial = rend.sharedMaterial;

        // Pool invariant: start clean & inactive
        ResetState();
    }

    private void OnDisable()
    {
        // Safety: any unexpected disable must restore invariant
        ResetState();
    }

    // ======================================================
    // POOL-SAFE API (AUTHORITATIVE)
    // ======================================================

    /// <summary>
    /// Activate tile at world position with given material and highlight type.
    /// MUST be called only by BoardHighlightSystem.
    /// </summary>
    public void Activate(
        Vector3 worldPos,
        Material mat,
        HighlightType type)
    {
        // Transform invariant
        transform.position = worldPos;

        if (rend != null)
        {
            rend.sharedMaterial = mat != null ? mat : defaultMaterial;
            rend.enabled = true;
        }

        // Authoritative type
        Type = type;
        gameObject.SetActive(true);

    }

    /// <summary>
    /// Deactivate tile and restore pool invariant.
    /// Safe to call multiple times.
    /// </summary>
    public void Deactivate()
    {
        Type = default;

        if (rend != null)
            rend.enabled = false;

        gameObject.SetActive(false);
    }

    // ======================================================
    // INTERNAL
    // ======================================================

    private void ResetState()
    {
        // Reset visual
        if (rend != null)
            rend.sharedMaterial = defaultMaterial;

        // Reset logical state
        Type = default;

    }
}
