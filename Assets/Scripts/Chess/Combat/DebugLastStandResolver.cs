using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Stand-in for the FPS Last Stand while the arena is not wired to the board.
/// Assign it to ChessGameManager.lastStandResolverComponent.
/// </summary>
public sealed class DebugLastStandResolver : MonoBehaviour, ILastStandResolver
{
    public enum Mode
    {
        KingAlwaysWins,
        KingAlwaysFalls,
        Random
    }

    [SerializeField] private Mode mode = Mode.Random;

    [Range(0f, 1f)]
    [SerializeField] private float kingWinChance = 0.3f;

    [Tooltip("Fake Last Stand duration in seconds (0 = instant).")]
    [Min(0f)]
    [SerializeField] private float duration = 1f;

    private readonly System.Random rng = new System.Random();

    public async Task<LastStandResult> ResolveAsync(LastStandRequest request, CancellationToken ct)
    {
        if (duration > 0f)
            await Task.Delay(Mathf.CeilToInt(duration * 1000f), ct);

        bool kingWins =
            mode == Mode.KingAlwaysWins ||
            (mode == Mode.Random && rng.NextDouble() < kingWinChance);

        LastStandResult result;
        if (kingWins)
        {
            result = LastStandResult.KingWins(request);
        }
        else
        {
            // The king takes down some (never all) of the participants before falling.
            int n = request.Participants.Count;
            int killed = n > 0 ? rng.Next(n) : 0;
            var defeated = new List<int>(killed);

            if (request.Mode == LastStandMode.Sequential)
            {
                // One by one: the first `killed` opponents in fight order fell.
                for (int i = 0; i < killed; i++)
                    defeated.Add(request.Participants[i].Square);
            }
            else
            {
                var pool = new List<LastStandParticipant>(request.Participants);
                for (int i = 0; i < killed; i++)
                {
                    int j = rng.Next(pool.Count);
                    defeated.Add(pool[j].Square);
                    pool.RemoveAt(j);
                }
            }

            result = LastStandResult.KingFalls(defeated);
        }

        Debug.Log($"[DebugLastStandResolver] {request} -> {result}");
        return result;
    }
}
