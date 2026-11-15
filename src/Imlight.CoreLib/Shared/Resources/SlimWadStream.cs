/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Imlight.CoreLib.Shared.Resources;

/// <summary>
/// A game archive (.wad) as the server keeps it in memory: its list of files and the data files the server reads
/// (zone layouts, triggers, templates, collision, text), without the 3D models, animations, pictures and sounds only
/// the game draws. Those are 98% of an area's archive and 40% of Root.wad, and the server read every archive whole
/// into memory (Niduus, 2026-11-15: the server at 9.4 GB). A part that was left out (the server never asks for one
/// today) is still read when asked, from the cache file, so every file reads exactly as before.
/// </summary>
internal sealed class SlimWadStream : Stream {

    // What the game draws or plays and the server never opens itself.
    private static readonly HashSet<string> s_leftOut = new(StringComparer.OrdinalIgnoreCase) {
        ".nif", ".kfm", ".kf", ".dds", ".tga", ".png", ".jpg", ".jpeg", ".bmp", ".mp3", ".ogg", ".wav", ".bik",
    };

    // Two kept files closer than this are read as one piece.
    private const long JOIN_GAP = 4096;

    private readonly long[] _starts;
    private readonly byte[][] _pieces;
    private readonly long _length;
    private readonly Func<long, int, byte[]> _readLeftOut;
    private long _position;

    private SlimWadStream(long length, List<(long Start, byte[] Data)> pieces, Func<long, int, byte[]> readLeftOut) {
        _length = length;
        _starts = [.. pieces.Select(p => p.Start)];
        _pieces = [.. pieces.Select(p => p.Data)];
        _readLeftOut = readLeftOut;
    }

    /// <summary>Bytes kept in memory.</summary>
    internal long KeptBytes => _pieces.Sum(p => (long) p.Length);

    /// <summary>Whether the server keeps this file of an archive in memory.</summary>
    internal static bool Keeps(string fileName) => !s_leftOut.Contains(Path.GetExtension(fileName ?? ""));

    /// <summary>
    /// Reads an archive from the start of <paramref name="source"/> (read once, front to back, no seeking), keeping its
    /// list of files and the files <see cref="Keeps"/> says. <paramref name="readLeftOut"/> reads a left-out part
    /// (offset, count) when one is asked for.
    /// </summary>
    internal static SlimWadStream Read(Stream source, long length, Func<long, int, byte[]> readLeftOut) {
        // The header (the list of files), recorded as it is read.
        var recorder = new RecordingStream(source);
        var header = Imcodec.Wad.ArchiveParser.Parse(recorder)
            ?? throw new InvalidDataException("The archive has no header.");
        var headerBytes = recorder.Recorded();
        long at = headerBytes.Length;

        var ranges = new List<(long Start, long End)>();
        foreach (var (name, entry) in header.Files) {
            if (!Keeps(name)) {
                continue;
            }

            var file = entry.Value;
            var size = file.IsCompressed ? file.CompressedSize : file.UncompressedSize;
            if (size > 0) {
                ranges.Add((file.Offset, (long) file.Offset + size));
            }
        }

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var joined = new List<(long Start, long End)>();
        foreach (var range in ranges) {
            var start = Math.Max(range.Start, at);
            if (range.End <= start) {
                continue;
            }

            if (joined.Count > 0 && start <= joined[^1].End + JOIN_GAP) {
                joined[^1] = (joined[^1].Start, Math.Max(joined[^1].End, range.End));
            }
            else {
                joined.Add((start, range.End));
            }
        }

        var pieces = new List<(long Start, byte[] Data)> { (0, headerBytes) };
        var skip = new byte[81920];
        foreach (var (start, end) in joined) {
            while (at < start) {
                var read = source.Read(skip, 0, (int) Math.Min(skip.Length, start - at));
                if (read <= 0) {
                    throw new EndOfStreamException("The archive ended before its files.");
                }

                at += read;
            }

            var data = new byte[end - start];
            source.ReadExactly(data);
            at = end;
            pieces.Add((start, data));
        }

        return new SlimWadStream(length, pieces, readLeftOut);
    }

    public override int Read(byte[] buffer, int offset, int count) {
        count = (int) Math.Min(count, Math.Max(0, _length - _position));
        var done = 0;
        while (done < count) {
            var at = _position + done;
            var index = Array.BinarySearch(_starts, at);
            if (index < 0) {
                index = ~index - 1;
            }

            if (index >= 0 && at < _starts[index] + _pieces[index].Length) {
                var piece = _pieces[index];
                var from = (int) (at - _starts[index]);
                var take = Math.Min(count - done, piece.Length - from);
                Buffer.BlockCopy(piece, from, buffer, offset + done, take);
                done += take;
                continue;
            }

            // A left-out part: up to the next kept piece, read from the cache file.
            var next = index + 1 < _starts.Length ? _starts[index + 1] : _length;
            var want = (int) Math.Min(count - done, next - at);
            var bytes = _readLeftOut(at, want);
            if (bytes is null || bytes.Length != want) {
                throw new IOException($"Could not read {want} bytes at {at} of the archive from the cache file.");
            }

            Buffer.BlockCopy(bytes, 0, buffer, offset + done, want);
            done += want;
        }

        _position += done;

        return done;
    }

    public override long Seek(long offset, SeekOrigin origin) {
        _position = origin switch {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };

        return _position;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position {
        get => _position;
        set => _position = value;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Passes reads through and keeps a copy of every byte read.</summary>
    private sealed class RecordingStream(Stream inner) : Stream {

        private readonly MemoryStream _copy = new();

        internal byte[] Recorded() => _copy.ToArray();

        public override int Read(byte[] buffer, int offset, int count) {
            var read = inner.Read(buffer, offset, count);
            _copy.Write(buffer, offset, read);

            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position {
            get => _copy.Length;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    }

}
