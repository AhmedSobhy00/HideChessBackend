namespace HideChessBackend.DTOs;

public class ChessMoveRequest
{
    public string  GameId    { get; set; } = string.Empty;
    /// <summary>Source square in algebraic notation, e.g. "e2".</summary>
    public string  From      { get; set; } = string.Empty;
    /// <summary>Destination square in algebraic notation, e.g. "e4".</summary>
    public string  To        { get; set; } = string.Empty;
    /// <summary>Promotion piece type. Valid values: "queen", "rook", "bishop", "knight".</summary>
    public string? Promotion { get; set; }
}
