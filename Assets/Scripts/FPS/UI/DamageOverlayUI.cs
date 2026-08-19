using UnityEngine;
using UnityEngine.UI;

public class DamageOverlayUI : MonoBehaviour
{
    [Header("Overlay Settings")]
    public RawImage overlayImage;
    public float maxAlpha = 0.5f;   // intensity of flash
    public float fadeSpeed = 2f;    // how quickly it fades back to transparent

    private float currentAlpha = 0f;

    private void Update()
    {
        if (overlayImage == null) return;

        // Gradually fade out
        if (currentAlpha > 0f)
        {
            currentAlpha -= Time.deltaTime * fadeSpeed;
            currentAlpha = Mathf.Clamp01(currentAlpha);
            overlayImage.color = new Color(1f, 0f, 0f, currentAlpha);
        }
    }

    public void Flash(float intensity)
    {
        float alpha = maxAlpha * intensity;
        currentAlpha = intensity ; // instantly flash to visible
        overlayImage.color = Color.Lerp(new Color(1f, 0f, 0f, 0f), new Color(1f, 0f, 0f, intensity), Mathf.Sin(currentAlpha * Mathf.PI));
    }
}
