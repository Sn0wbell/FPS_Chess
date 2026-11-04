using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Asked before a HUMAN capture starts its FPS duel: shows attacker vs defender and lets
/// the player confirm or cancel (cancel = the move is not played, still the player's turn).
/// Implemented by the temporary ChessGameplayOverlay; the real UI can implement it later.
/// Without one, captures are confirmed automatically.
/// </summary>
public interface ICaptureConfirmation
{
    Task<bool> ConfirmCaptureAsync(CombatRequest request, double estimatedWinChance, CancellationToken ct);
}
