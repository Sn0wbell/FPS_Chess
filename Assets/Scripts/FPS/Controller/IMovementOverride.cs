using UnityEngine;
public interface IMovementOverride
{
    bool IsActive { get; }
    Vector3 GetMovementDelta(float deltaTime);
    void ForceCancelMovement();
}

/// <summary>
/// A skill holding a piece (forced move: the bishop's rope, the king's pull) is told when another source knocks
/// that piece, right after the knock is set, so it can let it fly (and e.g. keep it on the rope).
/// </summary>
public interface IHeldPieceKnockHandler
{
    void OnHeldPieceKnocked(ChessPieceFPSController piece);

    /// <summary>Another skill takes the piece over (a king's pull, a bishop's rope): let go of it.</summary>
    void OnHeldPieceTakenOver(ChessPieceFPSController piece);
}
