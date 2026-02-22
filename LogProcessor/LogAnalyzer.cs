using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Asn1;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace LogProcessor
{
    public class LogAnalyzer
    {
        private readonly int _maxDegreeOfParallelism = Environment.ProcessorCount;

        private readonly ThreadLocal<Dictionary<uint, long>> _localDicts =
            new(() => new Dictionary<uint, long>(50000), trackAllValues: true);

        private static readonly byte[] IpTag = "ip="u8.ToArray();

        public async Task ProcessFileAsync(string filePath)
        {
            Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            var channel = Channel.CreateBounded<(byte[] data, int length)>(new BoundedChannelOptions(_maxDegreeOfParallelism)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });

            Task? readerTask = Task.Run(async () =>
            {
                using FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 512,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                PipeReader reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 2 * 1024 * 1024));

                try
                {
                    while (true)
                    {
                        ReadResult result = await reader.ReadAsync();
                        ReadOnlySequence<byte> buffer = result.Buffer;

                        while (buffer.Length > 2 * 1024 * 1024 && TryGetFullLines(ref buffer, out var completedSequence))
                        {
                            SendToChannel(completedSequence, channel.Writer);
                        }

                        if (result.IsCompleted)
                        {
                            if (!buffer.IsEmpty) SendToChannel(buffer, channel.Writer);
                            break;
                        }
                        reader.AdvanceTo(buffer.Start, buffer.End);
                    }
                }
                finally
                {
                    channel.Writer.Complete();
                    await reader.CompleteAsync();
                }
            });

            Task? processingTask = Parallel.ForEachAsync(channel.Reader.ReadAllAsync(),
                new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism },
                async (chunk, ct) =>
                {
                    Dictionary<uint, long> dict = _localDicts.Value!;
                    ProcessChunkOptimized(chunk.data, chunk.length, dict);
                    ArrayPool<byte>.Shared.Return(chunk.data);
                });

            await Task.WhenAll(readerTask, processingTask);

            Dictionary<uint, long> finalResults = AggregateFinalResults();

            watch.Stop();
            PrintTopIPs(finalResults, 5);
            Console.WriteLine($"\nProcessing completed in: {watch.Elapsed.TotalSeconds:F2} seconds");
        }

        private void SendToChannel(ReadOnlySequence<byte> sequence, ChannelWriter<(byte[] data, int length)> writer)
        {
            int length = (int)sequence.Length;
            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            sequence.CopyTo(rented);
            if (!writer.TryWrite((rented, length)))
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void ProcessChunkOptimized(byte[] chunk, int length, Dictionary<uint, long> localDict)
        {
            var span = chunk.AsSpan(0, length);
            int pos = 0;

            while (pos < length)
            {
                int ipStart = span.Slice(pos).IndexOf(IpTag);
                if (ipStart == -1) break;

                pos += ipStart + IpTag.Length;
                var remaining = span.Slice(pos);
                int ipEnd = remaining.IndexOf((byte)';');
                if (ipEnd == -1) break;

                if (TryParseIpToUint(remaining.Slice(0, ipEnd), out uint ip))
                {
                    ref long count = ref CollectionsMarshal.GetValueRefOrAddDefault(localDict, ip, out _);
                    count++;
                }
                pos += ipEnd + 1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryParseIpToUint(ReadOnlySpan<byte> ipSpan, out uint result)
        {
            result = 0;
            uint currentSegment = 0;
            foreach (byte b in ipSpan)
            {
                if (b == (byte)'.')
                {
                    result = (result << 8) | currentSegment;
                    currentSegment = 0;
                }
                else
                {
                    currentSegment = currentSegment * 10 + (uint)(b - '0');
                }
            }
            result = (result << 8) | currentSegment;
            return true;
        }

        private bool TryGetFullLines(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> completedLines)
        {
            var lastNewLine = buffer.PositionOf((byte)'\n');
            if (lastNewLine == null)
            {
                completedLines = default;
                return false;
            }
            var nextPos = buffer.GetPosition(1, lastNewLine.Value);
            completedLines = buffer.Slice(0, nextPos);
            buffer = buffer.Slice(nextPos);
            return true;
        }

        private Dictionary<uint, long> AggregateFinalResults()
        {
            Dictionary<uint, long> master = new Dictionary<uint, long>(100000);
            foreach (var dict in _localDicts.Values)
            {
                foreach (var kvp in dict)
                {
                    ref long count = ref CollectionsMarshal.GetValueRefOrAddDefault(master, kvp.Key, out _);
                    count += kvp.Value;
                }
            }
            return master;
        }

        private void PrintTopIPs(Dictionary<uint, long> results, int count)
        {
            var top = results.OrderByDescending(x => x.Value).Take(count);
            Console.WriteLine($"\nTop {count} IP Addresses:");
            foreach (var item in top)
            {
                Span<byte> bytes = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, item.Key);
                Console.WriteLine($"{new IPAddress(bytes),-15} | {item.Value:N0}");
            }
        }
    }
}