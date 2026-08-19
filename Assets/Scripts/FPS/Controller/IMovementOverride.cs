using UnityEngine;
public interface IMovementOverride
{
    bool IsActive { get; }
    Vector3 GetMovementDelta(float deltaTime);
    void ForceCancelMovement();
}
