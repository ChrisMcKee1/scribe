using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Vocabulary;

/// <summary>
/// What a save would store, written as a sequence of framed values and hashed: the draft a caller such as the Settings
/// window hands <see cref="StoredChangeAcknowledgement.Watch"/>. Every value is written as a token that starts with a tag
/// and says where it ends (text and part names carry their length in UTF-16 code units, lists and profiles their count,
/// numbers end at a terminator no number contains), so the written form is a prefix-free code: two different sequences of
/// values never write the same characters, whatever the values contain. No delimiter a user can type joins or splits a
/// value, and null differs from empty. The hash is taken over the UTF-16 code units themselves, so nothing in the text is
/// replaced on the way in (round 4, A8).
/// </summary>
/// <remarks>
/// The components a save computes beyond a plain editor are written here, the way the save stores them, so what they carry
/// is tested in Core: a hotkey binding field by field, the microphone as it is normalized when stored, the Azure
/// subscription's stored fields, the profiles field by field and the set of enabled libraries.
/// </remarks>
public sealed class DraftSnapshot
{
    private readonly StringBuilder _framed = new();

    /// <summary>Names the part of the draft that follows (a page, a component), so parts never run into one another.</summary>
    public DraftSnapshot Part(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _framed.Append('p');
        return Framed(name);
    }

    /// <summary>A text value, or null, which differs from empty.</summary>
    public DraftSnapshot Text(string? value)
    {
        if (value is null)
        {
            return Null();
        }

        _framed.Append('s');
        return Framed(value);
    }

    public DraftSnapshot Flag(bool value)
    {
        _framed.Append(value ? 't' : 'f');
        return this;
    }

    public DraftSnapshot Flag(bool? value) => value is { } flag ? Flag(flag) : Null();

    public DraftSnapshot Number(long value)
    {
        _framed.Append('i').Append(value.ToString(CultureInfo.InvariantCulture)).Append(';');
        return this;
    }

    public DraftSnapshot Number(long? value) => value is { } number ? Number(number) : Null();

    /// <summary>A floating-point value by its exact bits, or null.</summary>
    public DraftSnapshot Number(double? value)
    {
        if (value is not { } number)
        {
            return Null();
        }

        _framed.Append('d').Append(BitConverter.DoubleToInt64Bits(number).ToString(CultureInfo.InvariantCulture)).Append(';');
        return this;
    }

    /// <summary>A list of text values, count first.</summary>
    public DraftSnapshot List(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var items = values.ToList();
        _framed.Append('l').Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append(':');
        foreach (var item in items)
        {
            Text(item);
        }

        return this;
    }

    /// <summary>A hotkey binding, every field a save stores, or no binding.</summary>
    public DraftSnapshot Binding(HotkeyBinding? binding)
    {
        if (binding is null)
        {
            return Null();
        }

        _framed.Append('b');
        return Number(binding.VirtualKey)
            .Number((long)binding.Modifiers)
            .Number((long)binding.Mode)
            .Flag(binding.Suppress)
            .Text(binding.DisplayName)
            .Number(binding.SecondaryVirtualKey)
            .Flag(binding.SuppressChordMembers);
    }

    /// <summary>A microphone choice, normalized as a save stores it (a blank ID is the Windows default, with no name).</summary>
    public DraftSnapshot Microphone(MicrophoneSelection selection)
    {
        var stored = MicrophoneSelection.Normalize(selection.DeviceId, selection.DeviceName);
        _framed.Append('m');
        return Text(stored.DeviceId).Text(stored.DeviceName);
    }

    /// <summary>The Azure subscription fields a save stores (its ID, name and tenant), or no subscription.</summary>
    public DraftSnapshot Subscription(AzureSubscription? subscription)
    {
        if (subscription is null)
        {
            return Null();
        }

        _framed.Append('a');
        return Text(subscription.Id).Text(subscription.Name).Text(subscription.TenantId);
    }

    /// <summary>The per-app profiles as a save stores them, count first, each field by field.</summary>
    public DraftSnapshot Profiles(IReadOnlyList<AppProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _framed.Append('r').Append(profiles.Count.ToString(CultureInfo.InvariantCulture)).Append(':');
        foreach (var profile in profiles)
        {
            Text(profile.Name)
                .List(profile.ProcessNames)
                .Text(profile.WritingStyle)
                .Number(profile.NewlineHandling is { } mode ? (long?)(long)mode : null);
        }

        return this;
    }

    /// <summary>
    /// The enabled libraries as the set a save writes: ids compare without regard to case, as the library list matches them,
    /// and their order is not part of what is stored (a save writes them in precedence order).
    /// </summary>
    public DraftSnapshot LibrarySet(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return List(ids.Select(id => id.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    /// <summary>The draft's SHA-256, over its UTF-16 code units, as 64 upper-case hex digits.</summary>
    public string Hash() => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(_framed.ToString().AsSpan())));

    private DraftSnapshot Null()
    {
        _framed.Append('n');
        return this;
    }

    private DraftSnapshot Framed(string value)
    {
        _framed.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        return this;
    }
}
