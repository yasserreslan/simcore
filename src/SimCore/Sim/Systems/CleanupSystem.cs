namespace SimCore.Sim;

// The cleanup system: dead units leave the world at the end of the tick.
public sealed partial class World
{
    private void RunCleanup()
    {
        RecordAirLosses(); // the aircraft lost this tick and what brought them down (E6)
        _units.RemoveAll(u => u.HP <= 0);
    }
}
