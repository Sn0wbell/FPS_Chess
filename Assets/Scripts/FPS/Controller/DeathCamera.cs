using UnityEngine;

/// <summary>
/// Death cam (like CS2): when the player's piece dies, the camera leaves its head, glides back and up
/// and keeps looking at the spot where the piece broke apart. It stays there (no mouse look) until the
/// camera is bound to a piece again (ArenaController removes it).
/// </summary>
public class DeathCamera : MonoBehaviour
{
    private Vector3 from;
    private Vector3 to;
    private Vector3 focus;
    private float duration;
    private float elapsed;

    public Vector3 Focus => focus;

    /// <summary>Starts the death cam on this camera, looking at <paramref name="deathPoint"/> (the body's centre).</summary>
    public static DeathCamera Begin(Camera cam, Vector3 deathPoint, Vector3 lookDirection, float back, float up, float moveTime)
    {
        if (cam == null)
            return null;

        Transform t = cam.transform;
        t.SetParent(null, true);

        Vector3 flat = new Vector3(lookDirection.x, 0f, lookDirection.z);
        flat = flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward;

        // Behind and above the body; a wall or the ground in between: stop just in front of it.
        Vector3 offset = -flat * back + Vector3.up * up;
        float distance = offset.magnitude;
        Vector3 target = deathPoint + offset;
        if (distance > 1e-3f &&
            Physics.SphereCast(deathPoint, 0.2f, offset / distance, out RaycastHit hit, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            target = deathPoint + offset / distance * Mathf.Max(0.5f, hit.distance - 0.2f);
        }

        DeathCamera dc = cam.GetComponent<DeathCamera>();
        if (dc == null)
            dc = cam.gameObject.AddComponent<DeathCamera>();
        dc.from = t.position;
        dc.to = target;
        dc.focus = deathPoint;
        dc.duration = Mathf.Max(0.01f, moveTime);
        dc.elapsed = 0f;
        dc.enabled = true;
        dc.Apply();
        return dc;
    }

    /// <summary>Ends the death cam (the camera is bound to a piece again).</summary>
    public static void End(Camera cam)
    {
        if (cam == null)
            return;
        DeathCamera dc = cam.GetComponent<DeathCamera>();
        if (dc != null)
            Destroy(dc);
    }

    private void LateUpdate()
    {
        if (elapsed >= duration)
            return;
        elapsed += Time.deltaTime;
        Apply();
    }

    private void Apply()
    {
        float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
        transform.position = Vector3.Lerp(from, to, k);
        transform.LookAt(focus);
    }
}
