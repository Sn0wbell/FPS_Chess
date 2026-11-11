using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A shot's tracer: a short glowing streak (length, the same from any point of view) that runs along the bullet's way
/// at its own, slower speed so the eye can follow it (bullets are far too fast to be seen). The hit itself is the real
/// bullet's, at once: only the streak is slower. Once the bullet has hit (or ended), the streak still runs on to that
/// point - never more than maxLag seconds after - then stays a moment there (fadeSeconds), fading out.
/// Its own pooled object: it outlives the bullet that went back to its pool. Never ahead of the bullet, never behind
/// the muzzle; never thinner than minPixels on screen; thin and faint at its back end.
/// </summary>
public sealed class ShotTracer : MonoBehaviour
{
    private const float TailWidthShare = 0.25f;
    private const float TailAlphaShare = 0.35f;

    private static readonly Stack<ShotTracer> free = new();
    private static Transform root;

    private LineRenderer line;
    private Vector3 muzzle;
    private Vector3 direction;     // the way it runs
    private float travelled;       // how far its head is from the muzzle
    private float bulletTravelled; // how far the bullet is (the streak never gets ahead of it)
    private float length;
    private float speed;
    private float maxLag;
    private float width;
    private float minPixels;
    private float fadeSeconds;

    private bool bulletEnded;      // the bullet hit / ended: the streak runs on to that point
    private float endSpeed;        // ...at least this fast, to get there within maxLag
    private bool arrived;          // the streak is there: it fades out
    private float fadeStart;

    /// <summary>The bullet hit / ended: the streak runs on to that point, then fades.</summary>
    public bool IsEnded => bulletEnded;
    /// <summary>The streak has reached where the bullet hit / ended (it fades out now).</summary>
    public bool HasArrived => arrived;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        free.Clear();
        root = null;
    }

    public static ShotTracer Begin(Vector3 from, Vector3 direction, Material material, float length, float speed, float maxLag,
        float width, float minPixels, float fadeSeconds)
    {
        ShotTracer t = null;
        while (free.Count > 0 && t == null)
            t = free.Pop(); // skip any destroyed meanwhile
        if (t == null)
            t = Create();

        t.muzzle = from;
        t.direction = direction.sqrMagnitude > 1e-8f ? direction.normalized : Vector3.forward;
        t.travelled = 0f;
        t.bulletTravelled = 0f;
        t.length = Mathf.Max(0f, length);
        t.speed = Mathf.Max(0.01f, speed);
        t.maxLag = Mathf.Max(0f, maxLag);
        t.width = width;
        t.minPixels = minPixels;
        t.fadeSeconds = Mathf.Max(0f, fadeSeconds);
        t.bulletEnded = false;
        t.arrived = false;
        t.line.sharedMaterial = material;
        t.Draw();
        t.SetAlpha(1f);
        t.gameObject.SetActive(true);
        return t;
    }

    private static ShotTracer Create()
    {
        if (root == null)
            root = new GameObject("[ShotTracers]").transform;
        var go = new GameObject("ShotTracer");
        go.transform.SetParent(root, false);
        var t = go.AddComponent<ShotTracer>();
        t.line = go.AddComponent<LineRenderer>();
        t.line.useWorldSpace = true;
        t.line.positionCount = 2;
        t.line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        t.line.receiveShadows = false;
        return t;
    }

    /// <summary>The bullet is here now (the streak never gets ahead of it).</summary>
    public void Follow(Vector3 bullet)
    {
        if (bulletEnded) return;
        bulletTravelled = Mathf.Max(bulletTravelled, Vector3.Dot(bullet - muzzle, direction));
    }

    /// <summary>The bullet hit (or ended) here: the streak runs on to it (within maxLag), then fades out there.</summary>
    public void End(Vector3 point)
    {
        if (bulletEnded) return;
        bulletEnded = true;

        // From now on it runs straight to that point (a bullet that dropped a little: aimed at where it really hit).
        Vector3 head = muzzle + direction * travelled;
        float remaining = Vector3.Distance(head, point);
        Vector3 toPoint = point - muzzle;
        if (toPoint.sqrMagnitude > 1e-8f)
        {
            direction = toPoint.normalized;
            travelled = Mathf.Max(0f, Vector3.Dot(head - muzzle, direction));
        }
        bulletTravelled = toPoint.magnitude;
        endSpeed = maxLag > 0f ? Mathf.Max(speed, remaining / maxLag) : float.PositiveInfinity;
        if (travelled >= bulletTravelled - 1e-4f)
            Arrive();
    }

    private void Update()
    {
        if (!arrived)
        {
            float v = bulletEnded ? endSpeed : speed;
            travelled = Mathf.Min(travelled + v * Time.deltaTime, bulletTravelled);
            if (bulletEnded && travelled >= bulletTravelled - 1e-4f)
                Arrive();
        }

        if (!gameObject.activeSelf)
            return; // released on arriving (no fade)

        Draw();

        if (!arrived)
            return;
        float t = fadeSeconds > 0f ? (Time.time - fadeStart) / fadeSeconds : 1f;
        if (t >= 1f)
        {
            Release();
            return;
        }
        SetAlpha(1f - t);
    }

    private void Arrive()
    {
        travelled = bulletTravelled;
        arrived = true;
        fadeStart = Time.time;
        Draw();
        if (fadeSeconds <= 0f)
            Release();
    }

    private void Release()
    {
        gameObject.SetActive(false);
        free.Push(this);
    }

    /// <summary>The streak: its head where it has run to, its tail length behind (not behind the muzzle).</summary>
    private void Draw()
    {
        Vector3 head = muzzle + direction * travelled;
        Vector3 tail = muzzle + direction * Mathf.Max(0f, travelled - length);
        line.SetPosition(0, tail);
        line.SetPosition(1, head);
        line.startWidth = Mathf.Max(width * TailWidthShare, WorldWidthOfPixels(tail, minPixels * 0.5f));
        line.endWidth = Mathf.Max(width, WorldWidthOfPixels(head, minPixels));
    }

    /// <summary>Fading through the vertex colours (the material is shared by every shot of that colour).</summary>
    private void SetAlpha(float a)
    {
        line.startColor = new Color(1f, 1f, 1f, a * TailAlphaShare); // the back end (faint)
        line.endColor = new Color(1f, 1f, 1f, a);                    // the front
    }

    /// <summary>How wide <paramref name="pixels"/> screen pixels are at <paramref name="at"/> (0 without a camera).</summary>
    private static float WorldWidthOfPixels(Vector3 at, float pixels)
    {
        Camera cam = Camera.main;
        if (cam == null || pixels <= 0f || Screen.height <= 0)
            return 0f;
        float distance = Vector3.Distance(cam.transform.position, at);
        return pixels * 2f * distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Screen.height;
    }
}
