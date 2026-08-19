using System.Collections.Generic;
using UnityEngine;

public enum HighlightType
{
    Selection,
    LegalMove,
    CaptureMove,

    LastMoveFrom,
    LastMoveTo,

    Check,
    Checkmate
}

[System.Serializable]
public struct HighlightStyle
{
    public HighlightType type;
    public Material material;
}

public sealed class BoardHighlightSystem : MonoBehaviour
{
    [Header("References")]
    public ChessGrid grid;
    public HighlightTile tilePrefab;

    [Header("Styles")]
    public HighlightStyle[] styles;

    [Header("Pooling")]
    [SerializeField] private int poolSize = 48;

    // ======================================================
    // INTERNAL STATE
    // ======================================================

    private readonly Dictionary<HighlightType, Material> styleMap =
        new Dictionary<HighlightType, Material>();

    private readonly List<HighlightTile> transientTiles =
        new List<HighlightTile>(32);

    private readonly List<HighlightTile> persistentTiles =
        new List<HighlightTile>(8);

    private readonly Stack<HighlightTile> pool =
        new Stack<HighlightTile>(64);

    private readonly HashSet<HighlightTile> inPool =
        new HashSet<HighlightTile>();

    private bool isInitialized;

    // ======================================================
    // UNITY
    // ======================================================

    private void Awake()
    {
        if (grid == null || tilePrefab == null)
        {
            Debug.LogError("BoardHighlightSystem: Missing references.", this);
            enabled = false;
            return;
        }

        BuildStyleMap();
        PrewarmPool();

        isInitialized = true;
    }

    private void OnDisable()
    {
        ClearAll();
    }

    // ======================================================
    // INIT
    // ======================================================

    private void BuildStyleMap()
    {
        styleMap.Clear();

        for (int i = 0; i < styles.Length; i++)
        {
            var s = styles[i];
            if (s.material != null)
                styleMap[s.type] = s.material;
        }
    }

    private void PrewarmPool()
    {
        pool.Clear();
        inPool.Clear();

        for (int i = 0; i < poolSize; i++)
        {
            HighlightTile tile = Instantiate(tilePrefab, transform);
            tile.Deactivate();
            pool.Push(tile);
            inPool.Add(tile);
        }
    }

    // ======================================================
    // CLEAR
    // ======================================================

    public void ClearAll()
    {
        if (!isInitialized)
            return;

        ReturnList(transientTiles);
        ReturnList(persistentTiles);

        transientTiles.Clear();
        persistentTiles.Clear();
    }

    public void ClearTransient()
    {
        if (!isInitialized)
            return;

        ReturnList(transientTiles);
        transientTiles.Clear();
    }

    public void ClearCheck()
    {
        for (int i = persistentTiles.Count - 1; i >= 0; i--)
        {
            var t = persistentTiles[i];
            if (t == null)
                continue;

            if (t.Type == HighlightType.Check ||
                t.Type == HighlightType.Checkmate)
            {
                SafeReturn(t);
                persistentTiles.RemoveAt(i);
            }
        }
    }

    private void ClearLastMove()
    {
        for (int i = persistentTiles.Count - 1; i >= 0; i--)
        {
            var t = persistentTiles[i];
            if (t == null)
                continue;

            if (t.Type == HighlightType.LastMoveFrom ||
                t.Type == HighlightType.LastMoveTo)
            {
                SafeReturn(t);
                persistentTiles.RemoveAt(i);
            }
        }
    }

    // ======================================================
    // PUBLIC SHOW API
    // ======================================================

    public void ShowSingle(
        int file,
        int rank,
        HighlightType type,
        bool isTransient = true)
    {
        if (!isInitialized)
            return;

        HighlightTile tile = Spawn(file, rank, type);
        if (tile == null)
            return;

        if (isTransient)
            transientTiles.Add(tile);
        else
            persistentTiles.Add(tile);
    }

    public void ShowLastMove(Vector2Int from, Vector2Int to)
    {
        if (!isInitialized)
            return;

        ClearLastMove();

        SpawnPersistent(from.x, from.y, HighlightType.LastMoveFrom);
        SpawnPersistent(to.x, to.y, HighlightType.LastMoveTo);
    }

    public void ShowCheck(Vector2Int kingSquare, bool isMate)
    {
        if (!isInitialized)
            return;

        ClearCheck();

        SpawnPersistent(
            kingSquare.x,
            kingSquare.y,
            isMate ? HighlightType.Checkmate : HighlightType.Check);
    }

    // ======================================================
    // SPAWN CORE
    // ======================================================

    private HighlightTile Spawn(int file, int rank, HighlightType type)
    {
        if ((uint)file > 7u || (uint)rank > 7u)
            return null;

        if (!styleMap.TryGetValue(type, out Material mat))
            return null;

        if (pool.Count == 0)
        {
            Debug.LogWarning("Highlight pool exhausted.", this);
            return null;
        }

        HighlightTile tile = pool.Pop();
        inPool.Remove(tile);

        tile.Deactivate();

        Vector3 pos = grid.BoardToWorld(file, rank);
        tile.Activate(pos, mat, type);

        return tile;
    }

    private void SpawnPersistent(int file, int rank, HighlightType type)
    {
        HighlightTile tile = Spawn(file, rank, type);
        if (tile != null)
            persistentTiles.Add(tile);
    }

    // ======================================================
    // POOL RETURN
    // ======================================================

    private void ReturnList(List<HighlightTile> list)
    {
        for (int i = 0; i < list.Count; i++)
            SafeReturn(list[i]);
    }

    private void SafeReturn(HighlightTile tile)
    {
        if (tile == null || inPool.Contains(tile))
            return;

        tile.Deactivate();
        pool.Push(tile);
        inPool.Add(tile);
    }
}
