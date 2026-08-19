using UnityEngine;
using System.Collections.Generic;

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
    /// Spawn all pieces in standard chess starting position.
    /// Returns list of ChessPiece components successfully spawned.
    /// </summary>
    public List<ChessPiece> SpawnAllPieces()
    {
        if (grid == null)
        {
            Debug.LogError("ChessPieceSpawner: Grid reference is NULL.");
            return new List<ChessPiece>();
        }

        var list = new List<ChessPiece>(32);

        // White
        SpawnPawnRank(1, whitePawn, true, list);
        SpawnBackRankWhite(0, list);

        // Black
        SpawnPawnRank(6, blackPawn, false, list);
        SpawnBackRankBlack(7, list);

        return list;
    }

    // ============================================================
    // INTERNAL SPAWN HELPERS
    // ============================================================

    private void SpawnPawnRank(int rank, GameObject pawnPrefab, bool isWhite, List<ChessPiece> list)
    {
        for (int file = 0; file < 8; file++)
        {
            SpawnOne(pawnPrefab, file, rank, isWhite, list);
        }
    }

    private void SpawnBackRankWhite(int rank, List<ChessPiece> list)
    {
        SpawnOne(whiteRook, 0, rank, true, list);
        SpawnOne(whiteKnight, 1, rank, true, list);
        SpawnOne(whiteBishop, 2, rank, true, list);
        SpawnOne(whiteQueen, 3, rank, true, list);
        SpawnOne(whiteKing, 4, rank, true, list);
        SpawnOne(whiteBishop, 5, rank, true, list);
        SpawnOne(whiteKnight, 6, rank, true, list);
        SpawnOne(whiteRook, 7, rank, true, list);
    }

    private void SpawnBackRankBlack(int rank, List<ChessPiece> list)
    {
        SpawnOne(blackRook, 0, rank, false, list);
        SpawnOne(blackKnight, 1, rank, false, list);
        SpawnOne(blackBishop, 2, rank, false, list);
        SpawnOne(blackQueen, 3, rank, false, list);
        SpawnOne(blackKing, 4, rank, false, list);
        SpawnOne(blackBishop, 5, rank, false, list);
        SpawnOne(blackKnight, 6, rank, false, list);
        SpawnOne(blackRook, 7, rank, false, list);
    }

    private void SpawnOne(
        GameObject prefab,
        int file,
        int rank,
        bool expectedIsWhite,
        List<ChessPiece> list)
    {
        if (prefab == null)
        {
            Debug.LogError($"ChessPieceSpawner: Prefab is NULL at ({file},{rank})");
            return;
        }

        ChessPiece prefabPiece = prefab.GetComponent<ChessPiece>();
        if (prefabPiece == null)
        {
            Debug.LogError(
                $"ChessPieceSpawner: Prefab '{prefab.name}' has NO ChessPiece component. Spawn aborted.");
            return;
        }

        if (prefabPiece.isWhite != expectedIsWhite)
        {
            Debug.LogError(
                $"ChessPieceSpawner: Prefab '{prefab.name}' has isWhite={prefabPiece.isWhite} " +
                $"but expected {expectedIsWhite}. Check prefab setup.");
            return;
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
            return;
        }

        piece.file = file;
        piece.rank = rank;

        list.Add(piece);
    }
}
