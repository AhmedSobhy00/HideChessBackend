namespace HideChessBackend.DTOs;

public class ReconnectRequest
{
    public string GameId   { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
}
