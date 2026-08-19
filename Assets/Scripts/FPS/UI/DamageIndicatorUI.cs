using UnityEngine;
using UnityEngine.UI;

public class DamageIndicatorUI : MonoBehaviour
{
    [Header("Indicator Settings")]
    public RawImage indicatorImage;
    public float fadeDuration = 1.5f;   // Base fade duration
    public float maxAlpha = 0.8f;
    public float rotationOffset = 0f;

    [Header("Damage Scaling")]
    public float minDamage = 10f;       // Smallest hit expected
    public float maxDamage = 100f;      // Strongest hit expected
    public float minFadeMultiplier = 0.5f;  // Weak hits fade out faster
    public float maxFadeMultiplier = 1.5f;  // Strong hits linger longer

    private Transform playerTransform;
    private Camera playerCamera;
    private float fadeTimer = 0f;
    private float actualFadeDuration;
    private Vector3 hitDirection;
    private bool active = false;

    public void Initialize(Transform player, Camera cam)
    {
        playerTransform = player;
        playerCamera = cam;
        indicatorImage.color = new Color(1, 0, 0, 0);
    }

    // Updated to include damageAmount
    public void ShowHitFrom(Vector3 attackerPosition, float damageAmount)
    {
        if (playerTransform == null || playerCamera == null) return;

        Vector3 direction = attackerPosition - playerTransform.position;
        direction.y = 0f;
        hitDirection = direction.normalized;

        // Normalize damage to 0–1 range
        float damageNormalized = Mathf.InverseLerp(minDamage, maxDamage, damageAmount);
        float fadeScale = Mathf.Lerp(minFadeMultiplier, maxFadeMultiplier, damageNormalized);

        actualFadeDuration = fadeDuration * fadeScale;
        fadeTimer = actualFadeDuration;
        active = true;

        float size = Mathf.Lerp(1.35f, 1.95f, damageNormalized);
        indicatorImage.rectTransform.localScale = Vector3.one * size;

        Color color = Color.Lerp(Color.yellow, Color.red, damageNormalized);
        indicatorImage.color = new Color(color.r, color.g, color.b, maxAlpha);
    }

    void Update()
    {
        if (!active) return;

        fadeTimer -= Time.deltaTime;

        float alpha = Mathf.Clamp01(fadeTimer / actualFadeDuration) * maxAlpha;
        indicatorImage.color = new Color(255, 0, 0, alpha);

        if (playerTransform && playerCamera)
        {
            Vector3 forward = playerCamera.transform.forward;
            float angle = Vector3.SignedAngle(forward, hitDirection, Vector3.up);
            indicatorImage.rectTransform.rotation = Quaternion.Euler(0, 0, -(angle + rotationOffset));
        }

        if (fadeTimer <= 0f)
        {
            active = false;
            gameObject.SetActive(false); // return to pool
        }
    }
}
