using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A spent shell thrown out of a gun: purely visual (its colliders / rigidbodies are switched off, so bullets and
/// pieces never meet it). It flies, spins, bounces off the ground / walls a couple of times, lies there, then shrinks
/// away at the end of its lifetime. Pooled per shell prefab.
/// </summary>
public sealed class EjectedShell : MonoBehaviour
{
    private const float Gravity = 9.81f;
    private const float Bounciness = 0.35f;
    private const int MaxBounces = 3;
    private const float RestSpeed = 0.4f;
    private const float SurfaceGap = 0.005f;
    private const float ShrinkSeconds = 0.25f;

    private static readonly Dictionary<GameObject, Stack<EjectedShell>> free = new();
    private static Transform root;
    private static int hitMask;

    private GameObject prefab;
    private Vector3 baseScale;
    private Vector3 velocity;
    private Vector3 spinAxis;
    private float spin;
    private int bounces;
    private bool resting;
    private float endTime;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        free.Clear();
        root = null;
        hitMask = 0;
    }

    /// <summary>Throws a shell of <paramref name="shellPrefab"/> from <paramref name="position"/>.</summary>
    public static EjectedShell Throw(GameObject shellPrefab, Vector3 position, Quaternion rotation, Vector3 velocity,
        float spinDegreesPerSecond, float lifetime, Vector3? worldScale = null)
    {
        if (shellPrefab == null)
            return null;

        EjectedShell s = null;
        if (free.TryGetValue(shellPrefab, out var stack))
            while (stack.Count > 0 && s == null)
                s = stack.Pop(); // skip any destroyed meanwhile
        if (s == null)
            s = Create(shellPrefab);

        s.transform.SetPositionAndRotation(position, rotation);
        if (worldScale.HasValue)
            s.baseScale = worldScale.Value; // a copy of a part in a scaled gun: its size as seen
        s.transform.localScale = s.baseScale;
        s.velocity = velocity;
        s.spinAxis = Random.onUnitSphere;
        s.spin = spinDegreesPerSecond;
        s.bounces = 0;
        s.resting = false;
        s.endTime = Time.time + Mathf.Max(0f, lifetime);
        s.gameObject.SetActive(true);
        return s;
    }

    private static EjectedShell Create(GameObject shellPrefab)
    {
        if (root == null)
            root = new GameObject("[EjectedShells]").transform;
        if (hitMask == 0)
            hitMask = ~LayerMask.GetMask("Bullet", "Weapon", "ChessPieces", "Ignore Raycast");

        GameObject go = Instantiate(shellPrefab, root);
        foreach (var c in go.GetComponentsInChildren<Collider>(true))
            c.enabled = false; // visual only: never in the way of bullets or pieces
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
        {
            rb.isKinematic = true;
            rb.detectCollisions = false;
        }
        var s = go.AddComponent<EjectedShell>();
        s.prefab = shellPrefab;
        s.baseScale = go.transform.localScale;
        return s;
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        if (!resting)
            Fly(dt);

        float left = endTime - Time.time;
        if (left <= 0f)
        {
            Release();
            return;
        }
        if (left < ShrinkSeconds)
            transform.localScale = baseScale * (left / ShrinkSeconds);
    }

    private void Fly(float dt)
    {
        Vector3 from = transform.position;
        velocity += Vector3.down * (Gravity * dt);
        Vector3 step = velocity * dt;
        float distance = step.magnitude;

        if (distance > 1e-6f && Physics.Raycast(from, step / distance, out RaycastHit hit, distance, hitMask, QueryTriggerInteraction.Ignore))
        {
            transform.position = hit.point + hit.normal * SurfaceGap;
            velocity = Vector3.Reflect(velocity, hit.normal) * Bounciness;
            spin *= Bounciness;
            bounces++;
            if (bounces >= MaxBounces || velocity.magnitude < RestSpeed)
            {
                resting = true;
                velocity = Vector3.zero;
            }
        }
        else
        {
            transform.position = from + step;
        }

        if (spin != 0f)
            transform.rotation = Quaternion.AngleAxis(spin * dt, spinAxis) * transform.rotation;
    }

    private void Release()
    {
        gameObject.SetActive(false);
        if (prefab == null)
        {
            Destroy(gameObject);
            return;
        }
        if (!free.TryGetValue(prefab, out var stack))
            free[prefab] = stack = new Stack<EjectedShell>();
        stack.Push(this);
    }
}
