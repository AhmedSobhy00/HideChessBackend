namespace HideChessBackend.DTOs;

public class SetupMoveRequest
{
    public string GameId  { get; set; } = string.Empty;
    public int    FromRow { get; set; }
    public int    FromCol { get; set; }
    public int    ToRow   { get; set; }
    public int    ToCol   { get; set; }
}
