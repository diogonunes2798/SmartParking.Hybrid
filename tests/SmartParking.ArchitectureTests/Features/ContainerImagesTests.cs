using System.Text.RegularExpressions;
using SmartParking.Tests.Common;

namespace SmartParking.ArchitectureTests.Features;

/// <summary>Tests and local environment use the same database images (spec 000, AC-14).</summary>
public sealed partial class ContainerImagesTests
{
    [Fact]
    public void ComposeFile_ComparedWithTestImages_DeclaresTheSameImagesAndTags_AC14()
    {
        // Arrange
        var compose = File.ReadAllText(FindComposeFile());

        // Act
        var composeImages = ImageLine().Matches(compose)
            .Select(match => match.Groups["image"].Value)
            .Order(StringComparer.Ordinal);

        // Assert
        Assert.Equal(ContainerImages.All.Order(StringComparer.Ordinal), composeImages);
    }

    private static string FindComposeFile()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "compose.yaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("compose.yaml was not found above the test output directory.");
    }

    [GeneratedRegex(@"^\s*image:\s*(?<image>\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ImageLine();
}
