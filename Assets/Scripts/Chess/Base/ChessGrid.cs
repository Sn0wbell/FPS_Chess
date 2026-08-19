using UnityEngine;

public class ChessGrid : MonoBehaviour
{
    [Min(0.0001f)]
    public float cellSize = 1f;

    [Tooltip("World position of A1 (file=0, rank=0). White perspective.")]
    public Vector3 origin = Vector3.zero;

    [Header("Gizmos")]
    public bool drawGridGizmos = true;
    public Color lineColor = Color.yellow;
    public Color cellCenterColor = Color.cyan;
    public float centerPointSize = 0.06f;

    public bool WorldToBoard(Vector3 worldPos, out int file, out int rank)
    {
        Vector3 local = worldPos - origin;

        file = Mathf.FloorToInt(local.x / cellSize);
        rank = Mathf.FloorToInt(local.z / cellSize);

        if ((uint)file > 7u || (uint)rank > 7u)
            return false;

        return true;
    }

    public Vector3 BoardToWorld(int file, int rank)
    {
        // Guard against invalid input to avoid silent bugs.
        file = Mathf.Clamp(file, 0, 7);
        rank = Mathf.Clamp(rank, 0, 7);

        return new Vector3(
            origin.x + file * cellSize + cellSize * 0.5f,
            origin.y,
            origin.z + rank * cellSize + cellSize * 0.5f
        );
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        // Protect invariant: cellSize must be positive.
        if (cellSize <= 0f)
        {
            cellSize = 0.0001f;
            Debug.LogWarning($"{nameof(ChessGrid)}: cellSize must be > 0. Value clamped.", this);
        }

        if (centerPointSize < 0f)
        {
            centerPointSize = 0f;
            Debug.LogWarning($"{nameof(ChessGrid)}: centerPointSize cannot be negative. Value clamped.", this);
        }
    }

    // ============================================================
    // GIZMO DRAWING (Editor only)
    // ============================================================
    private void OnDrawGizmos()
    {
        if (!drawGridGizmos) return;

        Gizmos.color = lineColor;

        float size = cellSize * 8f;

        // Horizontal lines (ranks)
        for (int r = 0; r <= 8; r++)
        {
            Vector3 a = origin + new Vector3(0f, 0f, r * cellSize);
            Vector3 b = origin + new Vector3(size, 0f, r * cellSize);
            Gizmos.DrawLine(a, b);
        }

        // Vertical lines (files)
        for (int f = 0; f <= 8; f++)
        {
            Vector3 a = origin + new Vector3(f * cellSize, 0f, 0f);
            Vector3 b = origin + new Vector3(f * cellSize, 0f, size);
            Gizmos.DrawLine(a, b);
        }

        // Cell centers
        Gizmos.color = cellCenterColor;
        float rSize = centerPointSize;

        for (int file = 0; file < 8; file++)
        {
            for (int rank = 0; rank < 8; rank++)
            {
                Vector3 pos = BoardToWorld(file, rank);
                Gizmos.DrawSphere(pos, rSize);
            }
        }
    }
#endif
}
