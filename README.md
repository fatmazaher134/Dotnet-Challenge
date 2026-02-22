# Dotnet-Challenge
This is a high-performance log processor! I've used some advanced .NET features like System.IO.Pipelines and ArrayPool.

## Design Rationale
#### Approach: High-Throughput Parallel Execution
To process a large log file efficiently under memory constraint, I use a Producer-Consumer pattern.
- Producer: Reads the file asynchronously using System.IO.Pipelines. This enables high-performance streaming with minimal copying and zero-allocation slices.
- Consumer: Employs Channel<T> to send blocks of data to multiple worker threads. Processing is spread across all CPU cores using Parallel.ForEachAsync.

## Selection: Data Structures and Threading
- ThreadLocal<Dictionary<uint, long>>: To avoid the lock contention overhead from using a single ConcurrentDictionary, I simply gave each thread its own local dictionary. Workers can run at shape speed counting the IPs without ever having to waiting on a lock.
- uint for IP Addresses: Storing IPs as strings is expensive in memory. I wroted a small parser(TryParseIpToUint) which converts an IPv4 address to a 32bit integer (uint). This compresses the dictionary's memory usage by around 80% when using string keys.
- ArrayPool<byte>: To reduce GC pressure, I rent the buffers from a shared pool and return then after processing.
- CollectionsMarshal: Access dictionary values by reference (GetValueRefOrAddDefault), To eliminate the double lookup overhead when you are incrementing numbers.

## Trade-offs
- Upsides: Very high throughput, very low GC overhead. When strin­gs and locks are avoided, the bot­tle­neck moves from soft­ware syn­chro­niza­tion to hard­ware I/O speed.
- downsides : The code is more complex than a traditional File.ReadLines() style. Also, ThreadLocal approach need an explicit "aggregation" phase at the end to consolidate results from all threads.

## Alternatives Considered
- StreamReader.ReadLineAsync: I ruled this out because it generates a new string object for each line in the log. For a file with millions of lines, this would result in large GC pauses and slow down the app.
- ConcurrentDictionary: Although it is easier to implement, the internal locking becomes a bottleneck as the number of CPU cores grows. The “Local-then-Merge” approach linearly scales much better.
- Memory-Mapped Files: I thought about this for I/O speedier still, but then went with Pipelines because it’s more memory safe and I had the feeling it could deal better with very large files (larger than RAM) across OSes.
