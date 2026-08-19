using UnityEngine;
using System.Collections;

// =========================
// RED ZONE (Overlay-only, no cage)
// =========================
public class RedZone : Zone
{
    private Coroutine blinkRoutine;

    // =========================
    // PUBLIC API
    // =========================
    public override void Activate(Vector3 pos, float radius, float duration)
    {
        base.Activate(pos, radius, duration);

        // đảm bảo alpha đúng ngay frame đầu
        ApplyOverlayAlpha(cageColor.a);

        blinkRoutine = StartCoroutine(BlinkRoutine(duration));
    }

    public override void Deactivate()
    {
        base.Deactivate();
        StopBlink();
    }

    // =========================
    // BLINK (AUTHORITATIVE)
    // =========================
    private IEnumerator BlinkRoutine(float duration)
    {
        const float startPeriod = 0.7f;
        const float endPeriod = 0.1f;
        const float minAlpha = 0.12f;

        float elapsed = 0f;
        float phase = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float lifeT = Mathf.Clamp01(elapsed / duration);

            float period = Mathf.Lerp(startPeriod, endPeriod, lifeT);

            phase += Time.deltaTime / period;
            float t = Mathf.PingPong(phase, 1f);
            t = t * t * (3f - 2f * t);

            float alpha = Mathf.Lerp(minAlpha, cageColor.a, t);

            ApplyOverlayAlpha(alpha);

            yield return null;
        }

        ApplyOverlayAlpha(cageColor.a);
    }

    private void StopBlink()
    {
        if (blinkRoutine != null)
            StopCoroutine(blinkRoutine);

        blinkRoutine = null;
    }

    // =========================
    // CORE FIX: trực tiếp gọi overlay system
    // =========================
    private void ApplyOverlayAlpha(float a)
    {
        if (cageId < 0)
            return;

        Color c = cageColor;
        c.a = a;

        ZoneOverlaySystem.UpdateColor(cageId, c);
    }

    // =========================
    // OVERRIDES
    // =========================

    protected override void UpdatePulse()
    {
    }

    protected override void LayoutQuads(float radius)
    {
    }
    protected override void BuildIfNeeded()
    {
    }
    protected override void StopAllInternalCoroutines()
    {
        base.StopAllInternalCoroutines();
        StopBlink();
    }
}