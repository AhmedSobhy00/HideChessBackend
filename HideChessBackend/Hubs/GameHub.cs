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
            var g = await _game.CreateGameAsync(Context.ConnectionId, request.PlayerName);
            await Clients.Caller.SendAsync("GameCreated", new
            {
                gameId    = g.GameId,
                playerId  = g.PlayerWhite!.PlayerId,
                yourColor = "White",
                yourName  = g.PlayerWhite.Name,
                shareUrl  = $"/game/{g.GameId}"
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CreateGame failed");
            await Error("Failed to create game");
        }
    }

    public async Task JoinGame(JoinGameRequest request)
    {
        try
        {
            var (g, err) = await _game.JoinGameAsync(
                request.GameId, Context.ConnectionId, request.PlayerName);

            if (err != null) { await Error(err); return; }

            // Tell the joining player their details
            await Clients.Caller.SendAsync("GameJoined", new
            {
                gameId       = g!.GameId,
                playerId     = g.PlayerBlack!.PlayerId,
                yourColor    = "Black",
                yourName     = g.PlayerBlack.Name,
                opponentName = g.PlayerWhite!.Name
            });

            // Tell the waiting (white) player someone joined
            await Clients.Client(g.PlayerWhite.ConnectionId).SendAsync("PlayerJoined", new
            {
                opponentName = g.PlayerBlack.Name,
                color        = "Black"
            });

            // Start the 60-second hidden setup phase
            await _game.StartSetupPhaseAsync(g.GameId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "JoinGame failed");
            await Error("Failed to join game");
        }
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
