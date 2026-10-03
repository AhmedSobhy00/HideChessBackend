using HideChessBackend.Enums;

namespace HideChessBackend.Models;

public class PlayerInfo
{
    public string ConnectionId { get; set; } = string.Empty;
    public string PlayerId    { get; set; } = string.Empty;
    public string Name        { get; set; } = "Anonymous";
    public PieceColor Color   { get; set; }
    public bool IsReady       { get; set; }
    public bool IsConnected   { get; set; } = true;
}
