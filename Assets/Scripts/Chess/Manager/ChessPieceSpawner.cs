using UnityEngine;

public class ChessPieceSpawner : MonoBehaviour
{
    [Header("Grid reference")]
    public ChessGrid grid;

    [Header("White Prefabs")]
    public GameObject whitePawn;
    public GameObject whiteRook;
    public GameObject whiteKnight;
    public GameObject whiteBishop;
    public GameObject whiteQueen;
    public GameObject whiteKing;

    [Header("Black Prefabs")]
    public GameObject blackPawn;
    public GameObject blackRook;
    public GameObject blackKnight;
    public GameObject blackBishop;
    public GameObject blackQueen;
    public GameObject blackKing;

    /// <summary>
    /// Spawns one piece of the given type/colour on a board square.
    /// Used for the initial setup (from any FEN) and for promotion (model swap).
    /// Returns null when the prefab is missing or invalid.
    /// </summary>
    public ChessPiece SpawnPiece(PieceType type, bool isWhite, int file, int rank)
    {
        if (grid == null)
        {
            Debug.LogError("ChessPieceSpawner: Grid reference is NULL.");
            return null;
        }

        GameObject prefab = GetPrefab(type, isWhite);
        if (prefab == null)
        {
            Debug.LogError($"ChessPieceSpawner: No prefab for {(isWhite ? "White" : "Black")} {type} at ({file},{rank})");
            return null;
        }

        ChessPiece prefabPiece = prefab.GetComponent<ChessPiece>();
        if (prefabPiece == null)
        {
            Debug.LogError(
                $"ChessPieceSpawner: Prefab '{prefab.name}' has NO ChessPiece component. Spawn aborted.");
            return null;
        }

        if (prefabPiece.isWhite != isWhite)
        {
            Debug.LogError(
                $"ChessPieceSpawner: Prefab '{prefab.name}' has isWhite={prefabPiece.isWhite} " +
                $"but expected {isWhite}. Check prefab setup.");
            return null;
        }

        Vector3 pos = grid.BoardToWorld(file, rank);
        GameObject obj = Instantiate(prefab, pos, Quaternion.identity, transform);

        ChessPiece piece = obj.GetComponent<ChessPiece>();
        if (piece == null)
        {
            // This should NEVER happen because prefab was validated above
            Debug.LogError(
                $"ChessPieceSpawner: Instantiated object '{obj.name}' lost ChessPiece component.");
            Destroy(obj);
            return null;
        }

        // The rules core is authoritative; keep the component in sync with it.
        piece.type = type;
        piece.file = file;
        piece.rank = rank;
        return piece;
    }

    public GameObject GetPrefab(PieceType type, bool isWhite)
    {
        switch (type)
        {
            case PieceType.Pawn: return isWhite ? whitePawn : blackPawn;
            case PieceType.Rook: return isWhite ? whiteRook : blackRook;
            case PieceType.Knight: return isWhite ? whiteKnight : blackKnight;
            case PieceType.Bishop: return isWhite ? whiteBishop : blackBishop;
            case PieceType.Queen: return isWhite ? whiteQueen : blackQueen;
            case PieceType.King: return isWhite ? whiteKing : blackKing;
            default: return null;
        }
    }
}
