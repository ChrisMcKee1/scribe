using Scribe.Core.Infrastructure;

namespace Scribe.Core.Tests;

public class ResilientEventTests
{
    [Fact]
    public void AHandlerThatThrowsDoesNotStopTheOnesRegisteredAfterIt()
    {
        var reached = new List<string>();
        Action<int>? handlers = null;
        handlers += _ => reached.Add("first");
        handlers += _ => throw new InvalidOperationException("tray icon was disposed");
        handlers += _ => reached.Add("overlay");

        ResilientEvent.InvokeAll(handlers, 1);

        Assert.Equal(["first", "overlay"], reached);
    }

    [Fact]
    public void EveryFailureIsReported()
    {
        var errors = new List<string>();
        Action<int>? handlers = null;
        handlers += _ => throw new InvalidOperationException("one");
        handlers += _ => throw new InvalidOperationException("two");

        ResilientEvent.InvokeAll(handlers, 0, ex => errors.Add(ex.Message));

        Assert.Equal(["one", "two"], errors);
    }

    [Fact]
    public void HandlersReceiveTheArgument()
    {
        var seen = new List<string>();
        Action<string>? handlers = null;
        handlers += value => seen.Add(value);
        handlers += value => seen.Add(value + "!");

        ResilientEvent.InvokeAll(handlers, "recording");

        Assert.Equal(["recording", "recording!"], seen);
    }

    [Fact]
    public void NoSubscribersIsNotAnError() =>
        ResilientEvent.InvokeAll<int>(null, 0, _ => throw new InvalidOperationException());

    [Fact]
    public void AReporterThatThrowsStillLetsTheRemainingHandlersRun()
    {
        var reached = 0;
        Action<int>? handlers = null;
        handlers += _ => throw new InvalidOperationException();
        handlers += _ => reached++;

        ResilientEvent.InvokeAll(handlers, 0, _ => throw new InvalidOperationException("logger died"));

        Assert.Equal(1, reached);
    }
}
