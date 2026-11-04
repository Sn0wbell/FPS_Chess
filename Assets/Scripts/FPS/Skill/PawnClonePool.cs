using UnityEngine;
using System.Collections.Generic;

public class PawnClonePool
{
    private readonly GameObject prefab;
    private readonly Transform root;
    private readonly Stack<PawnCloneHealth> pool = new();
    private readonly HashSet<PawnCloneHealth> inPool = new();

    public PawnClonePool(GameObject prefab, Transform owner)
    {
        this.prefab = prefab;
        root = new GameObject("[PawnClonePool]").transform;
        root.SetParent(owner);
    }

    /// <summary>Returns a clone, or null when the prefab is missing/invalid.</summary>
    public PawnCloneHealth Get(Vector3 pos, Quaternion rot)
    {
        PawnCloneHealth clone = null;

        while (pool.Count > 0 && clone == null)
        {
            clone = pool.Pop();          // skip clones destroyed while pooled
            if (clone != null) inPool.Remove(clone);
        }

        if (clone != null)
        {
            clone.transform.SetPositionAndRotation(pos, rot);
        }
        else
        {
            if (prefab == null)
            {
                Debug.LogError("PawnClonePool: clone prefab is not assigned.");
                return null;
            }

            GameObject go = Object.Instantiate(prefab, pos, rot);
            clone = go.GetComponent<PawnCloneHealth>();
            if (clone == null)
            {
                Debug.LogError($"PawnClonePool: '{prefab.name}' has no PawnCloneHealth.");
                Object.Destroy(go);
                return null;
            }
        }

        clone.transform.SetParent(null);
        return clone;
    }

    /// <summary>Idempotent: a clone already in the pool is never pushed twice.</summary>
    public void Return(PawnCloneHealth clone)
    {
        if (clone == null || inPool.Contains(clone))
            return;

        clone.CancelAll();
        if (root != null)
            clone.transform.SetParent(root);
        pool.Push(clone);
        inPool.Add(clone);
    }
}
