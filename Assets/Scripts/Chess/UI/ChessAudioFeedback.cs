using UnityEngine;

/// <summary>
/// Sound cues for chess events. Every clip is optional (empty = silent).
/// Add it next to an AudioSource and assign the game manager.
/// </summary>
public sealed class ChessAudioFeedback : MonoBehaviour
{
    [SerializeField] private ChessGameManager game;
    [SerializeField] private AudioSource source;

    [Range(0f, 1f)]
    [SerializeField] private float volume = 1f;

    [Header("Board")]
    [SerializeField] private AudioClip move;
    [SerializeField] private AudioClip capture;
    [SerializeField] private AudioClip check;
    [SerializeField] private AudioClip kingInDanger;

    [Header("Duels")]
    [SerializeField] private AudioClip duelStart;
    [SerializeField] private AudioClip attackFailed;

    [Header("Last Stand / End")]
    [SerializeField] private AudioClip lastStandStart;
    [SerializeField] private AudioClip lastStandWon;
    [SerializeField] private AudioClip lastStandLost;
    [SerializeField] private AudioClip gameOver;

    private void Awake()
    {
        if (source == null)
            source = GetComponent<AudioSource>();
    }

    private void OnEnable()
    {
        if (game == null)
            return;

        game.OnMovePlayed += HandleMove;
        game.OnCheck += HandleCheck;
        game.OnKingEndangered += HandleKingInDanger;
        game.OnDuelStarting += HandleDuelStarting;
        game.OnAttackFailed += HandleAttackFailed;
        game.OnLastStandAnnounced += HandleLastStandAnnounced;
        game.OnLastStandFinished += HandleLastStandFinished;
        game.OnGameOver += HandleGameOver;
    }

    private void OnDisable()
    {
        if (game == null)
            return;

        game.OnMovePlayed -= HandleMove;
        game.OnCheck -= HandleCheck;
        game.OnKingEndangered -= HandleKingInDanger;
        game.OnDuelStarting -= HandleDuelStarting;
        game.OnAttackFailed -= HandleAttackFailed;
        game.OnLastStandAnnounced -= HandleLastStandAnnounced;
        game.OnLastStandFinished -= HandleLastStandFinished;
        game.OnGameOver -= HandleGameOver;
    }

    private void HandleMove(ChessMove m) => Play(m.IsCapture ? capture : move);
    private void HandleCheck(bool kingIsWhite) => Play(check);
    private void HandleKingInDanger(bool kingIsWhite) => Play(kingInDanger);
    private void HandleDuelStarting(CombatRequest r) => Play(duelStart);
    private void HandleAttackFailed(CombatRequest r) => Play(attackFailed);
    private void HandleLastStandAnnounced(LastStandRequest r) => Play(lastStandStart);
    private void HandleLastStandFinished(LastStandRequest r, LastStandResult res) => Play(res.KingSurvived ? lastStandWon : lastStandLost);
    private void HandleGameOver(GameResult r) => Play(gameOver);

    private void Play(AudioClip clip)
    {
        if (source != null && clip != null)
            source.PlayOneShot(clip, volume);
    }
}
