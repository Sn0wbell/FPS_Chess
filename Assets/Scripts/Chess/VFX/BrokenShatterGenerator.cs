using UnityEngine;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public sealed class BrokenShatterGenerator : MonoBehaviour
{
    [Header("Fragment Settings")]
    [Range(1, 64)]
    public int fragments = 18;
    public float fragmentSize = 0.06f;

    [Header("Physics")]
    public float explosionForce = 1.6f;
    public float explosionRadius = 0.45f;
    [Tooltip("Very small value (0.02–0.06 recommended)")]
    public float upwardModifier = 0.03f;

    [Header("Lifetime")]
    public float lifetime = 1.4f;

    [Header("Rendering")]
    public Material fallbackMaterial;

    [Header("Layer")]
    [Tooltip("Layer used for all shatter fragments")]
    [SerializeField] private LayerMask fragmentLayer;

    private static Mesh cubeMesh;
    private CancellationTokenSource cts;

    private void Awake()
    {
        if (cubeMesh == null)
            cubeMesh = CreateCubeMesh();

        cts = new CancellationTokenSource();
    }

    private void OnDestroy()
    {
        cts?.Cancel();
        cts?.Dispose();
    }

    /// <summary>
    /// Spawn shatter fragments at position using source material.
    /// Board logic must already be committed.
    /// </summary>
    public async Task ShatterAt(Vector3 position, Material sourceMaterial)
    {
        if (!isActiveAndEnabled)
            return;

        CancellationToken token = cts.Token;

        GameObject root = new GameObject("ShatterFragments");
        root.transform.position = position;
        root.transform.rotation = Quaternion.identity;

        int fragLayer = ResolveLayer(fragmentLayer);

        List<Rigidbody> bodies = new List<Rigidbody>(fragments);
        List<Material> materials = new List<Material>(fragments);

        try
        {
            for (int i = 0; i < fragments; i++)
            {
                if (token.IsCancellationRequested)
                    return;

                GameObject frag = new GameObject($"frag_{i}");
                frag.layer = fragLayer;
                frag.transform.SetParent(root.transform, false);

                float size = fragmentSize * Random.Range(0.85f, 1.15f);
                frag.transform.localScale = Vector3.one * size;
                frag.transform.position =
                    position + Random.insideUnitSphere * (fragmentSize * 0.6f);
                frag.transform.rotation = Random.rotation;

                MeshFilter mf = frag.AddComponent<MeshFilter>();
                mf.sharedMesh = cubeMesh;

                MeshRenderer mr = frag.AddComponent<MeshRenderer>();
                Material mat =
                    sourceMaterial != null
                        ? new Material(sourceMaterial)
                        : fallbackMaterial != null
                            ? new Material(fallbackMaterial)
                            : new Material(Shader.Find("Standard"));

                mr.sharedMaterial = mat;
                materials.Add(mat);

                Rigidbody rb = frag.AddComponent<Rigidbody>();
                rb.mass = 0.25f;
                rb.linearDamping = 1.2f;
                rb.angularDamping = 1.0f;
                rb.useGravity = true;
                rb.isKinematic = true;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.Discrete;

                bodies.Add(rb);

                Destroy(frag, lifetime);
            }

            Destroy(root, lifetime + 0.25f);

            await Task.Yield();

            foreach (var rb in bodies)
            {
                if (rb == null || token.IsCancellationRequested)
                    continue;

                rb.isKinematic = false;

                Vector3 center = position + new Vector3(Random.Range(-0.1f, 0.1f), 0f, Random.Range(-0.1f, 0.1f));

                rb.AddExplosionForce(
                    explosionForce,
                    center,
                    explosionRadius,
                    upwardModifier,
                    ForceMode.Impulse);

                rb.AddTorque( Random.insideUnitSphere * 0.05f, ForceMode.Impulse);
            }

            await Task.Delay(Mathf.CeilToInt(lifetime * 1000f), token);
        }
        finally
        {
            foreach (var m in materials)
                if (m != null)
                    Destroy(m);
        }
    }

    // ======================================================
    // UTIL
    // ======================================================

    private static int ResolveLayer(LayerMask mask)
    {
        int v = mask.value;
        if (v == 0)
            return 0;

        return Mathf.RoundToInt(Mathf.Log(v, 2));
    }

    private static Mesh CreateCubeMesh()
    {
        Mesh m = new Mesh { name = "ProceduralCube" };

        Vector3[] v =
        {
            new(-0.5f,-0.5f,-0.5f),
            new( 0.5f,-0.5f,-0.5f),
            new( 0.5f, 0.5f,-0.5f),
            new(-0.5f, 0.5f,-0.5f),
            new(-0.5f,-0.5f, 0.5f),
            new( 0.5f,-0.5f, 0.5f),
            new( 0.5f, 0.5f, 0.5f),
            new(-0.5f, 0.5f, 0.5f)
        };

        int[] t =
        {
            0,2,1, 0,3,2,
            4,5,6, 4,6,7,
            0,1,5, 0,5,4,
            2,3,7, 2,7,6,
            0,4,7, 0,7,3,
            1,2,6, 1,6,5
        };

        m.vertices = v;
        m.triangles = t;
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }
}
