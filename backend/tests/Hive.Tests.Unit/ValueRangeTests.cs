using Hive.Modules.Devices;

namespace Hive.Tests.Unit;

public class ValueRangeTests
{
    [Fact]
    public void A_range_needs_a_valid_device_and_metric_and_at_least_one_end_in_order()
    {
        Assert.Null(ValueRangeStore.Validate("meteo-out1", "temperature", new ValueRangeRequest(18, 26)));
        Assert.Null(ValueRangeStore.Validate("meteo-out1", "temperature", new ValueRangeRequest(null, 26)));
        Assert.Null(ValueRangeStore.Validate("meteo-out1", "humidity", new ValueRangeRequest(30, null)));
        Assert.NotNull(ValueRangeStore.Validate("meteo-out1", "temperature", new ValueRangeRequest(null, null)));
        Assert.NotNull(ValueRangeStore.Validate("meteo-out1", "temperature", new ValueRangeRequest(26, 18)));
        Assert.NotNull(ValueRangeStore.Validate("meteo-out1", "temperature", new ValueRangeRequest(20, 20)));
        Assert.NotNull(ValueRangeStore.Validate("Bad Id", "temperature", new ValueRangeRequest(1, 2)));
        Assert.NotNull(ValueRangeStore.Validate("meteo-out1", "Temp/erature", new ValueRangeRequest(1, 2)));
        Assert.NotNull(ValueRangeStore.Validate("meteo-out1", "temperature", new ValueRangeRequest(double.NaN, 2)));
    }
}
