using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.Logging;
using Scribe.Core.TextInjection;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

/// <summary>
/// Deterministic stand-ins for the clipboard, input and logging boundaries of text injection. Nested in
/// one type so they cannot collide with helpers other test files define.
/// </summary>
internal static class TextInjectionFakes
{
    internal const uint CF_DIB = 8;
    internal const uint CF_LOCALE = 16;
    internal const uint CF_OEMTEXT = 7;
    internal const uint CF_TEXT = 1;

    /// <summary>
    /// A scripted clipboard that models the Win32 contract the borrower relies on: only the thread
    /// holding it open can read or change it, every EmptyClipboard and SetClipboardData moves the
    /// sequence number, and a text write gains Windows' synthesized companions when it is closed.
    /// Other applications act only while Scribe does not hold it, because OpenClipboard guarantees
    /// exactly that; the fake throws if a test scripts an impossible interleaving.
    /// </summary>
    internal sealed class Clipboard : IClipboardNative
    {
        private readonly List<(uint Format, byte[] Data)> _formats = [];
        private readonly Dictionary<string, uint> _registered = new(StringComparer.OrdinalIgnoreCase);
        private bool _changedInSession;

        public uint Sequence { get; private set; } = 5000;

        public bool IsHeldByScribe { get; private set; }

        /// <summary>Every call Scribe made, in order, for ordering and atomicity assertions.</summary>
        public List<string> Trace { get; } = [];

        public int OpenAttempts { get; private set; }

        public int Closes { get; private set; }

        /// <summary>Decides each OpenClipboard attempt by its 1-based cumulative number. Default: succeed.</summary>
        public Func<int, bool>? OpenAttemptSucceeds { get; set; }

        /// <summary>Runs while an attempt fails, which is when another application holds the clipboard.</summary>
        public Action<int>? WhileOpenFails { get; set; }

        /// <summary>
        /// Runs right after each CloseClipboard, by its 1-based cumulative number, before Scribe's next
        /// call of any kind. That is the first moment another application can write, so a test can land
        /// a copy between a release and the sequence number read that follows it.
        /// </summary>
        public Action<int>? AfterClose { get; set; }

        public Func<string, bool>? SetTextSucceeds { get; set; }

        public Func<uint, bool>? SetDataSucceeds { get; set; }

        public bool ReadTextFails { get; set; }

        public bool EmptyFails { get; set; }

        /// <summary>Unproven platform variant: CloseClipboard after a change moves the sequence number.</summary>
        public bool CloseMovesSequence { get; set; }

        /// <summary>Unproven platform variant: rendering a synthesized format on demand moves it.</summary>
        public bool SynthesizedReadMovesSequence { get; set; }

        public uint SequenceNumber => Sequence;

        public int FormatCount => _formats.Count;

        public string? Text => Find(CF_UNICODETEXT) is { } data ? Decode(data) : null;

        public bool IsFormatAvailable(uint format) => Find(format) is not null;

        public bool Has(string formatName) => IsFormatAvailable(RegisterFormat(formatName));

        public uint RegisterFormat(string name)
        {
            if (!_registered.TryGetValue(name, out var id))
            {
                id = 0xC000u + (uint)_registered.Count;
                _registered[name] = id;
            }

            return id;
        }

        public bool TryOpen()
        {
            if (IsHeldByScribe)
            {
                throw new InvalidOperationException("Scribe opened a clipboard it already holds.");
            }

            OpenAttempts++;
            if (!(OpenAttemptSucceeds?.Invoke(OpenAttempts) ?? true))
            {
                Trace.Add("open-failed");
                WhileOpenFails?.Invoke(OpenAttempts);
                return false;
            }

            IsHeldByScribe = true;
            _changedInSession = false;
            Trace.Add("open");
            return true;
        }

        public void Close()
        {
            RequireHeld();
            Trace.Add("close");
            IsHeldByScribe = false;
            EndSession(_changedInSession);
            Closes++;
            AfterClose?.Invoke(Closes);
        }

        public bool Empty()
        {
            RequireHeld();
            Trace.Add("empty");
            if (EmptyFails)
            {
                return false;
            }

            ClearAndMove();
            _changedInSession = true;
            return true;
        }

        public bool TryReadText([NotNullWhen(true)] out string? text)
        {
            RequireHeld();
            Trace.Add("read-text");
            text = ReadTextFails ? null : Text;
            return text is not null;
        }

        public bool SetText(string text)
        {
            RequireHeld();
            Trace.Add("set-text");
            if (!(SetTextSucceeds?.Invoke(text) ?? true))
            {
                return false;
            }

            Put(CF_UNICODETEXT, Encode(text));
            _changedInSession = true;
            return true;
        }

        public bool SetData(uint format, ReadOnlySpan<byte> data)
        {
            RequireHeld();
            Trace.Add("set-data:" + NameOf(format));
            if (!(SetDataSucceeds?.Invoke(format) ?? true))
            {
                return false;
            }

            Put(format, data.ToArray());
            _changedInSession = true;
            return true;
        }

        public bool TryReadData(uint format, Span<byte> destination)
        {
            RequireHeld();
            Trace.Add("read-data:" + NameOf(format));
            if (Find(format) is not { } data || data.Length < destination.Length)
            {
                return false;
            }

            data.AsSpan(0, destination.Length).CopyTo(destination);
            return true;
        }

        /// <summary>Puts content on the clipboard as if the user had copied it before the dictation.</summary>
        public void SeedText(string text) => AsOtherApp(() =>
        {
            ClearAndMove();
            Put(CF_UNICODETEXT, Encode(text));
        });

        public void SeedEmpty() => AsOtherApp(ClearAndMove);

        public void SeedFormats(params uint[] formats) => AsOtherApp(() =>
        {
            ClearAndMove();
            foreach (var format in formats)
            {
                Put(format, [1, 2, 3, 4]);
            }
        });

        /// <summary>Another application copies: EmptyClipboard, then its own text.</summary>
        public void OtherAppCopies(string text) => SeedText(text);

        /// <summary>
        /// Another application copies text together with one more format, for example a clone of an
        /// item that carries a receipt of its own.
        /// </summary>
        public void OtherAppCopies(string text, uint extraFormat, byte[] extraData) => AsOtherApp(() =>
        {
            ClearAndMove();
            Put(CF_UNICODETEXT, Encode(text));
            Put(extraFormat, extraData);
        });

        /// <summary>A misbehaving application replaces the text without emptying first.</summary>
        public void OtherAppRewritesTextInPlace(string text) => AsOtherApp(() => Put(CF_UNICODETEXT, Encode(text)));

        /// <summary>The paste target reads the text, and the ANSI form Windows synthesizes from it.</summary>
        public string? TargetReads()
        {
            string? text = null;
            AsOtherApp(() =>
            {
                text = Text;
                if (SynthesizedReadMovesSequence)
                {
                    Sequence++;
                }
            }, changes: false);

            return text;
        }

        /// <summary>
        /// A clipboard monitor reads the item the same way, for example on the update notification that
        /// follows Scribe's write. Changes nothing, but can move the number where synthesized reads do.
        /// </summary>
        public void MonitorReads() => TargetReads();

        private void AsOtherApp(Action action, bool changes = true)
        {
            if (IsHeldByScribe)
            {
                throw new InvalidOperationException(
                    "Impossible interleaving: another application cannot touch a clipboard Scribe holds open.");
            }

            action();
            EndSession(changes);
        }

        private void EndSession(bool changed)
        {
            if (!changed)
            {
                return;
            }

            // Windows adds the ANSI, OEM and locale companions of a Unicode text item when it is closed.
            if (Find(CF_UNICODETEXT) is not null)
            {
                foreach (var companion in new[] { CF_LOCALE, CF_TEXT, CF_OEMTEXT })
                {
                    if (Find(companion) is null)
                    {
                        _formats.Add((companion, [0]));
                    }
                }
            }

            if (CloseMovesSequence)
            {
                Sequence++;
            }
        }

        private void ClearAndMove()
        {
            _formats.Clear();
            Sequence++;
        }

        private void Put(uint format, byte[] data)
        {
            int index = _formats.FindIndex(f => f.Format == format);
            if (index >= 0)
            {
                _formats[index] = (format, data);
            }
            else
            {
                _formats.Add((format, data));
            }

            Sequence++;
        }

        private byte[]? Find(uint format)
        {
            foreach (var (candidate, data) in _formats)
            {
                if (candidate == format)
                {
                    return data;
                }
            }

            return null;
        }

        private string NameOf(uint format)
        {
            foreach (var (name, id) in _registered)
            {
                if (id == format)
                {
                    return name;
                }
            }

            return format.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private void RequireHeld()
        {
            if (!IsHeldByScribe)
            {
                throw new InvalidOperationException("Clipboard call made without holding the clipboard open.");
            }
        }

        private static byte[] Encode(string text) => Encoding.Unicode.GetBytes(text + "\0");

        private static string Decode(byte[] data)
        {
            var text = Encoding.Unicode.GetString(data);
            int end = text.IndexOf('\0');
            return end < 0 ? text : text[..end];
        }
    }

    /// <summary>Records every SendInput batch and sleep; foreground and delivery are scripted.</summary>
    internal sealed class Platform : IInjectionPlatform
    {
        public nint Foreground { get; set; } = 0x4242;

        public bool StandardEdit { get; set; }

        public List<INPUT[]> Batches { get; } = [];

        public List<int> Sleeps { get; } = [];

        /// <summary>How many events each batch (by 0-based index) inserts. Default: all of them.</summary>
        public Func<int, INPUT[], uint>? Deliver { get; set; }

        public Action<int>? OnSleep { get; set; }

        public int UnicodeEvents => Batches.Sum(b => b.Count(i => (i.U.ki.dwFlags & KEYEVENTF_UNICODE) != 0));

        public int CtrlVChords => Batches.Count(IsCtrlVChord);

        public nint GetForegroundWindow() => Foreground;

        public uint SendInput(INPUT[] inputs)
        {
            Batches.Add([.. inputs]);
            return Deliver?.Invoke(Batches.Count - 1, inputs) ?? (uint)inputs.Length;
        }

        public bool TryInsertIntoStandardEdit(string text, nint expectedForegroundWindow) => StandardEdit;

        public void Sleep(int milliseconds)
        {
            Sleeps.Add(milliseconds);
            OnSleep?.Invoke(milliseconds);
        }

        private static bool IsCtrlVChord(INPUT[] batch) =>
            batch.Length == 4 &&
            batch[0].U.ki is { wVk: VK_CONTROL, dwFlags: 0 } &&
            batch[1].U.ki is { wVk: VK_V, dwFlags: 0 } &&
            batch[2].U.ki is { wVk: VK_V, dwFlags: KEYEVENTF_KEYUP } &&
            batch[3].U.ki is { wVk: VK_CONTROL, dwFlags: KEYEVENTF_KEYUP };
    }

    /// <summary>Captures every formatted message plus every structured argument and exception.</summary>
    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _entries = [];

        /// <summary>When set, a formatted message it matches is recorded and then thrown on, like a faulting sink.</summary>
        public Func<string, bool>? ThrowOn { get; set; }

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var parts = new List<string> { $"{logLevel}: {message}" };
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                parts.AddRange(pairs.Select(p => $"{p.Key}={p.Value}"));
            }

            if (exception is not null)
            {
                parts.Add(exception.ToString());
            }

            lock (_entries)
            {
                _entries.Add(string.Join(" | ", parts));
            }

            if (ThrowOn?.Invoke(message) == true)
            {
                throw new IOException("Simulated log sink fault.");
            }
        }
    }
}
