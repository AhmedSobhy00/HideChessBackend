using HideChessBackend.Models;

namespace HideChessBackend.Services;

public interface IGameService
{
    // ── Lobby ─────────────────────────────────────────────────────────────────
    Task<GameState> CreateGameAsync(string connectionId, string playerName, string gameMode = "HiddenFormation");
    Task<(GameState? game, string? error)> JoinGameAsync(string gameId, string connectionId, string playerName);
    Task<(bool success, string? error)> StartMatchAsync(string gameId, string connectionId);

    // ── Setup phase ───────────────────────────────────────────────────────────
    Task StartSetupPhaseAsync(string gameId);
    Task<(bool success, string? error)> MoveSetupPieceAsync(
        string gameId, string connectionId,
        int fromRow, int fromCol, int toRow, int toCol);
    Task<(bool success, string? error)> SetReadyAsync(string gameId, string connectionId);
    Task StartRevealPhaseAsync(string gameId);

    // ── Playing phase ─────────────────────────────────────────────────────────
    Task<(bool success, string? error)> MakeMoveAsync(
        string gameId, string connectionId,
        string from, string to, string? promotion);
    Task<(bool success, string? error)> GetLegalMovesAsync(
        string gameId, string connectionId, string from);

    // ── Game-control actions ──────────────────────────────────────────────────
    Task<(bool success, string? error)> ResignAsync(string gameId, string connectionId);
    Task<(bool success, string? error)> OfferDrawAsync(string gameId, string connectionId);
    Task<(bool success, string? error)> AcceptDrawAsync(string gameId, string connectionId);
    Task<(bool success, string? error)> DeclineDrawAsync(string gameId, string connectionId);

    // ── Connectivity ──────────────────────────────────────────────────────────
    Task HandleDisconnectionAsync(string connectionId);
    Task HandleReconnectionAsync(string gameId, string playerId, string newConnectionId);

    // ── Queries ───────────────────────────────────────────────────────────────
    GameState? GetGame(string gameId);
    GameState? GetGameByConnectionId(string connectionId);
}
