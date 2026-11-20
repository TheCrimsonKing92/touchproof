public sealed class DependencyIndexMeasureFloorProbeTests
{
    [Fact]
    public void Collect_process_floor() => DependencyIndexMeasurementProbe.Measure("floor", _ => 0);
}
