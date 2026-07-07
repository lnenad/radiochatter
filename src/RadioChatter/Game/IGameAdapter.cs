namespace RadioChatter.Game
{
    internal interface IGameAdapter
    {
        bool TryBuildSnapshot(Snapshot snapshot);
    }
}
