using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// TEMPORARY on-screen feedback (IMGUI, no scene setup) until the real chess UI exists:
///  - duel confirmation for the player's captures (attacker vs defender, estimated win chance)
///  - banners: AI attack, failed attack, Last Stand start/result
///  - persistent warning while a king is left in check (it can be captured)
/// Add it to any GameObject, assign the game manager, and drag it into
/// ChessGameManager.captureConfirmationComponent. Game-over is NOT shown here (event + log only).
/// </summary>
public sealed class ChessGameplayOverlay : MonoBehaviour, ICaptureConfirmation
{
    [SerializeField] private ChessGameManager game;

    [Header("Look")]
    [SerializeField] private int fontSize = 22;
    [SerializeField] private float bannerSeconds = 2.5f;

    [Header("Keys (duel confirmation)")]
    [SerializeField] private KeyCode confirmKey = KeyCode.Return;
    [SerializeField] private KeyCode cancelKey = KeyCode.Escape;

    private string banner;
    private Color bannerColor = Color.white;
    private float bannerUntil;

    private TaskCompletionSource<bool> pendingConfirm;
    private CombatRequest pendingRequest;
    private double pendingChance;
    private CancellationTokenRegistration pendingCancel;

    /// <summary>True while the duel confirmation is waiting for the player.</summary>
    public bool IsAskingConfirmation => pendingConfirm != null;

    /// <summary>Current banner text, or null (for tests / other UIs).</summary>
    public string CurrentBanner => Time.time <= bannerUntil ? banner : null;

    // ======================================================
    // LIFECYCLE
    // ======================================================

    private void OnEnable()
    {
        if (game == null)
            return;

        game.OnDuelStarting += HandleDuelStarting;
        game.OnAttackFailed += HandleAttackFailed;
        game.OnLastStandAnnounced += HandleLastStandAnnounced;
        game.OnLastStandFinished += HandleLastStandFinished;
    }

    private void OnDisable()
    {
        if (game != null)
        {
            game.OnDuelStarting -= HandleDuelStarting;
            game.OnAttackFailed -= HandleAttackFailed;
            game.OnLastStandAnnounced -= HandleLastStandAnnounced;
            game.OnLastStandFinished -= HandleLastStandFinished;
        }

        Answer(false); // never leave the game waiting on a hidden dialog
    }

    private void Update()
    {
        if (pendingConfirm == null)
            return;

        if (Input.GetKeyDown(confirmKey)) Answer(true);
        else if (Input.GetKeyDown(cancelKey)) Answer(false);
    }

    // ======================================================
    // DUEL CONFIRMATION (ICaptureConfirmation)
    // ======================================================

    public Task<bool> ConfirmCaptureAsync(CombatRequest request, double estimatedWinChance, CancellationToken ct)
    {
        // A disabled overlay never draws or receives keys: asking would wait forever.
        if (!isActiveAndEnabled)
        {
            Debug.LogWarning("ChessGameplayOverlay is disabled; the capture is confirmed automatically.", this);
            return Task.FromResult(true);
        }

        Answer(false); // a previous unanswered request is declined

        pendingRequest = request;
        pendingChance = estimatedWinChance;

        // Continuations run later on the main thread, NOT inline inside OnGUI / OnDisable.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingConfirm = tcs;
        pendingCancel = ct.Register(() =>
        {
            if (pendingConfirm == tcs)
                pendingConfirm = null; // close the dialog
            tcs.TrySetCanceled();
        });
        return tcs.Task;
    }

    /// <summary>Answers the pending duel confirmation (buttons, keys, tests).</summary>
    public void Answer(bool confirmed)
    {
        TaskCompletionSource<bool> tcs = pendingConfirm;
        if (tcs == null)
            return;

        pendingConfirm = null;
        pendingCancel.Dispose();
        tcs.TrySetResult(confirmed);
    }

    // ======================================================
    // EVENT HANDLERS
    // ======================================================

    private void HandleDuelStarting(CombatRequest r)
    {
        if (r.AttackerIsHuman)
            return;

        Show($"Enemy {r.AttackerType} attacks your {r.DefenderType}! Get ready for the duel.", new Color(1f, 0.35f, 0.3f));
    }

    private void HandleAttackFailed(CombatRequest r)
    {
        string who = r.AttackerIsHuman ? "Your" : "Enemy";
        Show($"Attack failed: {who} {r.AttackerType} lost the duel against the {r.DefenderType}. Turn lost.",
            new Color(1f, 0.6f, 0.2f));
    }

    private void HandleLastStandAnnounced(LastStandRequest r)
    {
        string whose = r.KingIsHuman ? "Your" : "Enemy";
        string how = r.Mode == LastStandMode.Simultaneous ? "all at once" : "one by one";
        Show($"CHECKMATE - LAST STAND! {whose} king fights {r.Participants.Count} piece(s) {how}.",
            new Color(1f, 0.8f, 0.1f), 6f);
    }

    private void HandleLastStandFinished(LastStandRequest r, LastStandResult res)
    {
        if (res.KingSurvived)
            Show("The king survived the Last Stand! The mating move is taken back.", new Color(0.4f, 1f, 0.5f));
        else
            Show($"The king fell after defeating {res.DefeatedSquares.Count} / {r.Participants.Count} piece(s).",
                new Color(1f, 0.8f, 0.1f), 6f);
    }

    private void Show(string text, Color color, float seconds = -1f)
    {
        banner = text;
        bannerColor = color;
        bannerUntil = Time.time + (seconds > 0f ? seconds : bannerSeconds);
    }

    /// <summary>Text of the "king can be captured" warning, or null.</summary>
    public string DangerText()
    {
        if (game == null || game.board == null || game.IsGameOver)
            return null;

        ChessPosition pos = game.board.Position;
        bool notToMove = !pos.WhiteToMove;
        if (!pos.IsInCheck(notToMove))
            return null;

        bool humanKing = notToMove != game.aiIsWhite;
        return humanKing
            ? "WARNING: your king is still in check - the enemy can capture it now!"
            : "The enemy king is exposed - you can capture it!";
    }

    // ======================================================
    // DRAW (IMGUI)
    // ======================================================

    private void OnGUI()
    {
        var label = new GUIStyle(GUI.skin.label) { fontSize = fontSize, alignment = TextAnchor.MiddleCenter, wordWrap = true };
        float w = Mathf.Min(900f, Screen.width - 40f);
        float x = (Screen.width - w) * 0.5f;

        string b = CurrentBanner;
        if (b != null)
        {
            GUI.color = bannerColor;
            GUI.Box(new Rect(x, 20f, w, 60f), GUIContent.none);
            GUI.Label(new Rect(x, 20f, w, 60f), b, label);
        }

        string danger = DangerText();
        if (danger != null)
        {
            GUI.color = new Color(1f, 0.3f, 0.3f);
            GUI.Box(new Rect(x, 90f, w, 44f), GUIContent.none);
            GUI.Label(new Rect(x, 90f, w, 44f), danger, label);
        }

        GUI.color = Color.white;

        if (pendingConfirm != null)
            DrawConfirmation(label);
    }

    private void DrawConfirmation(GUIStyle label)
    {
        float w = 520f, h = 220f;
        var r = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        GUI.Box(r, GUIContent.none);

        string text =
            $"DUEL: your {pendingRequest.AttackerType} attacks the enemy {pendingRequest.DefenderType}\n" +
            $"Estimated win chance: {pendingChance:P0}\n" +
            "Lose the duel and you lose your turn.";
        GUI.Label(new Rect(r.x + 10f, r.y + 10f, w - 20f, 130f), text, label);

        if (GUI.Button(new Rect(r.x + 30f, r.y + h - 60f, 200f, 44f), $"Fight ({confirmKey})"))
            Answer(true);
        if (GUI.Button(new Rect(r.x + w - 230f, r.y + h - 60f, 200f, 44f), $"Cancel ({cancelKey})"))
            Answer(false);
    }
}
