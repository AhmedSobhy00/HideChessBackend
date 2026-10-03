namespace HideChessBackend.DTOs;

public class CreateGameRequest
{
    public string PlayerName { get; set; } = "Anonymous";
    public string GameMode   { get; set; } = "HiddenFormation";
}
