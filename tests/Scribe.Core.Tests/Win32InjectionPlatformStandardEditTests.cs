using Scribe.Core.TextInjection;
using Xunit;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

/// <summary>
/// What the production platform makes of EM_REPLACESEL's answer. Whether an Edit control took the text can only be seen in a
/// live window, which nothing here touches: the decision is pinned over the results Windows gives, and the one real call
/// made goes to a window handle that cannot exist, so it can reach no window and insert nothing.
/// </summary>
public sealed class Win32InjectionPlatformStandardEditTests
{
    private const int ErrorTimeout = 1460;

    // The expected outcome is passed by name: the enum is internal, and a public test method cannot take it.
    [Theory]
    [InlineData(1, 0, nameof(StandardEditInsertion.Inserted))]
    [InlineData(1, ERROR_ACCESS_DENIED, nameof(StandardEditInsertion.Inserted))] // a code an earlier call left, beside an answer
    [InlineData(0, 0, nameof(StandardEditInsertion.Unconfirmed))] // ERROR_SUCCESS: Microsoft's "generic failure"
    [InlineData(0, ErrorTimeout, nameof(StandardEditInsertion.Unconfirmed))]
    [InlineData(0, ERROR_ACCESS_DENIED, nameof(StandardEditInsertion.Declined))] // how UIPI blocks a message
    [InlineData(0, ERROR_INVALID_WINDOW_HANDLE, nameof(StandardEditInsertion.Declined))]
    [InlineData(0, 87, nameof(StandardEditInsertion.Unconfirmed))] // a code nobody listed: the text may be there
    public void An_answer_is_an_insertion_and_only_the_two_failures_reported_before_queuing_are_refusals(
        int answered, int lastError, string expected) =>
        Assert.Equal(expected, Win32InjectionPlatform.ClassifyEditMessage(answered, lastError).ToString());

    [Fact]
    public void A_window_handle_that_cannot_exist_is_a_refusal_read_from_the_real_last_error()
    {
        // No window has the handle 0. Windows says so through the last error the import captures, which is what separates
        // this refusal from a time-out that leaves the text possibly inserted.
        Assert.Equal(StandardEditInsertion.Declined, Win32InjectionPlatform.SendReplaceSelection(0, "never delivered"));
    }
}
