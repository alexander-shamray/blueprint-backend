using Shouldly;
using Xunit;

namespace Platform.IntegrationTests.Journey;

public sealed class SightingsTests
{
    [Fact]
    public void A_sighting_is_written_to_the_directory_the_variable_names()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sightings-" + Guid.NewGuid().ToString("N"));
        string? before = Environment.GetEnvironmentVariable(Sightings.DirectoryVariable);

        try
        {
            Environment.SetEnvironmentVariable(Sightings.DirectoryVariable, directory);

            string path = Sightings.Record("payment-double-send-x", "two sends under one key");

            Path.GetDirectoryName(path).ShouldBe(directory);
            File.ReadAllText(path).ShouldBe("two sends under one key");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Sightings.DirectoryVariable, before);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_sighting_without_a_named_directory_still_becomes_a_file()
    {
        string? before = Environment.GetEnvironmentVariable(Sightings.DirectoryVariable);

        try
        {
            Environment.SetEnvironmentVariable(Sightings.DirectoryVariable, null);

            string path = Sightings.Record("payment-double-send-unnamed", "text");

            File.Exists(path).ShouldBeTrue();
            File.Delete(path);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Sightings.DirectoryVariable, before);
        }
    }
}
