using UnityEngine;
using System;

public class BishopTrailPool : MonoBehaviour
{
    [SerializeField] private Transform prefab;
    [SerializeField] private int poolSize = 8;

    private Transform[] pool;
    private int nextIndex = 0;

    private void Awake()
    {
        if (prefab == null)
        {
            Debug.LogError($"{name}: Prefab is null in {nameof(BishopTrailPool)}");
            pool = Array.Empty<Transform>();
            return;
        }

        pool = new Transform[poolSize];
        for (int i = 0; i < poolSize; i++)
        {
            Transform t = Instantiate(prefab, transform);
            t.gameObject.SetActive(false);
            pool[i] = t;
        }
    }

    public Transform Get()
    {
        if (pool.Length == 0) return null;

        for (int i = 0; i < pool.Length; i++)
        {
            int idx = (nextIndex + i) % pool.Length;
            if (!pool[idx].gameObject.activeSelf)
            {
                nextIndex = (idx + 1) % pool.Length;
                pool[idx].gameObject.SetActive(true);
                return pool[idx];
            }
        }

        Transform tNew = Instantiate(prefab, transform);
        tNew.gameObject.SetActive(true);

        Array.Resize(ref pool, pool.Length + 1);
        pool[pool.Length - 1] = tNew;
        nextIndex = pool.Length % pool.Length;
        return tNew;
    }

    public void Return(Transform t)
    {
        if (t == null) return;

        t.gameObject.SetActive(false);
        t.SetParent(transform);
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;
    }
}
