using HideChessBackend.DTOs;
using HideChessBackend.Services;
using Microsoft.AspNetCore.SignalR;

namespace HideChessBackend.Hubs;

/// <summary>
/// Thin SignalR hub — routes client calls to <see cref="IGameService"/>.
/// All business logic lives in the service; the hub only handles transport.
/// </summary>
public class GameHub : Hub
{
    private readonly IGameService        _game;
    private readonly IChessService       _chess;
    private readonly ILogger<GameHub>    _log;

    public GameHub(IGameService game, IChessService chess, ILogger<GameHub> log)
    {
        _game  = game;
        _chess = chess;
        _log   = log;
    }

    // ── Lobby ────────────────────────────────────────────────────────────────

    public async Task CreateGame(CreateGameRequest request)
    {
        try
        {
            var g = await _game.CreateGameAsync(
                Context.ConnectionId, request.PlayerName, request.GameMode, request.PreferredColor);

            var creator = g.PlayerWhite?.ConnectionId == Context.ConnectionId ? g.PlayerWhite : g.PlayerBlack;
            await Clients.Caller.SendAsync("GameCreated", new
            {
                gameId    = g.GameId,
                playerId  = creator!.PlayerId,
                yourColor = creator.Color.ToString(),
                yourName  = creator.Name,
                gameMode  = g.GameMode,
                shareUrl  = $"/game/{g.GameId}"
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CreateGame failed");
            await Error("Failed to create game");
        }
    }

    public async Task CreateBotGame(CreateBotGameRequest request)
    {
        try
        {
            var g = await _game.CreateBotGameAsync(
                Context.ConnectionId, request.PlayerName, request.GameMode, request.Difficulty, request.PreferredColor);

            var human = g.PlayerWhite?.ConnectionId == Context.ConnectionId ? g.PlayerWhite : g.PlayerBlack;
            await Clients.Caller.SendAsync("GameCreated", new
            {
                gameId    = g.GameId,
                playerId  = human!.PlayerId,
                yourColor = human.Color.ToString(),
                yourName  = human.Name,
                gameMode  = g.GameMode,
                isBotGame = true,
                shareUrl  = $"/game/{g.GameId}"
            });

            if (g.GameMode == "Classic")
            {
                await _game.StartRevealPhaseAsync(g.GameId);
            }
            else
            {
                await _game.StartSetupPhaseAsync(g.GameId);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CreateBotGame failed");
            await Error("Failed to create bot game");
        }
    }

    public async Task JoinGame(JoinGameRequest request)
    {
        try
        {
            var cleanGameId = request.GameId?.Trim().ToUpperInvariant() ?? string.Empty;
            var (g, err) = await _game.JoinGameAsync(
                cleanGameId, Context.ConnectionId, request.PlayerName);

            if (err != null) { await Error(err); return; }

            var joiner = g!.PlayerWhite?.ConnectionId == Context.ConnectionId ? g.PlayerWhite : g.PlayerBlack;
            var host   = g.PlayerWhite?.ConnectionId == Context.ConnectionId ? g.PlayerBlack : g.PlayerWhite;

            // Tell the joining player their details
            await Clients.Caller.SendAsync("GameJoined", new
            {
                gameId       = g.GameId,
                playerId     = joiner!.PlayerId,
                yourColor    = joiner.Color.ToString(),
                yourName     = joiner.Name,
                opponentName = host!.Name,
                gameMode     = g.GameMode
            });

            // Tell the waiting player someone joined
            await Clients.Client(host.ConnectionId).SendAsync("PlayerJoined", new
            {
                opponentName = joiner.Name,
                color        = joiner.Color.ToString(),
                gameMode     = g.GameMode
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "JoinGame failed");
            await Error("Failed to join game");
        }
    }

    public async Task StartMatch(string gameId)
    {
        var (ok, err) = await _game.StartMatchAsync(gameId, Context.ConnectionId);
        if (!ok) await Error(err!);
    }

    // ── Setup phase ──────────────────────────────────────────────────────────

    public async Task MoveSetupPiece(SetupMoveRequest request)
    {
        var (ok, err) = await _game.MoveSetupPieceAsync(
            request.GameId, Context.ConnectionId,
            request.FromRow, request.FromCol, request.ToRow, request.ToCol);
        if (!ok) await Error(err!);
    }

    public async Task SetReady(string gameId)
    {
        var (ok, err) = await _game.SetReadyAsync(gameId, Context.ConnectionId);
        if (!ok) await Error(err!);
    }

    // ── Playing phase ────────────────────────────────────────────────────────

    public async Task MakeMove(ChessMoveRequest request)
    {
        var (ok, err) = await _game.MakeMoveAsync(
            request.GameId, Context.ConnectionId, request.From, request.To, request.Promotion);
        if (!ok) await Error(err!);
    }

    /// <summary>
    /// Client calls this after selecting a piece to get highlighted legal-move squares.
    /// Server returns a "LegalMoves" event with the list of valid destinations.
    /// </summary>
    public async Task GetLegalMoves(string gameId, string from)
    {
        var (ok, err) = await _game.GetLegalMovesAsync(gameId, Context.ConnectionId, from);
        if (!ok) await Error(err!);
    }

    // ── Game-control actions ─────────────────────────────────────────────────

    public async Task Resign(string gameId)
    {
        var (ok, err) = await _game.ResignAsync(gameId, Context.ConnectionId);
        if (!ok) await Error(err!);
    }

    public async Task OfferDraw(string gameId)
    {
        var (ok, err) = await _game.OfferDrawAsync(gameId, Context.ConnectionId);
        if (!ok) await Error(err!);
    }

    public async Task AcceptDraw(string gameId)
    {
        var (ok, err) = await _game.AcceptDrawAsync(gameId, Context.ConnectionId);
        if (!ok) await Error(err!);
    }

    public async Task DeclineDraw(string gameId)
    {
        var (ok, err) = await _game.DeclineDrawAsync(gameId, Context.ConnectionId);
        if (!ok) await Error(err!);
    }

    // ── Connectivity ─────────────────────────────────────────────────────────

    public async Task Reconnect(ReconnectRequest request)
    {
        await _game.HandleReconnectionAsync(
            request.GameId, request.PlayerId, Context.ConnectionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _game.HandleDisconnectionAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task Error(string message)
        => Clients.Caller.SendAsync("Error", new { message });
}
