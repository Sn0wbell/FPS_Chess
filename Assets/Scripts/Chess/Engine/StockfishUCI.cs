// =========================
// FILE: StockfishUCI.cs
// DESCRIPTION: UCI wrapper for Stockfish to use from Unity (async-friendly).
// NAMESPACE: Chess.Engine
// =========================

using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.IO;

namespace Chess.Engine
{
    /// <summary>
    /// Robust UCI wrapper for Stockfish.
    /// Thread-safe at API level (single search at a time).
    /// </summary>
    public sealed class StockfishUCI : IDisposable
    {
        private Process _process;

        private readonly ConcurrentQueue<string> _outputQueue =
            new ConcurrentQueue<string>();

        private readonly StringBuilder _logBuffer = new StringBuilder();

        private readonly object _ioLock = new object();
        private readonly object _stateLock = new object();

        private TaskCompletionSource<string> _bestMoveTcs;
        private TaskCompletionSource<bool> _uciOkTcs;
        private TaskCompletionSource<bool> _readyOkTcs;

        private CancellationTokenSource _searchCts;
        private bool _searchInProgress;
        private bool _isStopping;

        private readonly SynchronizationContext _syncContext;

        public bool IsRunning =>
            _process != null && !_process.HasExited;

        public string LastLog
        {
            get
            {
                lock (_logBuffer)
                {
                    return _logBuffer.ToString();
                }
            }
        }

        /// <summary>
        /// Fired for each output line from engine.
        /// Dispatched on captured SynchronizationContext if available.
        /// </summary>
        public event Action<string> OnEngineOutputLine;

        public StockfishUCI()
        {
            _syncContext = SynchronizationContext.Current;
        }

        // ============================================================
        // LIFECYCLE
        // ============================================================

        public async Task StartEngineAsync(string path, int startTimeoutMs = 5000)
        {
            if (IsRunning)
                return;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException(
                    "Stockfish executable not found", path);

            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            _process = new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true
            };

            _process.OutputDataReceived += (s, e) => OnOutputLine(e.Data);
            _process.ErrorDataReceived += (s, e) => OnOutputLine(e.Data);

            if (!_process.Start())
                throw new InvalidOperationException(
                    "Failed to start engine process.");

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // UCI handshake
            _uciOkTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            SendRawCommand("uci");

            await WaitForTcsAsync(
                _uciOkTcs,
                startTimeoutMs,
                "uciok");

            await SendReadyAsync();
        }

        public void StopEngine()
        {
            lock (_stateLock)
            {
                _isStopping = true;
                _searchCts?.Cancel();
                _searchInProgress = false;
            }

            try
            {
                if (_process != null && !_process.HasExited)
                {
                    try { SendRawCommand("quit"); } catch { }

                    if (!_process.WaitForExit(300))
                    {
                        try { _process.Kill(); } catch { }
                    }
                }
            }
            catch { }
            finally
            {
                _process?.Dispose();
                _process = null;
            }
        }

        public void Dispose()
        {
            StopEngine();
        }

        // ============================================================
        // UCI CORE API
        // ============================================================

        public Task SetOptionAsync(string name, object value)
        {
            EnsureRunning();
            SendRawCommand($"setoption name {name} value {value}");
            return Task.CompletedTask;
        }

        public async Task SendReadyAsync(int timeoutMs = 3000)
        {
            EnsureRunning();

            _readyOkTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            SendRawCommand("isready");

            await WaitForTcsAsync(
                _readyOkTcs,
                timeoutMs,
                "readyok");
        }

        public async Task SendPositionAsync(string fenOrStartposOrMoves)
        {
            EnsureRunning();

            if (string.IsNullOrWhiteSpace(fenOrStartposOrMoves))
                throw new ArgumentException("Empty position");

            string payload = fenOrStartposOrMoves.Trim();
            string cmd;

            if (payload.StartsWith("startpos", StringComparison.OrdinalIgnoreCase) ||
                payload.StartsWith("fen ", StringComparison.OrdinalIgnoreCase))
            {
                cmd = "position " + payload;
            }
            else
            {
                cmd = "position fen " + payload;
            }

            SendRawCommand(cmd);
            await SendReadyAsync();
        }

        public Task SendMovesAsync(string moves)
        {
            EnsureRunning();
            SendRawCommand("position startpos moves " + moves);
            return Task.CompletedTask;
        }

        public Task<string> GetBestMoveByDepthAsync(
            int depth,
            int timeoutMs = 60000)
        {
            if (depth <= 0)
                throw new ArgumentOutOfRangeException(nameof(depth));

            return StartSearchAsync($"go depth {depth}", timeoutMs);
        }

        public Task<string> GetBestMoveByTimeAsync(
            int movetimeMs,
            int timeoutMs = 60000)
        {
            if (movetimeMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(movetimeMs));

            return StartSearchAsync($"go movetime {movetimeMs}", timeoutMs);
        }

        // ============================================================
        // SEARCH CONTROL
        // ============================================================

        private async Task<string> StartSearchAsync(
            string goCommand,
            int timeoutMs)
        {
            EnsureRunning();

            lock (_stateLock)
            {
                if (_searchInProgress)
                    throw new InvalidOperationException(
                        "Search already in progress");

                _searchInProgress = true;
                _isStopping = false;

                _searchCts = new CancellationTokenSource(timeoutMs);

                _bestMoveTcs = new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            SendRawCommand(goCommand);

            using (_searchCts.Token.Register(() =>
            {
                _bestMoveTcs?.TrySetCanceled();
                try { SendRawCommand("stop"); } catch { }
            }))
            {
                try
                {
                    return await _bestMoveTcs.Task.ConfigureAwait(false);
                }
                finally
                {
                    lock (_stateLock)
                    {
                        _searchInProgress = false;
                        _searchCts?.Dispose();
                        _searchCts = null;
                        _bestMoveTcs = null;
                    }
                }
            }
        }

        // ============================================================
        // OUTPUT HANDLING
        // ============================================================

        private void OnOutputLine(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;

            lock (_logBuffer)
            {
                _logBuffer.AppendLine(line);
            }

            _outputQueue.Enqueue(line);

            try
            {
                if (OnEngineOutputLine != null)
                {
                    if (_syncContext != null)
                        _syncContext.Post(_ =>
                            OnEngineOutputLine?.Invoke(line), null);
                    else
                        OnEngineOutputLine.Invoke(line);
                }
            }
            catch
            {
                // Never allow user event to break engine loop
            }

            string trimmed = line.Trim();

            if (trimmed.Equals("uciok", StringComparison.OrdinalIgnoreCase))
            {
                _uciOkTcs?.TrySetResult(true);
                return;
            }

            if (trimmed.Equals("readyok", StringComparison.OrdinalIgnoreCase))
            {
                _readyOkTcs?.TrySetResult(true);
                return;
            }

            if (trimmed.StartsWith("bestmove ",
                StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split(
                    new[] { ' ' },
                    StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 2)
                {
                    _bestMoveTcs?.TrySetResult(parts[1]);
                }
                else
                {
                    _bestMoveTcs?.TrySetException(
                        new FormatException(
                            "Malformed bestmove line: " + trimmed));
                }
            }
        }

        // ============================================================
        // IO HELPERS
        // ============================================================

        private void SendRawCommand(string cmd)
        {
            EnsureRunning();

            lock (_ioLock)
            {
                if (_process == null || _process.HasExited)
                    return;

                _process.StandardInput.WriteLine(cmd);
                _process.StandardInput.Flush();
            }
        }

        private static async Task WaitForTcsAsync(
            TaskCompletionSource<bool> tcs,
            int timeoutMs,
            string expected)
        {
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                using (cts.Token.Register(() =>
                    tcs.TrySetCanceled()))
                {
                    try
                    {
                        await tcs.Task.ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        throw new TimeoutException(
                            $"Engine did not respond with {expected} within {timeoutMs} ms");
                    }
                }
            }
        }

        private void EnsureRunning()
        {
            if (!IsRunning || _isStopping)
                throw new InvalidOperationException(
                    "Engine not running");
        }

        // ============================================================
        // CONVENIENCE OPTIONS
        // ============================================================

        public Task SetThreadsAsync(int threads) =>
            SetOptionAsync("Threads", threads);

        public Task SetHashAsync(int mb) =>
            SetOptionAsync("Hash", mb);

        public Task SetSkillLevelAsync(int level) =>
            SetOptionAsync("Skill Level", level);
    }
}
