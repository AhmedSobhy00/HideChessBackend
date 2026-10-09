using System.Collections.Concurrent;
using HideChessBackend.DTOs;
using HideChessBackend.Hubs;
using HideChessBackend.Models.Battleship;
using Microsoft.AspNetCore.SignalR;

namespace HideChessBackend.Services;

public class BattleshipService : IBattleshipService
{
    private readonly ConcurrentDictionary<string, BattleshipGameState> _games = new();
    private readonly ConcurrentDictionary<string, string> _connectionToGameId = new();
    private readonly IHubContext<BattleshipHub> _hubContext;
    private readonly ILogger<BattleshipService> _logger;
    private readonly Random _rand = new();

    public BattleshipService(IHubContext<BattleshipHub> hubContext, ILogger<BattleshipService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public BattleshipGameState? GetGame(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return null;
        _games.TryGetValue(gameId.Trim().ToUpperInvariant(), out var game);
        return game;
    }

    public async Task<BattleshipGameState> CreateGameAsync(string connectionId, string playerName)
    {
        var gameId = GenerateId(6);
        var game = new BattleshipGameState
        {
            GameId = gameId,
            RoomCode = gameId,
            Phase = BattleshipPhase.WaitingForPlayers,
            Host = new BattleshipPlayer
            {
                ConnectionId = connectionId,
                Name = string.IsNullOrWhiteSpace(playerName) ? "Commander" : playerName.Trim()
            }
        };

        _games[gameId] = game;
        _connectionToGameId[connectionId] = gameId;

        _logger.LogInformation("Battleship game {GameId} created by {Host}", gameId, game.Host.Name);
        return game;
    }

    public async Task<BattleshipGameState> CreateBotGameAsync(string connectionId, string playerName, string difficulty)
    {
        var gameId = GenerateId(6);
        var botPlayer = new BattleshipPlayer
        {
            PlayerId = "BOT_" + Guid.NewGuid().ToString("N")[..8],
            ConnectionId = "BOT_CONN",
            Name = $"Admiral AI ({difficulty})",
            IsBot = true,
            BotDifficulty = difficulty
        };

        // Randomly place bot ships
        AutoPlaceShips(botPlayer);
        botPlayer.IsReady = true;

        var game = new BattleshipGameState
        {
            GameId = gameId,
            RoomCode = gameId,
            Phase = BattleshipPhase.Setup,
            Host = new BattleshipPlayer
            {
                ConnectionId = connectionId,
                Name = string.IsNullOrWhiteSpace(playerName) ? "Commander" : playerName.Trim()
            },
            Guest = botPlayer
        };

        _games[gameId] = game;
        _connectionToGameId[connectionId] = gameId;

        _logger.LogInformation("Battleship Bot game {GameId} created against {BotName}", gameId, botPlayer.Name);
        return game;
    }

    public async Task<(BattleshipGameState? game, string? error)> JoinGameAsync(string gameId, string connectionId, string playerName)
    {
        var cleanId = gameId?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!_games.TryGetValue(cleanId, out var game))
        {
            return (null, "Game not found");
        }

        if (game.Phase != BattleshipPhase.WaitingForPlayers)
        {
            return (null, "Game is already in progress or completed");
        }

        game.Guest = new BattleshipPlayer
        {
            ConnectionId = connectionId,
            Name = string.IsNullOrWhiteSpace(playerName) ? "Challenger" : playerName.Trim()
        };

        game.Phase = BattleshipPhase.Setup;
        _connectionToGameId[connectionId] = cleanId;

        _logger.LogInformation("Player {Guest} joined Battleship game {GameId}", game.Guest.Name, cleanId);
        return (game, null);
    }

    public async Task<(bool ok, string? error)> PlaceFleetAsync(string gameId, string connectionId, List<PlaceShipDTO> ships)
    {
        var game = GetGame(gameId);
        if (game == null) return (false, "Game not found");

        var player = GetPlayerByConnection(game, connectionId);
        if (player == null) return (false, "Player not found");

        if (game.Phase != BattleshipPhase.Setup && game.Phase != BattleshipPhase.WaitingForPlayers)
        {
            return (false, "Fleet placement is not allowed in current phase");
        }

        if (!ValidateFleet(ships, out var shipInstances, out var grid, out var error))
        {
            return (false, error);
        }

        player.Ships = shipInstances;
        player.Grid = grid;
        player.IsReady = true;

        await CheckBothReadyAndStartAsync(game);
        return (true, null);
    }

    public async Task<(bool ok, string? error, List<PlaceShipDTO>? ships)> RandomizeFleetAsync(string gameId, string connectionId)
    {
        var game = GetGame(gameId);
        if (game == null) return (false, "Game not found", null);

        var player = GetPlayerByConnection(game, connectionId);
        if (player == null) return (false, "Player not found", null);

        AutoPlaceShips(player);

        var dtos = player.Ships.Select(s => new PlaceShipDTO
        {
            Type = s.Type,
            StartRow = s.StartRow,
            StartCol = s.StartCol,
            IsVertical = s.IsVertical
        }).ToList();

        return (true, null, dtos);
    }

    public async Task<(bool ok, string? error)> SetReadyAsync(string gameId, string connectionId)
    {
        var game = GetGame(gameId);
        if (game == null) return (false, "Game not found");

        var player = GetPlayerByConnection(game, connectionId);
        if (player == null) return (false, "Player not found");

        if (player.Ships.Count < 5)
        {
            // Auto place if player hits ready without placing all ships
            AutoPlaceShips(player);
        }

        player.IsReady = true;
        await CheckBothReadyAndStartAsync(game);
        return (true, null);
    }

    public async Task<(bool ok, string? error)> FireShotAsync(string gameId, string connectionId, int row, int col)
    {
        var game = GetGame(gameId);
        if (game == null) return (false, "Game not found");

        if (game.Phase != BattleshipPhase.Playing)
            return (false, "Game is not in playing phase");

        var shooter = GetPlayerByConnection(game, connectionId);
        if (shooter == null) return (false, "Player not found");

        if (game.CurrentTurnPlayerId != shooter.PlayerId)
            return (false, "Not your turn");

        var defender = shooter.PlayerId == game.Host.PlayerId ? game.Guest! : game.Host;

        if (row < 0 || row > 9 || col < 0 || col > 9)
            return (false, "Coordinates out of bounds");

        if (shooter.TargetRadar[row, col] != CellState.Empty)
            return (false, "Already targeted this square");

        return await ProcessShotAsync(game, shooter, defender, row, col);
    }

    private async Task<(bool ok, string? error)> ProcessShotAsync(BattleshipGameState game, BattleshipPlayer shooter, BattleshipPlayer defender, int row, int col)
    {
        bool isHit = defender.Grid[row, col] == CellState.Ship;
        ShipInstance? hitShip = null;

        if (isHit)
        {
            defender.Grid[row, col] = CellState.Hit;
            shooter.TargetRadar[row, col] = CellState.Hit;
            shooter.HitsOnOpponent.Add(new Coordinate(row, col));

            hitShip = defender.Ships.FirstOrDefault(s => s.OccupiedCells.Any(c => c.Row == row && c.Col == col));
            if (hitShip != null)
            {
                hitShip.Hits++;
                if (hitShip.IsSunk)
                {
                    foreach (var c in hitShip.OccupiedCells)
                    {
                        defender.Grid[c.Row, c.Col] = CellState.Sunk;
                        shooter.TargetRadar[c.Row, c.Col] = CellState.Sunk;
                    }
                }
            }

            // Populate Bot AI Target Queue on Hit
            if (shooter.IsBot)
            {
                if (hitShip != null && hitShip.IsSunk)
                {
                    // Filter out queue coordinates that were occupied by the sunk ship
                    shooter.BotTargetQueue.RemoveAll(q => shooter.TargetRadar[q.Row, q.Col] != CellState.Empty);
                }
                else
                {
                    // Enqueue adjacent orthogonal cells (Up, Down, Left, Right)
                    int[] dr = { -1, 1, 0, 0 };
                    int[] dc = { 0, 0, -1, 1 };
                    for (int i = 0; i < 4; i++)
                    {
                        int nr = row + dr[i];
                        int nc = col + dc[i];
                        if (nr >= 0 && nr < 10 && nc >= 0 && nc < 10)
                        {
                            if (shooter.TargetRadar[nr, nc] == CellState.Empty && !shooter.BotTargetQueue.Any(q => q.Row == nr && q.Col == nc))
                            {
                                shooter.BotTargetQueue.Add(new Coordinate(nr, nc));
                            }
                        }
                    }
                }
            }
        }
        else
        {
            defender.Grid[row, col] = CellState.Miss;
            shooter.TargetRadar[row, col] = CellState.Miss;
            shooter.MissesOnOpponent.Add(new Coordinate(row, col));
        }

        bool allSunk = defender.Ships.All(s => s.IsSunk);
        string nextTurnId = allSunk ? shooter.PlayerId : (isHit ? shooter.PlayerId : defender.PlayerId);
        game.CurrentTurnPlayerId = nextTurnId;

        // Broadcast Shot Fired event
        var shotData = new
        {
            shooterPlayerId = shooter.PlayerId,
            row = row,
            col = col,
            isHit = isHit,
            isSunk = hitShip?.IsSunk ?? false,
            sunkShipType = (hitShip?.IsSunk == true) ? hitShip.Type.ToString() : null,
            sunkShipCells = (hitShip?.IsSunk == true) ? hitShip.OccupiedCells : null,
            nextTurnPlayerId = nextTurnId
        };

        await BroadcastToGame(game.GameId, "BattleshipShotFired", shotData);

        if (allSunk)
        {
            game.Phase = BattleshipPhase.Finished;
            game.WinnerPlayerId = shooter.PlayerId;
            game.WinnerName = shooter.Name;

            await BroadcastFinishedState(game, shooter, "All ships destroyed");
            return (true, null);
        }

        // Handle Bot Turn if next turn belongs to bot
        var nextTurnPlayer = game.Host.PlayerId == nextTurnId ? game.Host : game.Guest;
        if (nextTurnPlayer != null && nextTurnPlayer.IsBot && game.Phase == BattleshipPhase.Playing)
        {
            var botPlayer = nextTurnPlayer;
            var opponent = botPlayer.PlayerId == game.Host.PlayerId ? game.Guest! : game.Host;
            _ = Task.Run(async () =>
            {
                var delay = _rand.Next(2800, 4200);
                await Task.Delay(delay);
                await TriggerBotMoveAsync(game, botPlayer, opponent);
            });
        }

        return (true, null);
    }

    private async Task TriggerBotMoveAsync(BattleshipGameState game, BattleshipPlayer bot, BattleshipPlayer humanTarget)
    {
        if (game.Phase != BattleshipPhase.Playing) return;

        Coordinate target = SelectBotTarget(bot, humanTarget);
        await ProcessShotAsync(game, bot, humanTarget, target.Row, target.Col);
    }

    private Coordinate SelectBotTarget(BattleshipPlayer bot, BattleshipPlayer human)
    {
        var diff = bot.BotDifficulty ?? "Medium";

        // EASY DIFFICULTY: Pure 100% Random Shots
        if (diff.Equals("Easy", StringComparison.OrdinalIgnoreCase))
        {
            return PickRandomUnshotCell(bot);
        }

        // HARD DIFFICULTY: Lethal Tactical Radar Tracking
        if (diff.Equals("Hard", StringComparison.OrdinalIgnoreCase))
        {
            // First check adjacent target queue from previous hits
            while (bot.BotTargetQueue.Count > 0)
            {
                var next = bot.BotTargetQueue[0];
                bot.BotTargetQueue.RemoveAt(0);
                if (bot.TargetRadar[next.Row, next.Col] == CellState.Empty)
                    return next;
            }

            // 70% chance to target an unshot cell of an active human ship
            if (_rand.Next(100) < 70 && human.Ships != null)
            {
                var aliveHumanCells = human.Ships
                    .Where(s => !s.IsSunk)
                    .SelectMany(s => s.OccupiedCells)
                    .Where(c => bot.TargetRadar[c.Row, c.Col] == CellState.Empty)
                    .ToList();

                if (aliveHumanCells.Count > 0)
                {
                    return aliveHumanCells[_rand.Next(aliveHumanCells.Count)];
                }
            }

            return PickParityUnshotCell(bot);
        }

        // MEDIUM DIFFICULTY (Default): Systematic Hunt & Target Strategy
        while (bot.BotTargetQueue.Count > 0)
        {
            var next = bot.BotTargetQueue[0];
            bot.BotTargetQueue.RemoveAt(0);

            if (bot.TargetRadar[next.Row, next.Col] == CellState.Empty)
                return next;
        }

        // Parity hunt (checkerboard search)
        return PickParityUnshotCell(bot);
    }

    private Coordinate PickParityUnshotCell(BattleshipPlayer bot)
    {
        var parityCandidates = new List<Coordinate>();
        var allCandidates = new List<Coordinate>();

        for (int r = 0; r < 10; r++)
        {
            for (int c = 0; c < 10; c++)
            {
                if (bot.TargetRadar[r, c] == CellState.Empty)
                {
                    allCandidates.Add(new Coordinate(r, c));
                    if ((r + c) % 2 == 0)
                    {
                        parityCandidates.Add(new Coordinate(r, c));
                    }
                }
            }
        }

        if (parityCandidates.Count > 0)
            return parityCandidates[_rand.Next(parityCandidates.Count)];

        if (allCandidates.Count > 0)
            return allCandidates[_rand.Next(allCandidates.Count)];

        return new Coordinate(0, 0);
    }

    private Coordinate PickRandomUnshotCell(BattleshipPlayer bot)
    {
        var candidates = new List<Coordinate>();
        for (int r = 0; r < 10; r++)
        {
            for (int c = 0; c < 10; c++)
            {
                if (bot.TargetRadar[r, c] == CellState.Empty)
                {
                    candidates.Add(new Coordinate(r, c));
                }
            }
        }

        if (candidates.Count == 0) return new Coordinate(0, 0);
        return candidates[_rand.Next(candidates.Count)];
    }

    public async Task<(bool ok, string? error)> ResignAsync(string gameId, string connectionId)
    {
        var game = GetGame(gameId);
        if (game == null) return (false, "Game not found");

        var resigner = GetPlayerByConnection(game, connectionId);
        if (resigner == null) return (false, "Player not found");

        var winner = resigner.PlayerId == game.Host.PlayerId ? game.Guest! : game.Host;
        game.Phase = BattleshipPhase.Finished;
        game.WinnerPlayerId = winner.PlayerId;
        game.WinnerName = winner.Name;

        await BroadcastFinishedState(game, winner, $"{resigner.Name} resigned");

        return (true, null);
    }

    private async Task BroadcastFinishedState(BattleshipGameState game, BattleshipPlayer winner, string reason)
    {
        var hostEnemyShips = game.Guest?.Ships ?? new List<ShipInstance>();
        var guestEnemyShips = game.Host.Ships;

        if (!string.IsNullOrEmpty(game.Host.ConnectionId) && !game.Host.IsBot)
        {
            await _hubContext.Clients.Client(game.Host.ConnectionId).SendAsync("BattleshipFinished", new
            {
                winnerPlayerId = winner.PlayerId,
                winnerName = winner.Name,
                reason = reason,
                enemyShips = hostEnemyShips
            });
        }

        if (game.Guest != null && !string.IsNullOrEmpty(game.Guest.ConnectionId) && !game.Guest.IsBot)
        {
            await _hubContext.Clients.Client(game.Guest.ConnectionId).SendAsync("BattleshipFinished", new
            {
                winnerPlayerId = winner.PlayerId,
                winnerName = winner.Name,
                reason = reason,
                enemyShips = guestEnemyShips
            });
        }
    }

    private async Task CheckBothReadyAndStartAsync(BattleshipGameState game)
    {
        if (game.Host.IsReady && (game.Guest?.IsReady == true))
        {
            game.Phase = BattleshipPhase.Playing;
            // Host goes first
            game.CurrentTurnPlayerId = game.Host.PlayerId;

            await BroadcastToGame(game.GameId, "BattleshipMatchStarted", new
            {
                firstTurnPlayerId = game.CurrentTurnPlayerId,
                hostName = game.Host.Name,
                guestName = game.Guest.Name
            });
        }
    }

    private bool ValidateFleet(List<PlaceShipDTO> shipDtos, out List<ShipInstance> shipInstances, out CellState[,] grid, out string error)
    {
        shipInstances = new();
        grid = new CellState[10, 10];
        error = string.Empty;

        var requiredTypes = new[] { ShipType.Carrier, ShipType.Battleship, ShipType.Cruiser, ShipType.Submarine, ShipType.Destroyer };
        if (shipDtos.Count != requiredTypes.Length)
        {
            error = "Fleet must contain exactly 5 ships";
            return false;
        }

        foreach (var dto in shipDtos)
        {
            var relative = ShipShapeHelper.GetRelativeCells(dto.Type, dto.IsVertical);
            var occupied = new List<Coordinate>();

            foreach (var rel in relative)
            {
                int r = dto.StartRow + rel.Row;
                int c = dto.StartCol + rel.Col;

                if (r < 0 || r > 9 || c < 0 || c > 9)
                {
                    error = $"Ship {dto.Type} goes out of grid bounds";
                    return false;
                }

                if (grid[r, c] == CellState.Ship)
                {
                    error = $"Ship {dto.Type} overlaps with another ship";
                    return false;
                }

                grid[r, c] = CellState.Ship;
                occupied.Add(new Coordinate(r, c));
            }

            shipInstances.Add(new ShipInstance
            {
                Type = dto.Type,
                StartRow = dto.StartRow,
                StartCol = dto.StartCol,
                IsVertical = dto.IsVertical,
                OccupiedCells = occupied
            });
        }

        return true;
    }

    private void AutoPlaceShips(BattleshipPlayer player)
    {
        var requiredTypes = new[] { ShipType.Carrier, ShipType.Battleship, ShipType.Cruiser, ShipType.Submarine, ShipType.Destroyer };
        var grid = new CellState[10, 10];
        var instances = new List<ShipInstance>();

        foreach (var st in requiredTypes)
        {
            bool placed = false;
            int maxAttempts = 500;

            while (!placed && maxAttempts-- > 0)
            {
                bool isVert = _rand.Next(2) == 0;
                var relative = ShipShapeHelper.GetRelativeCells(st, isVert);

                int maxR = 9 - relative.Max(x => x.Row);
                int maxC = 9 - relative.Max(x => x.Col);
                if (maxR < 0 || maxC < 0) continue;

                int startR = _rand.Next(0, maxR + 1);
                int startC = _rand.Next(0, maxC + 1);

                bool valid = true;
                var cells = new List<Coordinate>();

                foreach (var rel in relative)
                {
                    int r = startR + rel.Row;
                    int c = startC + rel.Col;

                    if (grid[r, c] == CellState.Ship)
                    {
                        valid = false;
                        break;
                    }
                    cells.Add(new Coordinate(r, c));
                }

                if (valid)
                {
                    foreach (var cell in cells)
                    {
                        grid[cell.Row, cell.Col] = CellState.Ship;
                    }
                    instances.Add(new ShipInstance
                    {
                        Type = st,
                        StartRow = startR,
                        StartCol = startC,
                        IsVertical = isVert,
                        OccupiedCells = cells
                    });
                    placed = true;
                }
            }
        }

        player.Grid = grid;
        player.Ships = instances;
        player.IsReady = true;
    }

    public async Task HandleDisconnectionAsync(string connectionId)
    {
        if (_connectionToGameId.TryRemove(connectionId, out var gameId))
        {
            var game = GetGame(gameId);
            if (game != null)
            {
                await BroadcastToGame(gameId, "BattleshipPlayerDisconnected", new { connectionId });
            }
        }
    }

    public async Task HandleReconnectionAsync(string gameId, string playerId, string newConnectionId)
    {
        var game = GetGame(gameId);
        if (game == null) return;

        var player = game.Host.PlayerId == playerId ? game.Host : (game.Guest?.PlayerId == playerId ? game.Guest : null);
        if (player != null)
        {
            player.ConnectionId = newConnectionId;
            _connectionToGameId[newConnectionId] = gameId;

            await BroadcastToGame(gameId, "BattleshipPlayerReconnected", new { playerId });
        }
    }

    private BattleshipPlayer? GetPlayerByConnection(BattleshipGameState game, string connectionId)
    {
        if (game.Host.ConnectionId == connectionId) return game.Host;
        if (game.Guest?.ConnectionId == connectionId) return game.Guest;
        return null;
    }

    private async Task BroadcastToGame(string gameId, string method, object arg)
    {
        var game = GetGame(gameId);
        if (game == null) return;

        var connIds = new List<string>();
        if (!string.IsNullOrEmpty(game.Host.ConnectionId) && !game.Host.IsBot) connIds.Add(game.Host.ConnectionId);
        if (game.Guest != null && !string.IsNullOrEmpty(game.Guest.ConnectionId) && !game.Guest.IsBot) connIds.Add(game.Guest.ConnectionId);

        if (connIds.Count > 0)
        {
            await _hubContext.Clients.Clients(connIds).SendAsync(method, arg);
        }
    }

    private static string GenerateId(int length)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = new Random();
        return new string(Enumerable.Repeat(chars, length).Select(s => s[random.Next(s.Length)]).ToArray());
    }
}
