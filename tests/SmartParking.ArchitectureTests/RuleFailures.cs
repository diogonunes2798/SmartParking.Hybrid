using TestResult = NetArchTest.Rules.TestResult;

namespace SmartParking.ArchitectureTests;

internal static class RuleFailures
{
    public static string Describe(TestResult result) =>
        result.IsSuccessful
            ? string.Empty
            : "Violating types: " + string.Join(", ", result.FailingTypeNames ?? []);
}
