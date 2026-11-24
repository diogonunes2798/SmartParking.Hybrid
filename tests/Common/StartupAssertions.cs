using Microsoft.Extensions.Options;
using SmartParking.Infrastructure;

namespace SmartParking.Tests.Common;

internal static class StartupAssertions
{
    /// <summary>Starting the host must fail with an options validation error that names Persistence:Mode.</summary>
    public static void AssertStartupFailsOnPersistenceMode(Action startHost)
    {
        var exception = Record.Exception(startHost);

        Assert.NotNull(exception);
        var validation = Unwrap(exception).OfType<OptionsValidationException>().FirstOrDefault();
        Assert.NotNull(validation);
        Assert.Contains(PersistenceOptions.ModeKey, validation.Message, StringComparison.Ordinal);
    }

    private static IEnumerable<Exception> Unwrap(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Unwrap))
                {
                    yield return inner;
                }
            }
        }
    }
}
