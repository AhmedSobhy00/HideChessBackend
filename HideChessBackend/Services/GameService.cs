using System.Collections.Concurrent;
using HideChessBackend.Enums;
using HideChessBackend.Hubs;
using HideChessBackend.Models;
using HideChessBackend.Settings;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace HideChessBackend.Services;

/// <summary>
/// In-memory game manager.  All state mutations are guarded by a per-game
/// SemaphoreSlim so concurrent SignalR calls and timer callbacks cannot corrupt
/// the game state.
/// </summary>
public class GameService : IGameService
{
    private readonly ConcurrentDictionary<string, GameState> _games           = new();
    private readonly ConcurrentDictionary<string, string>    _connectionToGame = new(); // connectionId → gameId

    private readonly IChessService        _chess;
    private readonly IHubContext<GameHub> _hub;
    private readonly GameSettings         _cfg;
    private readonly ILogger<GameService> _log;

    public GameService(
        IChessService chess,
        IHubContext<GameHub> hub,
        IOptions<GameSettings> cfg,
        ILogger<GameService> log)
    {
        _chess = chess;
        _hub   = hub;
        _cfg   = cfg.Value;
        _log   = log;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Lobby
    // ════════════════════════════════════════════════════════════════════════

    public Task<GameState> CreateGameAsync(string connectionId, string playerName, string gameMode = "HiddenFormation")
    {
        var gameId = GenerateGameId();
        var game   = new GameState
        {
            GameId         = gameId,
            GameMode       = string.IsNullOrWhiteSpace(gameMode) ? "HiddenFormation" : gameMode,
            PlayerWhite    = new PlayerInfo
            {
                ConnectionId = connectionId,
                PlayerId     = Guid.NewGuid().ToString(),
                Name         = Sanitize(playerName),
                Color        = PieceColor.White,
                IsConnected  = true
            },
            WhiteSetupBoard = _chess.CreateDefaultSetup(PieceColor.White),
            BlackSetupBoard = _chess.CreateDefaultSetup(PieceColor.Black),
            CreatedAt       = DateTime.UtcNow
        };

        _games[gameId]              = game;
        _connectionToGame[connectionId] = gameId;
        return Task.FromResult(game);
    }

    public async Task<(GameState? game, string? error)> JoinGameAsync(
        string gameId, string connectionId, string playerName)
    {
        gameId = gameId?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!_games.TryGetValue(gameId, out var game))
            return (null, "Game not found");
        if (game.Phase != GamePhase.WaitingForPlayers)
            return (null, "Game has already started");
        if (game.PlayerBlack != null)
            return (null, "Game is full");

        await game.Lock.WaitAsync();
        try
        {
            if (game.Phase != GamePhase.WaitingForPlayers || game.PlayerBlack != null)
                return (null, "Game is no longer joinable");

            game.PlayerBlack = new PlayerInfo
            {
                ConnectionId = connectionId,
                PlayerId     = Guid.NewGuid().ToString(),
                Name         = Sanitize(playerName),
                Color        = PieceColor.Black,
                IsConnected  = true
            };
        }
        finally { game.Lock.Release(); }

        _connectionToGame[connectionId] = gameId;
        return (game, null);
    }

    public async Task<(bool success, string? error)> StartMatchAsync(string gameId, string connectionId)
    {
        if (!_games.TryGetValue(gameId, out var game)) return (false, "Game not found");

        await game.Lock.WaitAsync();
        try
        {
            if (game.Phase != GamePhase.WaitingForPlayers) return (false, "Game has already started");
            if (game.PlayerWhite == null || game.PlayerWhite.ConnectionId != connectionId)
                return (false, "Only the room owner can start the match");
            if (game.PlayerBlack == null)
                return (false, "Waiting for opponent to join");
        }
        finally { game.Lock.Release(); }

        var ids = ConnectedPlayerIds(game);
        await _hub.Clients.Clients(ids).SendAsync("MatchStarting", new { seconds = 3 });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                if (game.GameMode == "Classic")
                {
                    await StartRevealPhaseAsync(gameId);
                }
                else
                {
                    await StartSetupPhaseAsync(gameId);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "StartMatch countdown error for game {Id}", gameId);
            }
        });

        return (true, null);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Setup phase
    // ════════════════════════════════════════════════════════════════════════

    public async Task StartSetupPhaseAsync(string gameId)
    {
        if (!_games.TryGetValue(gameId, out var game)) return;
        if (game.PlayerWhite == null || game.PlayerBlack == null) return;

        await game.Lock.WaitAsync();
        CancellationTokenSource cts;
        DateTime endsAt;
        try
        {
            if (game.Phase != GamePhase.WaitingForPlayers) return;
            game.Phase      = GamePhase.Setup;
            endsAt          = DateTime.UtcNow.AddSeconds(_cfg.SetupDurationSeconds);
            game.SetupEndsAt = endsAt;
            cts              = new CancellationTokenSource();
            game.SetupTimerCts = cts;
        }
        finally { game.Lock.Release(); }

        // Fire-and-forget timer — calls StartRevealPhaseAsync when it fires
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_cfg.SetupDurationSeconds), cts.Token);
                await StartRevealPhaseAsync(gameId);
            }
            catch (OperationCanceledException) { /* Both players pressed Ready early */ }
            catch (Exception ex) { _log.LogError(ex, "Setup timer error for game {Id}", gameId); }
        });

        // Send per-player setup info (different data — privacy is enforced here)
        static object PieceDto(ChessPieceInfo p) => new
        {
            type  = p.Type.ToString(),
            color = p.Color.ToString(),
            row   = p.Row,
            col   = p.Col
        };

        await _hub.Clients.Client(game.PlayerWhite.ConnectionId).SendAsync("SetupStarted", new
        {
            gameId,
            yourColor    = "White",
            yourName     = game.PlayerWhite.Name,
            opponentName = game.PlayerBlack.Name,
            setupEndsAt  = endsAt,
            yourPieces   = game.WhiteSetupBoard.Values.Select(PieceDto).ToList()
        });

        await _hub.Clients.Client(game.PlayerBlack.ConnectionId).SendAsync("SetupStarted", new
        {
            gameId,
            yourColor    = "Black",
            yourName     = game.PlayerBlack.Name,
            opponentName = game.PlayerWhite.Name,
            setupEndsAt  = endsAt,
            yourPieces   = game.BlackSetupBoard.Values.Select(PieceDto).ToList()
        });
    }

    public async Task<(bool success, string? error)> MoveSetupPieceAsync(
        string gameId, string connectionId,
        int fromRow, int fromCol, int toRow, int toCol)
    {
        if (!_games.TryGetValue(gameId, out var game)) return (false, "Game not found");

        await game.Lock.WaitAsync();
        try
        {
            if (game.Phase != GamePhase.Setup) return (false, "Not in setup phase");

            var player = PlayerByConnection(game, connectionId);
            if (player is null) return (false, "You are not in this game");
            if (player.IsReady)  return (false, "Formation is locked — you already pressed Ready");

            // Validate source / destination
            int minRow = player.Color == PieceColor.White ? 0 : 4;
            int maxRow = player.Color == PieceColor.White ? 3 : 7;

            if (fromRow == toRow && fromCol == toCol) return (false, "Source and destination are the same");
            if (toRow < minRow || toRow > maxRow || toCol < 0 || toCol > 7)
                return (false, "Destination is outside your deployment zone");

            var setupBoard = player.Color == PieceColor.White ? game.WhiteSetupBoard : game.BlackSetupBoard;

            if (!setupBoard.TryGetValue((fromRow, fromCol), out var piece))
                return (false, "No piece at that square");
            if (piece.Color != player.Color)
                return (false, "That piece does not belong to you");
            if (setupBoard.ContainsKey((toRow, toCol)))
                return (false, "Destination square is already occupied");

            // Move
            setupBoard.Remove((fromRow, fromCol));
            piece.Row = toRow;
            piece.Col = toCol;
            setupBoard[(toRow, toCol)] = piece;
        }
        finally { game.Lock.Release(); }

        // Inform only the mover — opponent must not see the move
        await _hub.Clients.Client(connectionId).SendAsync("SetupPieceMoved",
            new { fromRow, fromCol, toRow, toCol });

        return (true, null);
    }

    public async Task<(bool success, string? error)> SetReadyAsync(string gameId, string connectionId)
    {
        if (!_games.TryGetValue(gameId, out var game)) return (false, "Game not found");

        PlayerInfo? opponent   = null;
        bool        bothReady  = false;

        await game.Lock.WaitAsync();
        try
        {
            if (game.Phase != GamePhase.Setup) return (false, "Not in setup phase");

            var player = PlayerByConnection(game, connectionId);
            if (player is null)   return (false, "You are not in this game");
            if (player.IsReady)   return (false, "Already ready");

            var setupBoard = player.Color == PieceColor.White ? game.WhiteSetupBoard : game.BlackSetupBoard;
            if (!_chess.ValidateSetupFormation(setupBoard, player.Color))
                return (false, "Invalid formation — make sure all pieces are placed correctly");

            player.IsReady = true;
            opponent       = Opponent(game, player.Color);
            bothReady      = game.PlayerWhite!.IsReady && game.PlayerBlack!.IsReady;

            if (bothReady)
            {
                game.SetupTimerCts?.Cancel();
                game.SetupTimerCts = null;
            }
        }
        finally { game.Lock.Release(); }

        // Notify the player that their formation is locked
        await _hub.Clients.Client(connectionId).SendAsync("SetupLocked");

        // Notify opponent
        if (opponent?.IsConnected == true)
            await _hub.Clients.Client(opponent.ConnectionId).SendAsync("OpponentReady");

        // Both ready — kick off reveal immediately (lock already released above)
        if (bothReady)
            await StartRevealPhaseAsync(gameId);

        return (true, null);
    }

    public async Task StartRevealPhaseAsync(string gameId)
    {
        if (!_games.TryGetValue(gameId, out var game)) return;

        List<object> allPieces;

        await game.Lock.WaitAsync();
        try
        {
            if (game.Phase != GamePhase.Setup && game.Phase != GamePhase.WaitingForPlayers) return; // idempotency guard

            // Validate both formations; fall back to default if somehow corrupt
            if (!_chess.ValidateSetupFormation(game.WhiteSetupBoard, PieceColor.White))
                game.WhiteSetupBoard = _chess.CreateDefaultSetup(PieceColor.White);
            if (!_chess.ValidateSetupFormation(game.BlackSetupBoard, PieceColor.Black))
                game.BlackSetupBoard = _chess.CreateDefaultSetup(PieceColor.Black);

            game.Board       = _chess.CombineBoards(game.WhiteSetupBoard, game.BlackSetupBoard);

            // If secret setups placed Kings adjacent to each other, relocate Black King to prevent immediate check lock
            if (_chess.AreKingsAdjacent(game.Board))
            {
                var bkKvp = game.Board.FirstOrDefault(kvp => kvp.Value.Color == PieceColor.Black && kvp.Value.Type == PieceType.King);
                if (bkKvp.Value != null)
                {
                    (int oldR, int oldC) = bkKvp.Key;
                    for (int r = 7; r >= 4; r--)
                    {
                        bool found = false;
                        for (int c = 0; c < 8; c++)
                        {
                            if (!game.Board.ContainsKey((r, c)))
                            {
                                game.Board.Remove((oldR, oldC));
                                var kingPiece = bkKvp.Value;
                                kingPiece.Row = r;
                                kingPiece.Col = c;
                                game.Board[(r, c)] = kingPiece;
                                found = true;
                                break;
                            }
                        }
                        if (found && !_chess.AreKingsAdjacent(game.Board)) break;
                    }
                }
            }

            // Ensure neither King starts the game in check due to secret formation overlap
            ResolveInitialCheck(game.Board, PieceColor.White);
            ResolveInitialCheck(game.Board, PieceColor.Black);

            game.Phase       = GamePhase.Playing;
            game.CurrentTurn = PieceColor.White;
            game.HalfMoveClock  = 0;
            game.FullMoveNumber = 1;

            // Initialise position history
            var posKey = _chess.GetPositionKey(game.Board, PieceColor.White, null);
            game.PositionHistory[posKey] = 1;

            allPieces = game.Board.Values.Select(p => (object)new
            {
                type  = p.Type.ToString(),
                color = p.Color.ToString(),
                row   = p.Row,
                col   = p.Col
            }).ToList();
        }
        finally { game.Lock.Release(); }

        var connIds = ConnectedPlayerIds(game);
        await _hub.Clients.Clients(connIds).SendAsync("BoardRevealed", new
        {
            pieces      = allPieces,
            currentTurn = "White"
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Playing phase
    // ════════════════════════════════════════════════════════════════════════

    public async Task<(bool success, string? error)> GetLegalMovesAsync(
        string gameId, string connectionId, string from)
    {
        if (!_games.TryGetValue(gameId, out var game)) return (false, "Game not found");
        if (game.Phase != GamePhase.Playing) return (false, "Not in playing phase");

        var player = PlayerByConnection(game, connectionId);
        if (player is null) return (false, "You are not in this game");

        (int fr, int fc) = _chess.FromAlgebraic(from);
        (int, int)? ep = game.EnPassantTarget != null ? _chess.FromAlgebraic(game.EnPassantTarget) : null;

        var moves = _chess.GetLegalMoves(game.Board, fr, fc, ep)
                          .Select(m => _chess.ToAlgebraic(m.row, m.col))
                          .ToList();

        await _hub.Clients.Client(connectionId).SendAsync("LegalMoves",
            new { from, moves });

        return (true, null);
    }

    public async Task<(bool success, string? error)> MakeMoveAsync(
        string gameId, string connectionId,
        string from, string to, string? promotion)
    {
        if (!_games.TryGetValue(gameId, out var game)) return (false, "Game not found");

        object? moveMadeDto     = null;
        object? gameFinishedDto = null;

        await game.Lock.WaitAsync();
        try
        {
            if (game.Phase != GamePhase.Playing)
                return (false, "Game is not in playing phase");

            var player = PlayerByConnection(game, connectionId);
            if (player is null)             return (false, "You are not in this game");
            if (player.Color != game.CurrentTurn) return (false, "It is not your turn");

            // Parse squares
            (int fr, int fc) = _chess.FromAlgebraic(from);
            (int tr, int tc) = _chess.FromAlgebraic(to);

            if (!game.Board.TryGetValue((fr, fc), out var piece))
                return (false, "No piece at source square");
            if (piece.Color != player.Color)
                return (false, "That piece does not belong to you");

            // Resolve en-passant target
            (int, int)? ep = game.EnPassantTarget != null
                ? _chess.FromAlgebraic(game.EnPassantTarget) : null;

            // Resolve promotion
            PieceType? promoPiece = promotion?.ToLowerInvariant() switch
            {
                "queen"  => PieceType.Queen,
                "rook"   => PieceType.Rook,
                "bishop" => PieceType.Bishop,
                "knight" => PieceType.Knight,
                _        => null
            };

            // Apply the move (validates legality inside)
            var result = _chess.ApplyMove(game.Board, fr, fc, tr, tc, promoPiece, ep);
            if (!result.Success) return (false, result.Error);

            // ── Update state ─────────────────────────────────────────────────
            game.Board = result.NewBoard;

            game.EnPassantTarget = result.NewEnPassantTarget.HasValue
                ? _chess.ToAlgebraic(result.NewEnPassantTarget.Value.row,
                                     result.NewEnPassantTarget.Value.col)
                : null;

            // 50-move clock
            if (piece.Type == PieceType.Pawn || result.CapturedPiece != null)
                game.HalfMoveClock = 0;
            else
                game.HalfMoveClock++;

            // Full-move counter
            if (game.CurrentTurn == PieceColor.Black) game.FullMoveNumber++;

            // Move notation
            string notation = from + to + (result.IsPromotion
                ? (promoPiece ?? PieceType.Queen).ToString()[0..1].ToLower() : "");
            game.MoveHistory.Add(notation);

            // Clear any pending draw offer when a move is played
            game.DrawOfferedBy = null;

            // Switch turn
            var nextTurn = Opposite(game.CurrentTurn);
            game.CurrentTurn = nextTurn;

            // Position history (for threefold repetition)
            var posKey = _chess.GetPositionKey(game.Board, nextTurn, game.EnPassantTarget);
            game.PositionHistory.TryGetValue(posKey, out int posCount);
            game.PositionHistory[posKey] = posCount + 1;

            // ── End-game detection ───────────────────────────────────────────
            var nextEp = result.NewEnPassantTarget;

            bool isCheck      = _chess.IsInCheck(game.Board, nextTurn);
            bool isCheckmate  = isCheck && _chess.IsCheckmate(game.Board, nextTurn, nextEp);
            bool isStalemate  = !isCheck && _chess.IsStalemate(game.Board, nextTurn, nextEp);
            bool isInsufficient = _chess.HasInsufficientMaterial(game.Board);
            bool isFiftyMove  = game.HalfMoveClock >= 100;
            bool isThreefold  = game.PositionHistory[posKey] >= 3;
            bool isDraw       = isStalemate || isInsufficient || isFiftyMove || isThreefold;

            string? drawReason = isStalemate   ? "Stalemate"
                : isInsufficient ? "Insufficient material"
                : isFiftyMove    ? "50-move rule"
                : isThreefold    ? "Threefold repetition"
                : null;

            // Build piece snapshot for clients
            var pieces = game.Board.Values.Select(p => (object)new
            {
                type  = p.Type.ToString(),
                color = p.Color.ToString(),
                row   = p.Row,
                col   = p.Col
            }).ToList();

            moveMadeDto = new
            {
                from,
                to,
                promotion    = result.IsPromotion ? (promoPiece ?? PieceType.Queen).ToString() : null,
                pieces,
                isCheck,
                isCheckmate,
                isDraw,
                drawReason,
                currentTurn  = nextTurn.ToString(),
                capturedPiece = result.CapturedPiece != null ? (object)new
                {
                    type  = result.CapturedPiece.Type.ToString(),
                    color = result.CapturedPiece.Color.ToString()
                } : null,
                enPassantTarget = game.EnPassantTarget,
                isEnPassant  = result.IsEnPassant,
                moveNotation = notation,
                moveNumber   = game.FullMoveNumber
            };

            // ── Finish game if warranted ─────────────────────────────────────
            if (isCheckmate)
            {
                game.Phase  = GamePhase.Finished;
                game.Winner = player.Color; // the player who just moved wins
                game.Result = player.Color == PieceColor.White
                    ? GameResult.WhiteWins : GameResult.BlackWins;

                gameFinishedDto = new
                {
                    result = game.Result.ToString(),
                    winner = player.Color.ToString(),
                    reason = "Checkmate"
                };
            }
            else if (isDraw)
            {
                game.Phase  = GamePhase.Finished;
                game.Result = GameResult.Draw;

                gameFinishedDto = new
                {
                    result = "Draw",
                    winner = (string?)null,
                    reason = drawReason
                };
            }
        }
        finally { game.Lock.Release(); }

        var ids = ConnectedPlayerIds(game);
        if (moveMadeDto     != null) await _hub.Clients.Clients(ids).SendAsync("MoveMade",     moveMadeDto);
        if (gameFinishedDto != null) await _hub.Clients.Clients(ids).SendAsync("GameFinished", gameFinishedDto);

        return (true, null);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Game-control actions
    // ════════════════════════════════════════════════════════════════════════

    public async Task<(bool success, string? error)> ResignAsync(string gameId, string connectionId)
    {
        if (!_games.TryGetValue(gameId, out var game)) return (false, "Game not found");

        object? dto = null;

        await game.Lock.WaitAsync();
        try
        {
            if (game.Phase != GamePhase.Playing) return (false, "Not in playing phase");
            var player = PlayerByConnection(game, connectionId);
            if (player is null) return (false, "You are not in this game");

            var winner   = Opposite(player.Color);
            game.Phase   = GamePhase.Finished;
            game.Winner  = winner;
            game.Result  = winner == PieceColor.White ? GameResult.WhiteWins : GameResult.BlackWins;

            dto = new { result = game.Result.ToString(), winner = winner.ToString(), reason = "Resignation" };
        }
        finally { game.Lock.Release(); }

        if (dto != null)
            await _hub.Clients.Clients(ConnectedPlayerIds(game)).SendAsync("GameFinished", dto);

        return (true, null);
    }

    public async Task<(bool success, string? error)> OfferDrawAsync(string gameId, string connectionId)
    {
        if (!_games.TryGetValue(gameId, out var game)) return (false, "Game not found");

        PlayerInfo? opp = null;

        await game.Lock.WaitAsync();
        try
        {
            if (game.Phase != GamePhase.Playing)        return (false, "Not in playing phase");
            var player = PlayerByConnection(game, connectionId);
            if (player is null)                          return (false, "You are not in this game");
            if (game.DrawOfferedBy.HasValue)             return (false, "A draw has already been offered");

            game.DrawOfferedBy = player.Color;
            opp = Opponent(game, player.Color);
        }
        finally { game.Lock.Release(); }

        if (opp?.IsConnected == true)
            await _hub.Clients.Client(opp.ConnectionId).SendAsync("DrawOffered");

        return (true, null);
    }

    public async Task<(bool success, string? error)> AcceptDrawAsync(string gameId, string connectionId)
    {
        if (!_games.TryGetValue(gameId, out var game)) return (false, "Game not found");

        object? dto = null;

        await game.Lock.WaitAsync();
        try
        {
            if (game.Phase != GamePhase.Playing)   return (false, "Not in playing phase");
            var player = PlayerByConnection(game, connectionId);
            if (player is null)                     return (false, "You are not in this game");
            if (!game.DrawOfferedBy.HasValue || game.DrawOfferedBy == player.Color)
                return (false, "No draw offer to accept");

            game.DrawOfferedBy = null;
            game.Phase  = GamePhase.Finished;
            game.Result = GameResult.Draw;

            dto = new { result = "Draw", winner = (string?)null, reason = "Mutual agreement" };
        }
        finally { game.Lock.Release(); }

        if (dto != null)
            await _hub.Clients.Clients(ConnectedPlayerIds(game)).SendAsync("GameFinished", dto);

        return (true, null);
    }

    public async Task<(bool success, string? error)> DeclineDrawAsync(string gameId, string connectionId)
    {
        if (!_games.TryGetValue(gameId, out var game)) return (false, "Game not found");

        PlayerInfo? offerer = null;

        await game.Lock.WaitAsync();
        try
        {
            if (game.Phase != GamePhase.Playing)   return (false, "Not in playing phase");
            var player = PlayerByConnection(game, connectionId);
            if (player is null)                     return (false, "You are not in this game");
            if (!game.DrawOfferedBy.HasValue || game.DrawOfferedBy == player.Color)
                return (true, null);

            var offererColor   = game.DrawOfferedBy.Value;
            game.DrawOfferedBy = null;
            offerer = offererColor == PieceColor.White ? game.PlayerWhite : game.PlayerBlack;
        }
        finally { game.Lock.Release(); }

        if (offerer?.IsConnected == true)
            await _hub.Clients.Client(offerer.ConnectionId).SendAsync("DrawDeclined");

        return (true, null);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Connectivity
    // ════════════════════════════════════════════════════════════════════════

    public async Task HandleDisconnectionAsync(string connectionId)
    {
        if (!_connectionToGame.TryRemove(connectionId, out var gameId)) return;
        if (!_games.TryGetValue(gameId, out var game))                  return;

        PlayerInfo? disconnectedPlayer = null;
        PlayerInfo? opp                = null;
        bool        abandonImmediately = false;

        await game.Lock.WaitAsync();
        try
        {
            var player = PlayerByConnection(game, connectionId);
            if (player is null) return;

            player.IsConnected    = false;
            disconnectedPlayer    = player;
            opp                   = Opponent(game, player.Color);

            // During setup we abandon immediately — there is no sense waiting
            if (game.Phase is GamePhase.Setup or GamePhase.WaitingForPlayers)
            {
                game.Phase  = GamePhase.Finished;
                game.Result = GameResult.Abandoned;
                abandonImmediately = true;
            }
            else if (game.Phase == GamePhase.Playing)
            {
                game.Phase = GamePhase.Abandoned; // Temporary — player may reconnect
            }
        }
        finally { game.Lock.Release(); }

        if (abandonImmediately)
        {
            if (opp?.IsConnected == true)
                await _hub.Clients.Client(opp.ConnectionId).SendAsync("GameFinished",
                    new { result = "Abandoned", winner = opp.Color.ToString(), reason = "Opponent disconnected" });
            return;
        }

        // Notify opponent and start grace-period timer
        if (opp?.IsConnected == true)
            await _hub.Clients.Client(opp.ConnectionId).SendAsync("OpponentDisconnected");

        await game.Lock.WaitAsync();
        try
        {
            game.DisconnectionTimerCts?.Cancel();
            var cts = new CancellationTokenSource();
            game.DisconnectionTimerCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_cfg.DisconnectionGraceSeconds), cts.Token);
                    await ForceEndGameAsync(gameId, disconnectedPlayer!.Color, opp?.Color, "Opponent abandoned the game");
                }
                catch (OperationCanceledException) { /* Player reconnected in time */ }
                catch (Exception ex) { _log.LogError(ex, "Disconnection timer error for game {Id}", gameId); }
            });
        }
        finally { game.Lock.Release(); }
    }

    public async Task HandleReconnectionAsync(string gameId, string playerId, string newConnectionId)
    {
        if (!_games.TryGetValue(gameId, out var game)) return;

        PlayerInfo? reconnected = null;
        PlayerInfo? opp         = null;
        object?     stateDto    = null;

        await game.Lock.WaitAsync();
        try
        {
            if (game.PlayerWhite?.PlayerId == playerId)      reconnected = game.PlayerWhite;
            else if (game.PlayerBlack?.PlayerId == playerId) reconnected = game.PlayerBlack;
            if (reconnected is null) return;

            // Update connection mapping
            _connectionToGame.TryRemove(reconnected.ConnectionId, out _);
            reconnected.ConnectionId = newConnectionId;
            reconnected.IsConnected  = true;
            _connectionToGame[newConnectionId] = gameId;

            // Cancel grace-period timer
            game.DisconnectionTimerCts?.Cancel();
            game.DisconnectionTimerCts = null;

            // Restore playing phase if we had set it to Abandoned
            if (game.Phase == GamePhase.Abandoned)
                game.Phase = GamePhase.Playing;

            opp = Opponent(game, reconnected.Color);

            // Build the state snapshot the client needs to restore its UI
            switch (game.Phase)
            {
                case GamePhase.Setup:
                    var setupBoard = reconnected.Color == PieceColor.White
                        ? game.WhiteSetupBoard : game.BlackSetupBoard;
                    stateDto = new
                    {
                        phase        = "Setup",
                        yourColor    = reconnected.Color.ToString(),
                        setupEndsAt  = game.SetupEndsAt,
                        yourPieces   = setupBoard.Values.Select(p => new
                        {
                            type  = p.Type.ToString(),
                            color = p.Color.ToString(),
                            row   = p.Row, col = p.Col
                        }).ToList(),
                        isReady      = reconnected.IsReady,
                        opponentReady = opp?.IsReady ?? false
                    };
                    break;

                case GamePhase.Playing:
                    stateDto = new
                    {
                        phase        = "Playing",
                        yourColor    = reconnected.Color.ToString(),
                        pieces       = game.Board.Values.Select(p => new
                        {
                            type  = p.Type.ToString(),
                            color = p.Color.ToString(),
                            row   = p.Row, col = p.Col
                        }).ToList(),
                        currentTurn  = game.CurrentTurn.ToString(),
                        isCheck      = _chess.IsInCheck(game.Board, game.CurrentTurn),
                        moveHistory  = game.MoveHistory,
                        enPassantTarget = game.EnPassantTarget
                    };
                    break;

                case GamePhase.Finished:
                    stateDto = new
                    {
                        phase  = "Finished",
                        result = game.Result.ToString(),
                        winner = game.Winner?.ToString()
                    };
                    break;
            }
        }
        finally { game.Lock.Release(); }

        if (stateDto != null)
            await _hub.Clients.Client(newConnectionId).SendAsync("GameStateRestored", stateDto);

        if (opp?.IsConnected == true)
            await _hub.Clients.Client(opp.ConnectionId).SendAsync("OpponentReconnected");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Queries
    // ════════════════════════════════════════════════════════════════════════

    public GameState? GetGame(string gameId)
        => _games.TryGetValue(gameId, out var g) ? g : null;

    public GameState? GetGameByConnectionId(string connectionId)
        => _connectionToGame.TryGetValue(connectionId, out var id) ? GetGame(id) : null;

    // ════════════════════════════════════════════════════════════════════════
    //  Private helpers
    // ════════════════════════════════════════════════════════════════════════

    private async Task ForceEndGameAsync(
        string gameId, PieceColor loserColor, PieceColor? winnerColor, string reason)
    {
        if (!_games.TryGetValue(gameId, out var game)) return;

        object? dto = null;

        await game.Lock.WaitAsync();
        try
        {
            if (game.Phase == GamePhase.Finished) return; // already over

            var winner  = winnerColor;
            game.Phase  = GamePhase.Finished;
            game.Winner = winner;
            game.Result = winner.HasValue
                ? (winner == PieceColor.White ? GameResult.WhiteWins : GameResult.BlackWins)
                : GameResult.Abandoned;

            dto = new
            {
                result = game.Result.ToString(),
                winner = winner?.ToString(),
                reason
            };
        }
        finally { game.Lock.Release(); }

        if (dto != null)
            await _hub.Clients.Clients(ConnectedPlayerIds(game)).SendAsync("GameFinished", dto);
    }

    private void ResolveInitialCheck(Dictionary<(int row, int col), ChessPieceInfo> board, PieceColor kingColor)
    {
        if (!_chess.IsInCheck(board, kingColor)) return;

        var oppColor = kingColor == PieceColor.White ? PieceColor.Black : PieceColor.White;
        int minRow = oppColor == PieceColor.White ? 0 : 4;
        int maxRow = oppColor == PieceColor.White ? 3 : 7;

        // Find king position
        (int kr, int kc) kingPos = (-1, -1);
        foreach (var ((r, c), p) in board)
        {
            if (p.Color == kingColor && p.Type == PieceType.King)
            {
                kingPos = (r, c);
                break;
            }
        }
        if (kingPos.kr < 0) return;

        // Find checking opponent pieces using attack query
        var checkingPieces = board
            .Where(kvp => kvp.Value.Color == oppColor)
            .Where(kvp => _chess.IsSquareAttackedByPiece(board, kingPos.kr, kingPos.kc, kvp.Key.row, kvp.Key.col))
            .ToList();

        foreach (var kvp in checkingPieces)
        {
            (int oldR, int oldC) = kvp.Key;
            var piece = kvp.Value;

            bool relocated = false;
            for (int r = minRow; r <= maxRow && !relocated; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    if (!board.ContainsKey((r, c)))
                    {
                        board.Remove((oldR, oldC));
                        piece.Row = r;
                        piece.Col = c;
                        board[(r, c)] = piece;

                        if (!_chess.IsInCheck(board, kingColor))
                        {
                            relocated = true;
                            break;
                        }
                        else
                        {
                            // Revert and try next open square
                            board.Remove((r, c));
                            piece.Row = oldR;
                            piece.Col = oldC;
                            board[(oldR, oldC)] = piece;
                        }
                    }
                }
            }
        }

        // Fallback: If king is STILL in check, relocate King itself to a safe square in its deployment zone
        if (_chess.IsInCheck(board, kingColor))
        {
            var kingKvp = board.FirstOrDefault(kvp => kvp.Value.Color == kingColor && kvp.Value.Type == PieceType.King);
            if (kingKvp.Value != null)
            {
                (int oldR, int oldC) = kingKvp.Key;
                var kingPiece = kingKvp.Value;
                int kMinRow = kingColor == PieceColor.White ? 0 : 4;
                int kMaxRow = kingColor == PieceColor.White ? 3 : 7;

                for (int r = kMinRow; r <= kMaxRow; r++)
                {
                    bool found = false;
                    for (int c = 0; c < 8; c++)
                    {
                        if (!board.ContainsKey((r, c)))
                        {
                            board.Remove((oldR, oldC));
                            kingPiece.Row = r;
                            kingPiece.Col = c;
                            board[(r, c)] = pieceRef(kingPiece, r, c);

                            if (!_chess.IsInCheck(board, kingColor))
                            {
                                found = true;
                                break;
                            }
                            else
                            {
                                board.Remove((r, c));
                                kingPiece.Row = oldR;
                                kingPiece.Col = oldC;
                                board[(oldR, oldC)] = kingPiece;
                            }
                        }
                    }
                    if (found) break;
                }
            }
        }
    }

    private static ChessPieceInfo pieceRef(ChessPieceInfo p, int r, int c)
    {
        p.Row = r;
        p.Col = c;
        return p;
    }

    private static PlayerInfo? PlayerByConnection(GameState game, string connectionId)
    {
        if (game.PlayerWhite?.ConnectionId == connectionId) return game.PlayerWhite;
        if (game.PlayerBlack?.ConnectionId == connectionId) return game.PlayerBlack;
        return null;
    }

    private static PlayerInfo? Opponent(GameState game, PieceColor color)
        => color == PieceColor.White ? game.PlayerBlack : game.PlayerWhite;

    private static PieceColor Opposite(PieceColor c)
        => c == PieceColor.White ? PieceColor.Black : PieceColor.White;

    private static List<string> ConnectedPlayerIds(GameState game)
    {
        var ids = new List<string>(2);
        if (game.PlayerWhite?.IsConnected == true) ids.Add(game.PlayerWhite.ConnectionId);
        if (game.PlayerBlack?.IsConnected == true) ids.Add(game.PlayerBlack.ConnectionId);
        return ids;
    }

    private string GenerateGameId()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Unambiguous chars
        string id;
        do
        {
            id = new string(Enumerable.Range(0, 6)
                .Select(_ => chars[Random.Shared.Next(chars.Length)])
                .ToArray());
        }
        while (_games.ContainsKey(id));
        return id;
    }

    private static string Sanitize(string name)
        => string.IsNullOrWhiteSpace(name) ? "Anonymous"
           : name.Trim().Length > 24 ? name.Trim()[..24]
           : name.Trim();
}
