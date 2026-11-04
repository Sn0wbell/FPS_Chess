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
    Checkmate,

    // Appended only: existing serialized style indices must stay valid.
    LastStandKing,
    LastStandAttacker,
    LastStandDefeated,

    Threat,        // threat map: squares the opponent controls
    FailedAttack,  // attacker + target of a capture whose duel was lost
    KingInDanger   // a king left in check after a lost duel (it can be captured)
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
    [Tooltip("Types without a material fall back to a related style (e.g. Threat -> LegalMove).")]
    public HighlightStyle[] styles;

    [Header("Pooling")]
    [Tooltip("Tiles created up front. The pool grows when more are needed (threat map can use 64).")]
    [SerializeField] private int poolSize = 48;

    [Header("Layering")]
    [Tooltip("Vertical step between highlight layers so overlapping tiles never z-fight.")]
    [SerializeField] private float layerHeight = 0.002f;

    [Header("Pulse (check, danger, Last Stand)")]
    [SerializeField] private float pulseSpeed = 4f;
    [Range(0f, 0.5f)]
    [SerializeField] private float pulseAmount = 0.08f;

    // ======================================================
    // INTERNAL STATE
    // ======================================================

    private readonly Dictionary<HighlightType, Material> styleMap =
        new Dictionary<HighlightType, Material>();

    private readonly List<HighlightTile> transientTiles =
        new List<HighlightTile>(32);

    private readonly List<HighlightTile> persistentTiles =
        new List<HighlightTile>(16);

    private readonly List<HighlightTile> threatTiles =
        new List<HighlightTile>(64);

    private readonly Stack<HighlightTile> pool =
        new Stack<HighlightTile>(64);

    private readonly HashSet<HighlightTile> inPool =
        new HashSet<HighlightTile>();

    private Vector3 tileBaseScale = Vector3.one;
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

        tileBaseScale = tilePrefab.transform.localScale;

        BuildStyleMap();
        PrewarmPool();

        isInitialized = true;
    }

    private void OnDisable()
    {
        ClearAll();
    }

    private void Update()
    {
        if (!isInitialized || pulseAmount <= 0f)
            return;

        float k = 1f + Mathf.Sin(Time.time * pulseSpeed) * pulseAmount;
        for (int i = 0; i < persistentTiles.Count; i++)
        {
            HighlightTile t = persistentTiles[i];
            if (t != null && Pulses(t.Type))
                t.transform.localScale = tileBaseScale * k;
        }
    }

    // ======================================================
    // INIT
    // ======================================================

    private void BuildStyleMap()
    {
        styleMap.Clear();

        if (styles == null)
            return;

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
            pool.Push(CreateTile());
    }

    private HighlightTile CreateTile()
    {
        HighlightTile tile = Instantiate(tilePrefab, transform);
        tile.Deactivate();
        inPool.Add(tile);
        return tile;
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
        ReturnList(threatTiles);

        transientTiles.Clear();
        persistentTiles.Clear();
        threatTiles.Clear();
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
        ClearPersistent(HighlightType.Check, HighlightType.Checkmate, HighlightType.KingInDanger);
    }

    public void ClearLastMove()
    {
        ClearPersistent(HighlightType.LastMoveFrom, HighlightType.LastMoveTo);
    }

    public void ClearFailedAttack()
    {
        ClearPersistent(HighlightType.FailedAttack, HighlightType.FailedAttack);
    }

    public void ClearLastStand()
    {
        ClearPersistent(HighlightType.LastStandKing, HighlightType.LastStandAttacker, HighlightType.LastStandDefeated);
    }

    public void ClearThreats()
    {
        if (!isInitialized)
            return;

        ReturnList(threatTiles);
        threatTiles.Clear();
    }

    private void ClearPersistent(HighlightType a, HighlightType b, HighlightType? c = null)
    {
        for (int i = persistentTiles.Count - 1; i >= 0; i--)
        {
            var t = persistentTiles[i];
            if (t == null)
            {
                persistentTiles.RemoveAt(i);
                continue;
            }

            if (t.Type == a || t.Type == b || (c.HasValue && t.Type == c.Value))
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

    /// <summary>A king that can be captured right now (left in check after a lost duel).</summary>
    public void ShowKingInDanger(Vector2Int kingSquare)
    {
        if (!isInitialized)
            return;

        ClearCheck();
        SpawnPersistent(kingSquare.x, kingSquare.y, HighlightType.KingInDanger);
    }

    /// <summary>Marks the attacker's square and the target square of a lost capture duel.</summary>
    public void ShowFailedAttack(Vector2Int attacker, Vector2Int target)
    {
        if (!isInitialized)
            return;

        ClearFailedAttack();
        SpawnPersistent(attacker.x, attacker.y, HighlightType.FailedAttack);
        SpawnPersistent(target.x, target.y, HighlightType.FailedAttack);
    }

    /// <summary>Replaces the threat map with the given squares.</summary>
    public void ShowThreats(IReadOnlyList<Vector2Int> squares)
    {
        if (!isInitialized)
            return;

        ClearThreats();
        for (int i = 0; i < squares.Count; i++)
        {
            HighlightTile tile = Spawn(squares[i].x, squares[i].y, HighlightType.Threat);
            if (tile != null)
                threatTiles.Add(tile);
        }
    }

    // ======================================================
    // LAST STAND
    // ======================================================

    /// <summary>Marks the mated king and every piece taking part in the mate (persistent).</summary>
    public void ShowLastStand(Vector2Int kingSquare, IReadOnlyList<Vector2Int> attackers)
    {
        if (!isInitialized)
            return;

        ClearLastStand();
        SpawnPersistent(kingSquare.x, kingSquare.y, HighlightType.LastStandKing);
        for (int i = 0; i < attackers.Count; i++)
            SpawnPersistent(attackers[i].x, attackers[i].y, HighlightType.LastStandAttacker);
    }

    /// <summary>Marks the squares of the participants the king defeated (recap after the Last Stand).</summary>
    public void ShowLastStandDefeated(IReadOnlyList<Vector2Int> squares)
    {
        if (!isInitialized)
            return;

        for (int i = 0; i < squares.Count; i++)
            SpawnPersistent(squares[i].x, squares[i].y, HighlightType.LastStandDefeated);
    }

    // ======================================================
    // SPAWN CORE
    // ======================================================

    private HighlightTile Spawn(int file, int rank, HighlightType type)
    {
        if ((uint)file > 7u || (uint)rank > 7u)
            return null;

        if (!styleMap.TryGetValue(type, out Material mat) &&
            !styleMap.TryGetValue(FallbackStyle(type), out mat))
            return null;

        HighlightTile tile = pool.Count > 0 ? pool.Pop() : CreateTile();
        inPool.Remove(tile);

        tile.Deactivate();

        Vector3 pos = grid.BoardToWorld(file, rank);
        pos.y += LayerOf(type) * layerHeight;

        tile.transform.localScale = tileBaseScale;
        tile.Activate(pos, mat, type);

        return tile;
    }

    private void SpawnPersistent(int file, int rank, HighlightType type)
    {
        HighlightTile tile = Spawn(file, rank, type);
        if (tile != null)
            persistentTiles.Add(tile);
    }

    // New highlight types reuse an existing material until a dedicated style is assigned.
    private static HighlightType FallbackStyle(HighlightType type)
    {
        switch (type)
        {
            case HighlightType.LastStandKing: return HighlightType.Checkmate;
            case HighlightType.LastStandAttacker: return HighlightType.CaptureMove;
            case HighlightType.LastStandDefeated: return HighlightType.LastMoveFrom;
            case HighlightType.Threat: return HighlightType.LegalMove;
            case HighlightType.FailedAttack: return HighlightType.CaptureMove;
            case HighlightType.KingInDanger: return HighlightType.Check;
            default: return type;
        }
    }

    // Higher layers are drawn slightly above lower ones.
    private static int LayerOf(HighlightType type)
    {
        switch (type)
        {
            case HighlightType.Threat: return 0;
            case HighlightType.LastMoveFrom:
            case HighlightType.LastMoveTo: return 1;
            case HighlightType.FailedAttack: return 2;
            case HighlightType.LegalMove:
            case HighlightType.CaptureMove:
            case HighlightType.Selection: return 3;
            case HighlightType.Check:
            case HighlightType.Checkmate:
            case HighlightType.KingInDanger: return 4;
            default: return 5; // Last Stand
        }
    }

    private static bool Pulses(HighlightType type) =>
        type == HighlightType.Check ||
        type == HighlightType.Checkmate ||
        type == HighlightType.KingInDanger ||
        type == HighlightType.LastStandKing ||
        type == HighlightType.LastStandAttacker ||
        type == HighlightType.FailedAttack;

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

        tile.transform.localScale = tileBaseScale;
        tile.Deactivate();
        pool.Push(tile);
        inPool.Add(tile);
    }
}
