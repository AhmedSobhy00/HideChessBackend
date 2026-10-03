namespace HideChessBackend.DTOs;

public class JoinGameRequest
{
    public string GameId     { get; set; } = string.Empty;
    public string PlayerName { get; set; } = "Anonymous";
}
