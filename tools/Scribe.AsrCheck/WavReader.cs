namespace Scribe.AsrCheck;

/// <summary>
/// Minimal RIFF/WAVE reader for the test fixtures.
///
/// Hand-rolled rather than pulled from NAudio because this tool must exercise the ASR native stack
/// in isolation: if decoding fails, the cause should be unambiguous, not "something in the audio
/// dependency chain". Handles only what the fixture generator emits, and says so loudly otherwise.
/// </summary>
internal static class WavReader
{
    public static float[] ReadMonoFloat(string path, out int sampleRate)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        if (ReadFourCc(reader) != "RIFF") throw new InvalidDataException($"{path} is not a RIFF file.");
        reader.ReadUInt32(); // total size
        if (ReadFourCc(reader) != "WAVE") throw new InvalidDataException($"{path} is not a WAVE file.");

        short channels = 0;
        short bitsPerSample = 0;
        sampleRate = 0;

        // Chunk walk rather than fixed offsets: SAPI emits a LIST/fact chunk before data.
        // 8 bytes is the minimum viable header, so anything less is a truncated file.
        while (stream.Length - stream.Position >= 8)
        {
            var chunkId = ReadFourCc(reader);
            var chunkSize = reader.ReadUInt32();

            // A corrupt size must not turn into a huge allocation or a negative int cast.
            if (chunkSize > stream.Length - stream.Position)
            {
                throw new InvalidDataException($"{path} declares a {chunkId} chunk of {chunkSize} bytes past the end of the file.");
            }

            if (chunkId == "fmt ")
            {
                reader.ReadUInt16(); // audio format
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadUInt32(); // byte rate
                reader.ReadUInt16(); // block align
                bitsPerSample = reader.ReadInt16();
                if (chunkSize > 16) stream.Position += chunkSize - 16;
            }
            else if (chunkId == "data")
            {
                // Reached only after fmt in a well-formed file. A data-before-fmt file leaves these
                // at zero, and the checks below reject it rather than decoding garbage.
                if (bitsPerSample != 16) throw new InvalidDataException($"{path} is {bitsPerSample}-bit; expected 16-bit PCM (is the fmt chunk missing or after data?).");
                if (channels < 1) throw new InvalidDataException($"{path} declares {channels} channels.");

                var bytes = reader.ReadBytes((int)chunkSize);
                var frames = bytes.Length / 2 / channels;
                var samples = new float[frames];

                for (var frame = 0; frame < frames; frame++)
                {
                    // Average channels so a stereo fixture still yields the mono signal the
                    // recogniser expects; a mono file passes through unchanged.
                    var sum = 0;
                    for (var channel = 0; channel < channels; channel++)
                    {
                        sum += BitConverter.ToInt16(bytes, (frame * channels + channel) * 2);
                    }

                    samples[frame] = sum / (float)channels / 32768f;
                }

                return samples;
            }
            else
            {
                stream.Position += chunkSize;
            }

            // RIFF chunks are word aligned; an odd size carries a pad byte that is not counted.
            if (chunkSize % 2 == 1 && stream.Position < stream.Length) stream.Position++;
        }

        throw new InvalidDataException($"{path} contains no data chunk.");
    }

    /// <summary>
    /// Reads a 4-byte RIFF chunk id as ASCII.
    ///
    /// Deliberately not BinaryReader.ReadChars(4): that decodes with the reader's UTF-8 encoding, so
    /// on any non-ASCII byte it consumes a variable number of bytes to produce 4 characters and
    /// silently desyncs the stream position, turning a malformed file into a nonsense chunk walk
    /// instead of a clean error.
    /// </summary>
    private static string ReadFourCc(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length < 4) throw new InvalidDataException("Unexpected end of file reading a chunk id.");
        return System.Text.Encoding.ASCII.GetString(bytes);
    }
}
