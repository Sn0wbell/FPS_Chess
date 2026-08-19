using Chess.Engine;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

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

    // ======================================================
    // PUBLIC GAME STATE (AUTHORITATIVE)
    // ======================================================

    public bool IsBusy { get; private set; }
    public bool IsGameOver { get; private set; }

    public bool CanPlayerInput =>
        engineReady &&
        !IsBusy &&
        !IsGameOver &&
        board != null &&
        board.whiteToMove == !aiIsWhite;

    // ======================================================
    // INTERNAL
    // ======================================================

    private StockfishUCI engine;
    private bool engineReady;

    private int depth;
    private int moveTimeMs;
    private float aiDelay;

    private CancellationTokenSource lifetimeCts;

    // ======================================================
    // UNITY
    // ======================================================

    private void Start()
    {
        lifetimeCts = new CancellationTokenSource();
        _ = BootstrapAsync(lifetimeCts.Token);
    }

    private async Task BootstrapAsync(CancellationToken ct)
    {
        IsBusy = true;
        IsGameOver = false;
        engineReady = false;

        board.InitializeBoard();

        engine = new StockfishUCI();

        string exePath =
            Path.Combine(Application.streamingAssetsPath, stockfishExecutableName);

        try
        {
            await engine.StartEngineAsync(exePath);
            ConfigureDifficulty();
            engineReady = true;
        }
        catch (System.Exception ex)
        {
            Debug.LogError("Stockfish failed to start: " + ex);
            engineReady = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnApplicationQuit()
    {
        lifetimeCts?.Cancel();
        engine?.StopEngine();
    }

    // ======================================================
    // DIFFICULTY
    // ======================================================

    private void ConfigureDifficulty()
    {
        switch (difficulty)
        {
            case 1:
                depth = 4;
                moveTimeMs = 300;
                aiDelay = 6f;
                break;
            case 2:
                depth = 7;
                moveTimeMs = 800;
                aiDelay = 4f;
                break;
            case 3:
                depth = 10;
                moveTimeMs = 1500;
                aiDelay = 3f;
                break;
            case 4:
                depth = 14;
                moveTimeMs = 2500;
                aiDelay = 2f;
                break;
            default:
                depth = 20;
                moveTimeMs = 4000;
                aiDelay = 1.5f;
                break;
        }
    }

    // ======================================================
    // PLAYER ENTRY (THE ONLY ENTRY)
    // ======================================================

    public async Task<bool> OnPlayerMoveRequested(string uci)
    {
        if (!CanPlayerInput)
            return false;

        IsBusy = true;

        try
        {
            bool ok = await board.ApplyMove(uci);
            if (!ok)
                return false;

            await EvaluateGameTermination();
            if (IsGameOver)
                return true;

            await ExecuteAiTurnIfNeeded();
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ======================================================
    // AI FLOW (ENGINE-ONLY)
    // ======================================================

    private async Task ExecuteAiTurnIfNeeded()
    {
        if (IsGameOver || !engineReady)
            return;

        if (board.whiteToMove != aiIsWhite)
            return;

        CancellationToken ct = lifetimeCts.Token;

        try
        {
            if (aiDelay > 0f)
                await Task.Delay(
                    (int)(aiDelay * 1000),
                    ct);

            if (ct.IsCancellationRequested || IsGameOver)
                return;

            string fen = board.GenerateFEN();
            await engine.SendPositionAsync(fen);

            string bestMove =
                moveTimeMs > 0
                    ? await engine.GetBestMoveByTimeAsync(moveTimeMs)
                    : await engine.GetBestMoveByDepthAsync(depth);

            if (string.IsNullOrEmpty(bestMove) || bestMove == "(none)")
            {
                IsGameOver = true;
                Debug.Log("Game Over (engine reports no legal moves).");
                return;
            }

            await board.ApplyMove(bestMove);
            await EvaluateGameTermination();
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (System.Exception ex)
        {
            Debug.LogError("AI turn failed: " + ex);
        }
    }

    // ======================================================
    // GAME TERMINATION (ENGINE-TRUSTED)
    // ======================================================

    private async Task EvaluateGameTermination()
    {
        if (!engineReady || IsGameOver)
            return;

        string fen = board.GenerateFEN();
        await engine.SendPositionAsync(fen);

        string probe =
            moveTimeMs > 0
                ? await engine.GetBestMoveByTimeAsync(50)
                : await engine.GetBestMoveByDepthAsync(1);

        if (string.IsNullOrEmpty(probe) || probe == "(none)")
        {
            IsGameOver = true;
            Debug.Log("Game Over (checkmate or stalemate).");
        }
    }
}
