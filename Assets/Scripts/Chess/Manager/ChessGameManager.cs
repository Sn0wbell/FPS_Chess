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

    // ======================================================
    // UNITY
    // ======================================================

    private void Start()
    {
        _ = StartNewGameAsync();
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

            board.InitializeBoard();

            if (engine == null)
                engine = await TryStartStockfishAsync();

            EvaluateGameEnd();

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

    private async Task<IChessMoveEngine> TryStartStockfishAsync()
    {
        string exePath = Path.Combine(Application.streamingAssetsPath, stockfishExecutableName);
        var sf = new StockfishMoveEngine(depth, moveTimeMs);

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
            if (!await PlayTurnAsync(move, attackerIsHuman: true, ct))
                return false;

            if (IsGameOver)
                return true;

            await ExecuteAiTurnIfNeeded(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ======================================================
    // ONE TURN: DUEL (CAPTURES) -> COMMIT OR PASS -> RESULT
    // ======================================================

    private async Task<bool> PlayTurnAsync(ChessMove move, bool attackerIsHuman, CancellationToken ct)
    {
        bool done;

        if (move.IsCapture)
        {
            CombatRequest request = BuildCombatRequest(move, attackerIsHuman);
            CombatOutcome outcome = await ResolveCombatAsync(request, ct);
            OnCombatResolved?.Invoke(request, outcome);

            done = outcome == CombatOutcome.AttackerWins
                ? await board.CommitMoveAsync(move)
                : board.PassTurn();
        }
        else
        {
            done = await board.CommitMoveAsync(move);
        }

        if (!done)
        {
            Debug.LogError($"ChessGameManager: move {move} could not be applied. FEN: {board.GenerateFEN()}", this);
            return false;
        }

        EvaluateGameEnd();
        return true;
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

        if (await PlayTurnAsync(chosen.Value, attackerIsHuman: false, ct))
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
                    string uci = await engine.GetBestMoveAsync(fen, ct);
                    if (board.TryGetLegalMove(uci, out ChessMove move))
                        return move;

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

    private void EvaluateGameEnd()
    {
        if (IsGameOver)
            return;

        GameResult r = board.EvaluateResult();
        if (!r.IsOver)
            return;

        // NOTE (direction A): a checkmate will later start the king's Last Stand duel
        // instead of ending the game immediately.
        Result = r;
        Debug.Log($"Game Over: {r}. FEN: {board.GenerateFEN()}", this);
        OnGameOver?.Invoke(r);
    }
}
