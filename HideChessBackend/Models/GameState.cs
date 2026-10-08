using HideChessBackend.Enums;

namespace HideChessBackend.Models;

/// <summary>
/// The authoritative server-side game state.
/// Kept in memory; never fully exposed to any single client.
/// </summary>
public class GameState
{
    // ── Identity ─────────────────────────────────────────────────────────────
    public string GameId        { get; set; } = string.Empty;
    public string GameMode      { get; set; } = "HiddenFormation";
    public string OwnerPlayerId { get; set; } = string.Empty;
    public PlayerInfo? PlayerWhite { get; set; }
    public PlayerInfo? PlayerBlack { get; set; }

    // ── Phase ─────────────────────────────────────────────────────────────────
    public GamePhase Phase     { get; set; } = GamePhase.WaitingForPlayers;
    public DateTime SetupEndsAt { get; set; }

    // ── Setup boards (kept separate to enforce privacy) ───────────────────────
    // Key = (row, col). Only populated during Setup phase.
    public Dictionary<(int row, int col), ChessPieceInfo> WhiteSetupBoard { get; set; } = new();
    public Dictionary<(int row, int col), ChessPieceInfo> BlackSetupBoard { get; set; } = new();

    // ── Playing board (combined after Reveal) ────────────────────────────────
    public Dictionary<(int row, int col), ChessPieceInfo> Board { get; set; } = new();

    // ── Chess state ───────────────────────────────────────────────────────────
    public PieceColor CurrentTurn { get; set; } = PieceColor.White;

    /// <summary>En-passant target square in algebraic notation ("e3"), or null.</summary>
    public string? EnPassantTarget { get; set; }

    /// <summary>Resets on pawn move or capture; draw at 100 (50 full moves).</summary>
    public int HalfMoveClock { get; set; }
    public int FullMoveNumber { get; set; } = 1;

    /// <summary>Position hash → occurrence count; used for threefold-repetition detection.</summary>
    public Dictionary<string, int> PositionHistory { get; set; } = new();

    // ── Result ────────────────────────────────────────────────────────────────
    public PieceColor? Winner { get; set; }
    public GameResult  Result { get; set; } = GameResult.InProgress;

    // ── Draw-offer tracking ───────────────────────────────────────────────────
    public PieceColor? DrawOfferedBy { get; set; }

    // ── History ───────────────────────────────────────────────────────────────
    public List<string> MoveHistory { get; set; } = new();
    public List<string> SanMoveHistory { get; set; } = new();

    // ── Timestamps ───────────────────────────────────────────────────────────
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── Concurrency (not serialised) ─────────────────────────────────────────
    /// <summary>Per-game lock — prevents concurrent state mutations.</summary>
    public SemaphoreSlim Lock { get; } = new SemaphoreSlim(1, 1);

    // ── Timer handles (not serialised) ───────────────────────────────────────
    public CancellationTokenSource? SetupTimerCts        { get; set; }
    public CancellationTokenSource? DisconnectionTimerCts { get; set; }
}
