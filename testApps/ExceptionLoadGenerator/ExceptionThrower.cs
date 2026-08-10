////////////////////////////////////////////////////////////////////////////////
// Module: ExceptionThrower.cs
//
// Notes:
// Throws a rotating mix of distinct exception types from distinct, several-
// frames-deep named call chains, catching each one immediately so the
// process keeps running - this is what generates real CLR ExceptionThrown_V1
// events (Microsoft-Windows-DotNETRuntime provider, event ID 80) for
// nettraceParser's exception-event feature to decode against. Covers a
// plain throw, a caught-and-rethrown throw (ExceptionFlags.ReThrown), and a
// throw-with-inner-exception (ExceptionFlags.HasInnerException/Nested) so
// all three flag bits have real coverage in the captured trace.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.ExceptionLoadGenerator {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;

public static class ExceptionThrower
{
    public static void Run(int iterationCount)
    {
        for (int iteration = 0; iteration < iterationCount; ++iteration)
        {
            try
            {
                switch (iteration % 4)
                {
                    case 0:
                        LevelOneInvalidOperation();
                        break;

                    case 1:
                        LevelOneArgument("widgetId");
                        break;

                    case 2:
                        LevelOneWidgetNotFound(iteration);
                        break;

                    case 3:
                        LevelOneRethrow();
                        break;
                }
            }
            catch (Exception)
            {
                // Swallowed deliberately - the point of this loop is the
                // CLR's own ExceptionThrown_V1 event firing at throw time,
                // not observing the exception here.
            }
        }

        Console.WriteLine($"Threw {iterationCount} exceptions.");
    }

    private static void LevelOneInvalidOperation()
    {
        LevelTwoInvalidOperation();
    }

    private static void LevelTwoInvalidOperation()
    {
        LevelThreeInvalidOperation();
    }

    private static void LevelThreeInvalidOperation()
    {
        throw new InvalidOperationException("Widget cache is not initialized.");
    }

    private static void LevelOneArgument(string paramName)
    {
        LevelTwoArgument(paramName);
    }

    private static void LevelTwoArgument(string paramName)
    {
        throw new ArgumentException("Widget id must be non-empty.", paramName);
    }

    private static void LevelOneWidgetNotFound(int widgetId)
    {
        LevelTwoWidgetNotFound(widgetId);
    }

    private static void LevelTwoWidgetNotFound(int widgetId)
    {
        LevelThreeWidgetNotFound(widgetId);
    }

    private static void LevelThreeWidgetNotFound(int widgetId)
    {
        try
        {
            throw new InvalidOperationException($"Backing store lookup failed for widget {widgetId}.");
        }
        catch (Exception innerException)
        {
            throw new WidgetNotFoundException($"Widget {widgetId} was not found.", innerException);
        }
    }

    private static void LevelOneRethrow()
    {
        try
        {
            LevelTwoRethrowSource();
        }
        catch (Exception)
        {
            throw;
        }
    }

    private static void LevelTwoRethrowSource()
    {
        throw new ArgumentException("Simulated transient failure.");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.ExceptionLoadGenerator)
