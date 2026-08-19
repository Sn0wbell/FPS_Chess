using UnityEngine;

public enum PieceType
{
    Pawn,
    Rook,
    Knight,
    Bishop,
    Queen,
    King,
    None
}

public class ChessPiece : MonoBehaviour
{
    [Header("Identity")]
    public PieceType type;
    public bool isWhite;

    [Header("Board Position (read-only at runtime)")]
    [HideInInspector] public int file = -1;
    [HideInInspector] public int rank = -1;

    [Header("State")]
    [SerializeField] private bool isCaptured = false;

    public bool IsActiveOnBoard => !isCaptured && file >= 0 && rank >= 0;

    public void SetPosition(Vector3 worldPos, int f, int r)
    {
        // Guard against invalid board indices to prevent silent bugs.
        if ((uint)f > 7u || (uint)r > 7u)
        {
            Debug.LogWarning(
                $"[{name}] SetPosition called with invalid board coords ({f},{r}). Clamping.",
                this);
            f = Mathf.Clamp(f, 0, 7);
            r = Mathf.Clamp(r, 0, 7);
        }

        transform.position = worldPos;
        file = f;
        rank = r;
        isCaptured = false;
        gameObject.SetActive(true);
    }

    public void Capture()
    {
        isCaptured = true;
        file = -1;
        rank = -1;
        gameObject.SetActive(false);
    }

    public void SetBoardCoords(int f, int r)
    {
        if ((uint)f > 7u || (uint)r > 7u)
        {
            Debug.LogWarning(
                $"[{name}] SetBoardCoords called with invalid coords ({f},{r}). Ignored.",
                this);
            return;
        }

        file = f;
        rank = r;
        isCaptured = false;
    }

    public void ClearBoardCoords()
    {
        file = -1;
        rank = -1;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        // Defensive checks to keep inspector edits safe.
        if (isCaptured)
        {
            file = -1;
            rank = -1;
        }
    }
#endif

    public override string ToString()
    {
        string color = isWhite ? "White" : "Black";
        if (IsActiveOnBoard)
            return $"{color} {type} @ ({file},{rank})";
        return $"{color} {type} (captured)";
    }

    public void OnPromoted() { }
}
