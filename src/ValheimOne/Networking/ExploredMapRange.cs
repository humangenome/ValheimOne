namespace ValheimOne.Networking;

internal readonly struct ExploredMapRange
{
    public ExploredMapRange(int startX, int endX, int y)
    {
        StartX = startX;
        EndX = endX;
        Y = y;
    }

    public int StartX { get; }

    public int EndX { get; }

    public int Y { get; }
}
