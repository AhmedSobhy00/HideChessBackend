using HideChessBackend.Models;

namespace HideChessBackend.Services;

/// <summary>
/// Return value from <see cref="IChessService.ApplyMove"/>.
/// </summary>
public class MoveApplicationResult
{
    public bool    Success    { get; set; }
    public string? Error      { get; set; }

    public Dictionary<(int row, int col), ChessPieceInfo> NewBoard { get; set; } = new();
    public ChessPieceInfo? CapturedPiece           { get; set; }
    public (int row, int col)? NewEnPassantTarget   { get; set; }
    public bool IsPromotion { get; set; }
    public bool IsEnPassant { get; set; }
}
