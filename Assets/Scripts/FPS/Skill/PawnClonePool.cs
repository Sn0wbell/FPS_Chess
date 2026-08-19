using UnityEngine;
using System.Collections.Generic;

public class PawnClonePool
{
    private readonly GameObject prefab;
    private readonly Transform root;
    private readonly Stack<PawnCloneHealth> pool = new();

    public PawnClonePool(GameObject prefab, Transform owner)
    {
        this.prefab = prefab;
        root = new GameObject("[PawnClonePool]").transform;
        root.SetParent(owner);
    }

    public PawnCloneHealth Get(Vector3 pos, Quaternion rot)
    {
        PawnCloneHealth clone;

        if (pool.Count > 0)
        {
            clone = pool.Pop();
            clone.transform.SetPositionAndRotation(pos, rot);
        }
        else
        {
            GameObject go = Object.Instantiate(prefab, pos, rot);
            clone = go.GetComponent<PawnCloneHealth>();
        }

        clone.transform.SetParent(null);

        
        return clone;
    }

    public void Return(PawnCloneHealth clone)
    {
        clone.CancelAll();
        clone.transform.SetParent(root);
        pool.Push(clone);
    }
}
