namespace HideChessBackend.DTOs;

public class CreateBotGameRequest
{
    public string PlayerName     { get; set; } = "Anonymous";
    public string GameMode       { get; set; } = "HiddenFormation";
    public string Difficulty     { get; set; } = "Medium";
    public string PreferredColor { get; set; } = "Random";
}
