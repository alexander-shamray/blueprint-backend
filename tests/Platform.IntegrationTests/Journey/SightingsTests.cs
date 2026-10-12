using Shouldly;
using Xunit;

namespace Platform.IntegrationTests.Journey;

public sealed class SightingsTests
{
    [Fact]
    public void A_sighting_is_written_to_the_directory_it_is_given()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sightings-" + Guid.NewGuid().ToString("N"));

        try
        {
            string path = Sightings.Record("payment-double-send-x", "two sends under one key", directory);

            Path.GetDirectoryName(path).ShouldBe(directory);
            File.ReadAllText(path).ShouldBe("two sends under one key");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_named_directory_wins_and_none_falls_back_to_the_temp_path()
    {
        Sightings.Resolve("/ci/sightings").ShouldBe("/ci/sightings");
        Sightings.Resolve(null).ShouldBe(Path.Combine(Path.GetTempPath(), "platform-sightings"));
        Sightings.Resolve("  ").ShouldBe(Sightings.Resolve(null));
    }
}
