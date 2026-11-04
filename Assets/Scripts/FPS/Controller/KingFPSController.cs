using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The king's controller: right mouse button raises the shield (KingHealth) while it is held;
/// with the shield up the king can neither attack nor aim. Everything else is a normal piece.
/// Put this component on the king prefab in place of ChessPieceFPSController.
/// </summary>
[RequireComponent(typeof(KingHealth))]
public class KingFPSController : ChessPieceFPSController
{
    private KingHealth kingHealth;
    private bool shieldHeldThisFrame;

    public bool ShieldRaised => kingHealth != null && kingHealth.ShieldActive;

    protected override void HandleWeaponInput(bool attackBlocked)
    {
        shieldHeldThisFrame = Mouse.current != null && Mouse.current.rightButton.isPressed && !IsDead;

        // Shield up: no attack, no aim (the right button is the shield, not the scope).
        base.HandleWeaponInput(attackBlocked || shieldHeldThisFrame);
    }

    private void LateUpdate()
    {
        if (kingHealth == null)
            kingHealth = GetComponent<KingHealth>();

        // Raised only while held this frame (dropped when the input is not read: control off, dead, AI).
        if (kingHealth != null)
            kingHealth.SetShieldRaised(shieldHeldThisFrame);
        shieldHeldThisFrame = false;
    }
}
