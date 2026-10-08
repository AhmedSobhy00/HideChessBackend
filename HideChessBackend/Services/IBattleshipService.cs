using HideChessBackend.DTOs;
using HideChessBackend.Models.Battleship;

namespace HideChessBackend.Services;

public interface IBattleshipService
{
    Task<BattleshipGameState> CreateGameAsync(string connectionId, string playerName);
    Task<BattleshipGameState> CreateBotGameAsync(string connectionId, string playerName, string difficulty);
    Task<(BattleshipGameState? game, string? error)> JoinGameAsync(string gameId, string connectionId, string playerName);
    Task<(bool ok, string? error)> PlaceFleetAsync(string gameId, string connectionId, List<PlaceShipDTO> ships);
    Task<(bool ok, string? error, List<PlaceShipDTO>? ships)> RandomizeFleetAsync(string gameId, string connectionId);
    Task<(bool ok, string? error)> SetReadyAsync(string gameId, string connectionId);
    Task<(bool ok, string? error)> FireShotAsync(string gameId, string connectionId, int row, int col);
    Task<(bool ok, string? error)> ResignAsync(string gameId, string connectionId);
    Task HandleDisconnectionAsync(string connectionId);
    Task HandleReconnectionAsync(string gameId, string playerId, string newConnectionId);
    BattleshipGameState? GetGame(string gameId);
}
