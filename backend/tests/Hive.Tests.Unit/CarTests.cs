using Hive.Modules.Devices;

namespace Hive.Tests.Unit;

/// <summary>Car rules without a car: what a drive and a wiring may be.</summary>
public sealed class CarTests
{
    [Fact]
    public void A_drive_is_two_speeds_from_minus_100_to_100()
    {
        Assert.Null(CarDrive.Invalid(new DriveRequest(-100, 100)));
        Assert.Null(CarDrive.Invalid(new DriveRequest(0, 0)));
        Assert.NotNull(CarDrive.Invalid(new DriveRequest(101, 0)));
        Assert.NotNull(CarDrive.Invalid(new DriveRequest(0, -101)));
    }

    [Fact]
    public void The_wiring_uses_four_different_free_pins_and_another_for_the_light()
    {
        Assert.Null(CarSetup.Validate(new CarSetupDto()));
        Assert.Null(CarSetup.Validate(new CarSetupDto([12, 13, 14, 15], LightPin: 2)));
        Assert.NotNull(CarSetup.Validate(new CarSetupDto([14, 15, 13])));
        Assert.NotNull(CarSetup.Validate(new CarSetupDto([14, 15, 13, 13])));
        Assert.NotNull(CarSetup.Validate(new CarSetupDto([14, 15, 13, 4])));      // the light's pin
        Assert.NotNull(CarSetup.Validate(new CarSetupDto([14, 15, 13, 0])));      // the camera's clock
        Assert.NotNull(CarSetup.Validate(new CarSetupDto([14, 15, 13, 16])));     // PSRAM
        Assert.NotNull(CarSetup.Validate(new CarSetupDto(MaxSpeed: 5)));
        Assert.NotNull(CarSetup.Validate(new CarSetupDto(MaxRunS: 0)));
    }
}
