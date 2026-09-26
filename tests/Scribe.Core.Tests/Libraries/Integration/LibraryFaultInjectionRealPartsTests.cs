using Scribe.Core.Tests.Libraries.Storage;

namespace Scribe.Core.Tests.Libraries.Integration;

/// <summary>
/// J-1's fault suite with the real parts (contract 9.3): every interruption of a Save that touches every operation kind,
/// of its settings commit and of recovery, with C's composer, O's overlay and X's codec building and reading the files and
/// the local state, and O's edits documents, instead of J's doubles.
/// </summary>
public sealed class LibraryFaultInjectionRealPartsTests : LibraryFaultInjectionSuite
{
    protected override bool RealParts => true;
}
