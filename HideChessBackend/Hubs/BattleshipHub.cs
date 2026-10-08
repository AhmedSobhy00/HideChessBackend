using HideChessBackend.DTOs;
using HideChessBackend.Services;
using Microsoft.AspNetCore.SignalR;

namespace HideChessBackend.Hubs;

public class BattleshipHub : Hub
{
    private readonly IBattleshipService _battleship;
    private readonly ILogger<BattleshipHub> _log;

    public BattleshipHub(IBattleshipService battleship, ILogger<BattleshipHub> log)
    {
        _battleship = battleship;
        _log = log;
    }

    public async Task CreateBattleshipGame(CreateBattleshipGameRequest request)
    {
        try
        {
            var g = await _battleship.CreateGameAsync(Context.ConnectionId, request.PlayerName);
            await Clients.Caller.SendAsync("BattleshipGameCreated", new
            {
                gameId = g.GameId,
                playerId = g.Host.PlayerId,
                yourName = g.Host.Name,
                isHost = true,
                shareUrl = $"/battleship/game/{g.GameId}"
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CreateBattleshipGame failed");
            await Error("Failed to create Battleship game");
        }
    }

    public async Task CreateBattleshipBotGame(CreateBattleshipBotRequest request)
    {
        try
        {
            var g = await _battleship.CreateBotGameAsync(Context.ConnectionId, request.PlayerName, request.Difficulty);
            await Clients.Caller.SendAsync("BattleshipGameCreated", new
            {
                gameId = g.GameId,
                playerId = g.Host.PlayerId,
                yourName = g.Host.Name,
                opponentName = g.Guest?.Name,
                isHost = true,
                isBotGame = true,
                shareUrl = $"/battleship/game/{g.GameId}"
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CreateBattleshipBotGame failed");
            await Error("Failed to create Battleship bot game");
        }
    }

    public async Task JoinBattleshipGame(JoinBattleshipGameRequest request)
    {
        try
        {
            var cleanId = request.GameId?.Trim().ToUpperInvariant() ?? string.Empty;
            var (g, err) = await _battleship.JoinGameAsync(cleanId, Context.ConnectionId, request.PlayerName);
            if (err != null) { await Error(err); return; }

            var joiner = g!.Guest!;
            var host = g.Host;

            await Clients.Caller.SendAsync("BattleshipGameJoined", new
            {
                gameId = g.GameId,
                playerId = joiner.PlayerId,
                yourName = joiner.Name,
                opponentName = host.Name,
                isHost = false
            });

            await Clients.Client(host.ConnectionId).SendAsync("BattleshipPlayerJoined", new
            {
                opponentName = joiner.Name
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "JoinBattleshipGame failed");
            await Error("Failed to join Battleship game");
        }
    }

    public async Task PlaceFleet(PlaceFleetRequest request)
    {
        var (ok, err) = await _battleship.PlaceFleetAsync(request.GameId, Context.ConnectionId, request.Ships);
        if (!ok) await Error(err!);
    }

    public async Task RandomizeFleet(string gameId)
    {
        var (ok, err, ships) = await _battleship.RandomizeFleetAsync(gameId, Context.ConnectionId);
        if (!ok) { await Error(err!); return; }

        await Clients.Caller.SendAsync("BattleshipFleetRandomized", new { ships });
    }

    public async Task SetFleetReady(string gameId)
    {
        var (ok, err) = await _battleship.SetReadyAsync(gameId, Context.ConnectionId);
        if (!ok) await Error(err!);
    }

    public async Task FireShot(FireShotRequest request)
    {
        var (ok, err) = await _battleship.FireShotAsync(request.GameId, Context.ConnectionId, request.TargetRow, request.TargetCol);
        if (!ok) await Error(err!);
    }

    public async Task ResignBattleship(string gameId)
    {
        var (ok, err) = await _battleship.ResignAsync(gameId, Context.ConnectionId);
        if (!ok) await Error(err!);
    }

    public async Task ReconnectBattleship(string gameId, string playerId)
    {
        await _battleship.HandleReconnectionAsync(gameId, playerId, Context.ConnectionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _battleship.HandleDisconnectionAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private Task Error(string message)
        => Clients.Caller.SendAsync("Error", new { message });
}
