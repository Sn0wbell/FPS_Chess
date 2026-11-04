using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Turn flow: human move -> (FPS duel for captures) -> commit or pass -> game end check -> AI turn.
/// The rules core (ChessPosition via ChessBoardManager) decides legality and game results;
/// Stockfish only proposes moves for the AI side.
/// </summary>
public sealed class ChessGameManager : MonoBehaviour
{
    [Header("References")]
    public ChessBoardManager board;

    [Header("Stockfish")]
    public string stockfishExecutableName = "stockfish.exe";

    [Header("AI Settings")]
    [Range(1, 5)]
    public int difficulty = 3;

    [Tooltip("Is AI playing White side?")]
    public bool aiIsWhite = false;

    [Tooltip("Testing: a value >= 0 replaces the difficulty-based AI think delay (seconds).")]
    [SerializeField] private float aiMoveDelayOverride = -1f;

    [Header("Combat (Chess <-> FPS)")]
    [Tooltip("Component implementing ICombatResolver (e.g. DebugCombatResolver). " +
             "Empty = every capture succeeds immediately (plain chess).")]
    [SerializeField] private MonoBehaviour combatResolverComponent;

    [Tooltip("Component implementing ICaptureConfirmation (e.g. ChessGameplayOverlay). " +
             "Empty = human captures start their duel without asking.")]
    [SerializeField] private MonoBehaviour captureConfirmationComponent;

    [Tooltip("Seconds the AI's attack is shown (attacker/defender outlined) before its duel starts.")]
    [Min(0f)]
    [SerializeField] private float aiDuelIntroSeconds = 1.5f;

    [Tooltip("Seconds the board pauses after a lost duel so the failed attack can be seen.")]
    [Min(0f)]
    [SerializeField] private float failedAttackPauseSeconds = 1.5f;

    [Header("AI Duel Awareness")]
    [Tooltip("When the engine's move is a capture, weigh it by the chance of winning the duel.")]
    [SerializeField] private bool aiConsidersDuelOdds = true;

    [Tooltip("Number of engine candidate lines (Stockfish MultiPV) compared against a risky capture.")]
    [Range(1, 8)]
    [SerializeField] private int aiCandidateMoves = 4;

    [Tooltip("FPS strength per piece type: Pawn, Rook, Knight, Bishop, Queen, King. " +
             "Duel win chance = attacker / (attacker + defender).")]
    [SerializeField] private float[] duelStrength = { 1f, 5f, 3f, 3f, 9f, 4f };

    [Header("Last Stand (checkmate)")]
    [Tooltip("Component implementing ILastStandResolver (e.g. DebugLastStandResolver). " +
             "Empty = the mated king always falls (plain checkmate).")]
    [SerializeField] private MonoBehaviour lastStandResolverComponent;

    [Tooltip("Seconds the mating position stays frozen, participants highlighted, before the Last Stand starts.")]
    [Min(0f)]
    [SerializeField] private float lastStandIntroSeconds = 3f;

    [Tooltip("Seconds to look back at the board after a lost Last Stand before the game-over is raised.")]
    [Min(0f)]
    [SerializeField] private float lastStandRecapSeconds = 4f;

    // ======================================================
    // PUBLIC GAME STATE (AUTHORITATIVE)
    // ======================================================

    public bool IsBusy { get; private set; }

    public GameResult Result { get; private set; } = GameResult.Ongoing;

    public bool IsGameOver => Result.IsOver;

    /// <summary>Raised once when the game ends (checkmate, king captured or a draw).</summary>
    public event Action<GameResult> OnGameOver;

    /// <summary>Raised after every capture duel, before the board is updated.</summary>
    public event Action<CombatRequest, CombatOutcome> OnCombatResolved;

    /// <summary>A duel is about to start (after confirmation for humans, after the intro for the AI).</summary>
    public event Action<CombatRequest> OnDuelStarting;

    /// <summary>The attacker lost its duel: nothing moved, it lost the turn. UI: "Attack failed".</summary>
    public event Action<CombatRequest> OnAttackFailed;

    /// <summary>A move was played on the board (quiet move or won capture).</summary>
    public event Action<ChessMove> OnMovePlayed;

    /// <summary>The side to move is in check (raised before a checkmate's Last Stand too).</summary>
    public event Action<bool> OnCheck;

    /// <summary>The side NOT to move is in check: its king can be captured now (direction A).</summary>
    public event Action<bool> OnKingEndangered;

    /// <summary>Checkmate on the board: raised at the start of the freeze-frame (UI: "Last Stand!").</summary>
    public event Action<LastStandRequest> OnLastStandAnnounced;

    /// <summary>Last Stand over (board already updated). UI: "defeated X / Y".</summary>
    public event Action<LastStandRequest, LastStandResult> OnLastStandFinished;

    public bool CanPlayerInput =>
        !IsBusy &&
        !IsGameOver &&
        board != null &&
        board.whiteToMove != aiIsWhite;

    // ======================================================
    // INTERNAL
    // ======================================================

    private IChessMoveEngine engine;
    private ICombatResolver combatResolver;
    private ILastStandResolver lastStandResolver;
    private ICaptureConfirmation captureConfirmation;
    private DuelOdds duelOdds;
    private CancellationTokenSource lifetimeCts;

    private int skillLevel;
    private int depth;
    private int moveTimeMs;
    private float aiDelay;

    private readonly List<ChessMove> moveBuffer = new List<ChessMove>(64);

    /// <summary>Replaces the AI move source (tests, alternative AIs). Call before StartNewGameAsync.</summary>
    public void SetEngine(IChessMoveEngine moveEngine) => engine = moveEngine;

    /// <summary>Replaces the duel resolver (FPS arena, debug, tests).</summary>
    public void SetCombatResolver(ICombatResolver resolver) => combatResolver = resolver;

    /// <summary>Replaces the Last Stand resolver (FPS arena, debug, tests).</summary>
    public void SetLastStandResolver(ILastStandResolver resolver) => lastStandResolver = resolver;

    /// <summary>Replaces the capture confirmation (UI, tests).</summary>
    public void SetCaptureConfirmation(ICaptureConfirmation confirmation) => captureConfirmation = confirmation;

    /// <summary>Estimated chance that the attacker of this duel wins (tunable duelStrength).</summary>
    public double EstimateDuelWinChance(CombatRequest request) =>
        Odds.AttackerWinChance(request.AttackerType, request.DefenderType);

    private DuelOdds Odds => duelOdds ?? (duelOdds = new DuelOdds(duelStrength));

    /// <summary>Event subscribers (UI, audio, arena) must never break the turn flow.</summary>
    private void Raise(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            Debug.LogError("ChessGameManager: an event handler threw: " + ex, this);
        }
    }

    // ======================================================
    // UNITY
    // ======================================================

    [Tooltip("Seconds between attempts to restart a stalled AI turn (after an unexpected error).")]
    [Min(0.1f)]
    [SerializeField] private float aiWatchdogSeconds = 1f;

    private float nextWatchdogTime;

    private void Start()
    {
        _ = StartNewGameAsync();
    }

    // Safety net: if an unexpected error ever ended a turn early, it would be the AI's turn
    // with nothing running it (the player locked out). Restart the AI turn in that case.
    private void Update()
    {
        if (IsBusy || IsGameOver || board == null || board.whiteToMove != aiIsWhite || lifetimeCts == null)
            return;

        if (Time.time < nextWatchdogTime)
            return;

        nextWatchdogTime = Time.time + aiWatchdogSeconds;
        Debug.LogWarning("ChessGameManager: AI turn was not running; restarting it.", this);
        _ = RunAiTurnGuardedAsync();
    }

    /// <summary>Runs the AI turn with the busy flag and full error handling.</summary>
    public async Task RunAiTurnGuardedAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            await ExecuteAiTurnIfNeeded(lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            Debug.LogError("AI turn failed: " + ex, this);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnApplicationQuit() => Shutdown();

    private void OnDestroy() => Shutdown();

    private void Shutdown()
    {
        lifetimeCts?.Cancel();
        engine?.Dispose();
        engine = null;
    }

    // ======================================================
    // BOOTSTRAP
    // ======================================================

    public async Task StartNewGameAsync()
    {
        lifetimeCts?.Cancel();
        lifetimeCts = new CancellationTokenSource();
        CancellationToken ct = lifetimeCts.Token;

        IsBusy = true;
        Result = GameResult.Ongoing;

        try
        {
            ConfigureDifficulty();

            if (combatResolver == null)
                combatResolver = ResolveCombatComponent();

            if (lastStandResolver == null)
                lastStandResolver = ResolveLastStandComponent();

            if (captureConfirmation == null && captureConfirmationComponent != null)
            {
                captureConfirmation = captureConfirmationComponent as ICaptureConfirmation;
                if (captureConfirmation == null)
                    Debug.LogError($"ChessGameManager: '{captureConfirmationComponent.GetType().Name}' does not implement ICaptureConfirmation.", this);
            }

            duelOdds = new DuelOdds(duelStrength);

            board.InitializeBoard();

            if (engine == null)
                engine = await TryStartStockfishAsync();

            await EvaluateGameEndAsync(ct);

            // Fix: when the AI plays White it must make the first move.
            await ExecuteAiTurnIfNeeded(ct);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            Debug.LogError("Game bootstrap failed: " + ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private ICombatResolver ResolveCombatComponent()
    {
        if (combatResolverComponent == null)
            return new AttackerAlwaysWinsResolver();

        if (combatResolverComponent is ICombatResolver r)
            return r;

        Debug.LogError(
            $"ChessGameManager: '{combatResolverComponent.GetType().Name}' does not implement ICombatResolver. " +
            "Captures will always succeed.", this);
        return new AttackerAlwaysWinsResolver();
    }

    private ILastStandResolver ResolveLastStandComponent()
    {
        if (lastStandResolverComponent == null)
            return new KingAlwaysFallsResolver();

        if (lastStandResolverComponent is ILastStandResolver r)
            return r;

        Debug.LogError(
            $"ChessGameManager: '{lastStandResolverComponent.GetType().Name}' does not implement ILastStandResolver. " +
            "Checkmate will end the game directly.", this);
        return new KingAlwaysFallsResolver();
    }

    private async Task<IChessMoveEngine> TryStartStockfishAsync()
    {
        string exePath = Path.Combine(Application.streamingAssetsPath, stockfishExecutableName);
        var sf = new StockfishMoveEngine(depth, moveTimeMs, aiConsidersDuelOdds ? aiCandidateMoves : 1);

        try
        {
            await sf.StartAsync(exePath, skillLevel);
            return sf;
        }
        catch (Exception ex)
        {
            Debug.LogError("Stockfish failed to start, the AI will play fallback moves: " + ex);
            sf.Dispose();
            return null;
        }
    }

    // ======================================================
    // DIFFICULTY
    // ======================================================

    private void ConfigureDifficulty()
    {
        // Search stops at depth OR time, whichever comes first; Skill Level weakens play.
        switch (difficulty)
        {
            case 1: skillLevel = 0; depth = 4; moveTimeMs = 300; aiDelay = 6f; break;
            case 2: skillLevel = 5; depth = 7; moveTimeMs = 800; aiDelay = 4f; break;
            case 3: skillLevel = 10; depth = 10; moveTimeMs = 1500; aiDelay = 3f; break;
            case 4: skillLevel = 15; depth = 14; moveTimeMs = 2500; aiDelay = 2f; break;
            default: skillLevel = 20; depth = 20; moveTimeMs = 4000; aiDelay = 1.5f; break;
        }
    }

    // ======================================================
    // PLAYER ENTRY (THE ONLY ENTRY)
    // ======================================================

    /// <summary>
    /// Returns true when the move was accepted (even if its capture duel was lost).
    /// A promotion without a piece letter becomes a queen.
    /// </summary>
    public async Task<bool> OnPlayerMoveRequested(string uci)
    {
        if (!CanPlayerInput)
            return false;

        if (!board.TryGetLegalMove(uci, out ChessMove move))
            return false;

        IsBusy = true;
        CancellationToken ct = lifetimeCts != null ? lifetimeCts.Token : CancellationToken.None;

        try
        {
            if (await PlayTurnAsync(move, attackerIsHuman: true, ct) != TurnResult.Played)
                return false; // cancelled at the duel confirmation, or an error

            if (IsGameOver)
                return true;

            await ExecuteAiTurnIfNeeded(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // The board may already have changed; the watchdog restarts the AI turn if needed.
            Debug.LogError($"Turn failed after {uci}: {ex}", this);
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ======================================================
    // ONE TURN: DUEL (CAPTURES) -> COMMIT OR PASS -> RESULT
    // ======================================================

    private enum TurnResult
    {
        Played,
        Cancelled, // human declined the duel: nothing happened, still their turn
        Error
    }

    private async Task<TurnResult> PlayTurnAsync(ChessMove move, bool attackerIsHuman, CancellationToken ct)
    {
        bool done;
        bool moved = false;

        if (move.IsCapture)
        {
            CombatRequest request = BuildCombatRequest(move, attackerIsHuman);

            // Never throw the player into the FPS arena without warning.
            board.ShowDuelPreview(request);
            try
            {
                if (attackerIsHuman)
                {
                    if (!await ConfirmCaptureAsync(request, ct))
                        return TurnResult.Cancelled;
                }
                else
                {
                    Raise(() => OnDuelStarting?.Invoke(request));
                    await DelaySeconds(aiDuelIntroSeconds, ct);
                }
            }
            finally
            {
                board.ClearDuelPreview();
            }

            if (attackerIsHuman)
                Raise(() => OnDuelStarting?.Invoke(request));

            CombatOutcome outcome = await ResolveCombatAsync(request, ct);
            Raise(() => OnCombatResolved?.Invoke(request, outcome));

            if (outcome == CombatOutcome.AttackerWins)
            {
                done = await board.CommitMoveAsync(move);
                moved = done;
            }
            else
            {
                done = board.PassTurn();
                if (done)
                {
                    board.ShowFailedAttack(request);
                    Debug.Log($"Attack failed: {request}. The attacker loses its turn.", this);
                    Raise(() => OnAttackFailed?.Invoke(request));
                    await DelaySeconds(failedAttackPauseSeconds, ct);
                }
            }
        }
        else
        {
            done = await board.CommitMoveAsync(move);
            moved = done;
        }

        if (!done)
        {
            Debug.LogError($"ChessGameManager: move {move} could not be applied. FEN: {board.GenerateFEN()}", this);
            return TurnResult.Error;
        }

        if (moved)
            Raise(() => OnMovePlayed?.Invoke(move));

        RaiseCheckEvents();
        await EvaluateGameEndAsync(ct);
        return TurnResult.Played;
    }

    private async Task<bool> ConfirmCaptureAsync(CombatRequest request, CancellationToken ct)
    {
        if (captureConfirmation == null)
            return true;

        try
        {
            return await captureConfirmation.ConfirmCaptureAsync(request, EstimateDuelWinChance(request), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Capture confirmation failed; the capture is cancelled. {ex}", this);
            return false;
        }
    }

    private void RaiseCheckEvents()
    {
        ChessPosition pos = board.Position;
        bool side = pos.WhiteToMove;

        if (pos.IsInCheck(!side))
        {
            Debug.Log($"{(side ? "Black" : "White")} king is in check after a lost duel and can be captured!", this);
            Raise(() => OnKingEndangered?.Invoke(!side));
        }

        if (pos.IsInCheck(side))
            Raise(() => OnCheck?.Invoke(side));
    }

    private CombatRequest BuildCombatRequest(ChessMove move, bool attackerIsHuman)
    {
        ChessPosition pos = board.Position;
        BoardPiece attacker = pos.GetPiece(move.From);

        int defenderSquare = move.IsEnPassant
            ? ChessPosition.Square(move.ToFile, move.FromRank)
            : move.To;
        BoardPiece defender = pos.GetPiece(defenderSquare);

        return new CombatRequest(
            move,
            attacker.Type, attacker.IsWhite, attackerIsHuman,
            defender.Type, !attackerIsHuman,
            defenderSquare);
    }

    private async Task<CombatOutcome> ResolveCombatAsync(CombatRequest request, CancellationToken ct)
    {
        if (combatResolver == null)
            return CombatOutcome.AttackerWins;

        try
        {
            return await combatResolver.ResolveAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Combat resolver failed for {request}; treating it as an attacker win. {ex}", this);
            return CombatOutcome.AttackerWins;
        }
    }

    // ======================================================
    // AI FLOW
    // ======================================================

    private async Task ExecuteAiTurnIfNeeded(CancellationToken ct)
    {
        if (IsGameOver || board.whiteToMove != aiIsWhite)
            return;

        float delay = aiMoveDelayOverride >= 0f ? aiMoveDelayOverride : aiDelay;
        if (delay > 0f)
            await Task.Delay((int)(delay * 1000), ct);

        if (IsGameOver)
            return;

        ChessMove? chosen;

        // Direction A: a king left in check after a lost duel can be captured.
        // Stockfish cannot handle that position, so the AI takes the king itself.
        if (TryPickKingCapture(out ChessMove kingCapture))
            chosen = kingCapture;
        else
            chosen = await ChooseEngineMoveAsync(ct);

        if (!chosen.HasValue)
        {
            Debug.LogError($"AI has no legal move but the game is not over. FEN: {board.GenerateFEN()}", this);
            return;
        }

        if (await PlayTurnAsync(chosen.Value, attackerIsHuman: false, ct) == TurnResult.Played)
            return;

        // Fix: never leave the turn stuck on a rejected AI move.
        if (TryPickFallbackMove(out ChessMove fallback) && !fallback.Equals(chosen.Value))
        {
            Debug.LogWarning($"AI move {chosen.Value} failed, playing fallback {fallback}.", this);
            await PlayTurnAsync(fallback, attackerIsHuman: false, ct);
        }
    }

    private async Task<ChessMove?> ChooseEngineMoveAsync(CancellationToken ct)
    {
        string fen = board.GenerateFEN();

        if (engine != null && engine.IsReady)
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    EngineAnalysis analysis = await engine.AnalyseAsync(fen, ct);
                    string uci = analysis.BestMove;
                    if (board.TryGetLegalMove(uci, out ChessMove move))
                    {
                        if (aiConsidersDuelOdds && move.IsCapture)
                            move = await WeighDuelRiskAsync(move, analysis, ct);
                        return move;
                    }

                    Debug.LogWarning($"[AI] Engine move '{uci}' is not legal (attempt {attempt}). FEN: {fen}", this);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AI] Engine query failed (attempt {attempt}): {ex.Message}. FEN: {fen}", this);
                }
            }
        }

        return TryPickFallbackMove(out ChessMove fb) ? fb : (ChessMove?)null;
    }

    /// <summary>
    /// The engine wants a capture, but the duel can be lost (then the AI only loses its turn).
    /// Compare its expected value with the other candidate lines; keep it unless one is better.
    /// </summary>
    private async Task<ChessMove> WeighDuelRiskAsync(ChessMove engineMove, EngineAnalysis analysis, CancellationToken ct)
    {
        ChessPosition pos = board.Position;
        bool ai = pos.WhiteToMove;

        // Score of the engine move and of the other legal candidates (AI's point of view).
        int engineScore = int.MinValue;
        var others = new List<DuelRiskSelector.Option>(analysis.Candidates.Count);
        foreach (EngineCandidate c in analysis.Candidates)
        {
            if (!board.TryGetLegalMove(c.Uci, out ChessMove m))
                continue;
            if (m.Equals(engineMove))
                engineScore = c.Score;
            else
                others.Add(new DuelRiskSelector.Option(m, c.Score, WinChance(pos, m)));
        }

        if (others.Count == 0)
            return engineMove; // nothing to compare with

        if (engineScore == int.MinValue)
            engineScore = analysis.Candidates[0].Score; // Skill Level may pick a move outside the top lines

        // Losing the duel = a pass. If the AI is in check, a pass leaves its king capturable.
        int failScore;
        if (pos.IsInCheck(ai))
        {
            failScore = -EngineCandidate.MateScore;
        }
        else
        {
            ChessPosition passed = pos.Clone();
            passed.PassTurn();
            try
            {
                EngineAnalysis after = await engine.AnalyseAsync(passed.ToFen(), ct);
                failScore = after.Candidates.Count > 0 ? -after.Candidates[0].Score : 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AI] Could not evaluate a lost duel ({ex.Message}); keeping {engineMove}.", this);
                return engineMove;
            }
        }

        var engineOption = new DuelRiskSelector.Option(engineMove, engineScore, WinChance(pos, engineMove));
        ChessMove choice = DuelRiskSelector.Choose(engineOption, others, failScore);

        if (!choice.Equals(engineMove))
            Debug.Log($"[AI] Avoids risky duel {engineMove} (win chance {engineOption.WinChance:P0}), plays {choice} instead.", this);

        return choice;
    }

    private double WinChance(ChessPosition pos, ChessMove m)
    {
        if (!m.IsCapture)
            return 1.0;

        int defenderSquare = m.IsEnPassant ? ChessPosition.Square(m.ToFile, m.FromRank) : m.To;
        return Odds.AttackerWinChance(pos.GetPiece(m.From).Type, pos.GetPiece(defenderSquare).Type);
    }

    private bool TryPickKingCapture(out ChessMove move)
    {
        move = default;
        board.GetLegalMoves(moveBuffer);

        int best = -1;
        foreach (ChessMove m in moveBuffer)
        {
            if (!m.IsKingCapture)
                continue;

            // Send the strongest attacker into the duel.
            int value = PieceValue(board.Position.GetPiece(m.From).Type);
            if (value > best)
            {
                best = value;
                move = m;
            }
        }
        return best >= 0;
    }

    /// <summary>Emergency move when the engine is missing or keeps proposing illegal moves.</summary>
    private bool TryPickFallbackMove(out ChessMove move)
    {
        board.GetLegalMoves(moveBuffer);
        move = default;

        if (moveBuffer.Count == 0)
            return false;

        foreach (ChessMove m in moveBuffer)
        {
            if (m.IsCapture)
            {
                move = m;
                return true;
            }
        }

        move = moveBuffer[0];
        return true;
    }

    private static int PieceValue(PieceType t)
    {
        switch (t)
        {
            case PieceType.Queen: return 9;
            case PieceType.Rook: return 5;
            case PieceType.Bishop:
            case PieceType.Knight: return 3;
            case PieceType.Pawn: return 1;
            default: return 0;
        }
    }

    // ======================================================
    // GAME END (RULES CORE IS AUTHORITATIVE)
    // ======================================================

    private async Task EvaluateGameEndAsync(CancellationToken ct)
    {
        if (IsGameOver)
            return;

        GameResult r = board.EvaluateResult();
        if (!r.IsOver)
            return;

        // Direction A: checkmate -> the king's Last Stand. Needs the mating move to take back;
        // a mate with nothing to take back (start FEN, or mate exposed by a take-back) is final.
        if (r.Reason == GameEndReason.Checkmate && board.CanRevertLastMove)
        {
            if (await RunLastStandAsync(ct))
            {
                // King survived: mating move taken back, mating side passed. Re-check
                // (e.g. the 50-move rule or a mate exposed by the take-back).
                await EvaluateGameEndAsync(ct);
                return;
            }
        }

        EndGame(r);
    }

    private void EndGame(GameResult r)
    {
        Result = r;
        Debug.Log($"Game Over: {r}. FEN: {board.GenerateFEN()}", this);
        Raise(() => OnGameOver?.Invoke(r));
    }

    /// <summary>Returns true when the king survived (the game goes on).</summary>
    private async Task<bool> RunLastStandAsync(CancellationToken ct)
    {
        bool kingIsHuman = board.whiteToMove != aiIsWhite;
        LastStandRequest request = board.BuildLastStandRequest(kingIsHuman);

        // 1. Freeze-frame: show who takes part in the mate, give the player time to read it.
        board.ShowLastStandPreview(request);
        Debug.Log(request.ToString(), this);
        Raise(() => OnLastStandAnnounced?.Invoke(request));
        await DelaySeconds(lastStandIntroSeconds, ct);

        // 2. The fight.
        LastStandResult result = SanitizeLastStandResult(request, await ResolveLastStandAsync(request, ct));
        board.ClearLastStandPreview();

        // 3a. King survived: take the mating move back; the mating side loses its turn.
        if (result.KingSurvived)
        {
            if (!await board.RevertLastMoveAsPassAsync())
            {
                // Never loop: without a take-back the same mate would start another Last Stand.
                Debug.LogError($"Last Stand won but the mating move could not be taken back; ending as checkmate. FEN: {board.GenerateFEN()}", this);
                Raise(() => OnLastStandFinished?.Invoke(request, result));
                return false;
            }

            Debug.Log($"Last Stand: king survived, mating move taken back. FEN: {board.GenerateFEN()}", this);
            Raise(() => OnLastStandFinished?.Invoke(request, result));
            RaiseCheckEvents(); // e.g. the mating side's own king may now be capturable
            return true;
        }

        // 3b. King fell: remove the pieces it took down, then let the player look back.
        await board.RemoveDefeatedPiecesAsync(result.DefeatedSquares);
        board.ShowLastStandAftermath(request, result);
        Debug.Log($"Last Stand: king fell after defeating {result.DefeatedSquares.Count}/{request.Participants.Count}.", this);
        Raise(() => OnLastStandFinished?.Invoke(request, result));
        await DelaySeconds(lastStandRecapSeconds, ct);
        return false;
    }

    private async Task<LastStandResult> ResolveLastStandAsync(LastStandRequest request, CancellationToken ct)
    {
        if (lastStandResolver == null)
            return LastStandResult.KingFalls(new int[0]);

        try
        {
            return await lastStandResolver.ResolveAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Last Stand resolver failed; treating it as a lost Last Stand. {ex}", this);
            return LastStandResult.KingFalls(new int[0]);
        }
    }

    /// <summary>Keeps only real, distinct participant squares; a surviving king defeated everyone.</summary>
    private LastStandResult SanitizeLastStandResult(LastStandRequest request, LastStandResult result)
    {
        if (result == null)
            return LastStandResult.KingFalls(new int[0]);

        if (result.KingSurvived)
            return LastStandResult.KingWins(request);

        var valid = new List<int>(result.DefeatedSquares.Count);
        foreach (int sq in result.DefeatedSquares)
        {
            bool isParticipant = false;
            foreach (LastStandParticipant p in request.Participants)
                if (p.Square == sq) { isParticipant = true; break; }

            if (isParticipant && !valid.Contains(sq))
                valid.Add(sq);
            else
                Debug.LogWarning($"Last Stand result names {ChessPosition.SquareName(sq)}, which is not a participant; ignored.", this);
        }
        return LastStandResult.KingFalls(valid);
    }

    private static Task DelaySeconds(float seconds, CancellationToken ct) =>
        seconds > 0f ? Task.Delay((int)(seconds * 1000), ct) : Task.CompletedTask;
}
