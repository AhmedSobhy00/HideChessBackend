using HideChessBackend.Enums;

namespace HideChessBackend.Models;

/// <summary>
/// Represents one chess piece on the board.
/// Row 0 = rank 1 (White's back rank). Row 7 = rank 8 (Black's back rank).
/// Col 0 = file a. Col 7 = file h.
/// </summary>
public class ChessPieceInfo
{
    public PieceType  Type  { get; set; }
    public PieceColor Color { get; set; }
    public int        Row   { get; set; }
    public int        Col   { get; set; }
}
