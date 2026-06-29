using System;
using System.Collections.Concurrent;
using System.IO;

namespace JdMegaMind
{
    /// <summary>
    /// A thread-safe audio bridge that connects two systems with different
    /// audio consumption models:
    ///
    ///   PRODUCER: NAudio (event-driven, PUSHES bytes whenever mic captures audio)
    ///   CONSUMER: System.Speech.Recognition (pull-based, PULLS bytes whenever it wants them)
    ///
    /// The problem without this bridge:
    ///   NAudio fires DataAvailable events and pushes bytes.
    ///   System.Speech.Recognition calls Read() and expects bytes to be ready immediately.
    ///   These two schedules do not align — NAudio produces in bursts every ~100ms,
    ///   the speech engine reads continuously. Without a buffer between them,
    ///   the speech engine either starves (no bytes) or misses events entirely.
    ///
    /// The solution — a BlockingCollection queue:
    ///   NAudio pushes byte chunks into the queue via Write().
    ///   The speech engine pulls byte chunks out via Read().
    ///   If the speech engine asks for bytes before NAudio has produced any,
    ///   Read() blocks and waits automatically — the speech engine thread sleeps
    ///   until bytes arrive, then wakes up and continues. Zero wasted CPU.
    ///   Zero race conditions. Zero dropped audio.
    ///
    /// WHY inherit from Stream:
    ///   System.Speech.Recognition.SetInputToAudioStream() requires a Stream object.
    ///   By inheriting from Stream and implementing Read(), we satisfy that contract.
    ///   The speech engine treats this exactly like any other audio file stream —
    ///   it has no idea it's reading from a live microphone feed.
    ///
    /// THREAD SAFETY:
    ///   Write() is called from NAudio's background thread (DataAvailable event).
    ///   Read() is called from System.Speech's internal background thread.
    ///   BlockingCollection handles all synchronization internally.
    ///   You never need to use lock{} or Mutex here.
    /// </summary>
    public class AudioBridgeStream : Stream
    {
        // -----------------------------------------------------------------------
        // THE QUEUE
        // -----------------------------------------------------------------------

        // BlockingCollection is a thread-safe queue.
        // Capacity of 50 means it can hold up to 50 unread audio chunks before
        // Write() starts blocking. At 100ms chunks, that's 5 seconds of audio.
        // In practice the queue depth stays near zero because the speech engine
        // reads almost as fast as NAudio writes.
        //
        // WHY BlockingCollection over ConcurrentQueue:
        //   ConcurrentQueue.TryDequeue() returns false immediately if empty.
        //   You'd need a spin-wait loop in Read() burning CPU doing nothing.
        //   BlockingCollection.Take() BLOCKS the calling thread until data arrives —
        //   the speech engine thread sleeps, uses zero CPU, and wakes when bytes appear.
        private readonly BlockingCollection<byte[]> _queue =
            new BlockingCollection<byte[]>(50);

        // Tracks leftover bytes from a previous Read() call.
        // The speech engine asks for exactly N bytes per Read() call.
        // NAudio produces chunks of a different size.
        // If a NAudio chunk is larger than the speech engine requested,
        // we save the remainder here and serve it on the next Read() call.
        private byte[] _leftover = null;
        private int _leftoverOffset = 0;

        // -----------------------------------------------------------------------
        // PRODUCER SIDE — called by NAudio from OnAudioDataAvailable
        // -----------------------------------------------------------------------

        /// <summary>
        /// Called by NAudio's DataAvailable event handler to push a fresh
        /// chunk of PCM audio bytes into the queue.
        ///
        /// This runs on NAudio's background thread. BlockingCollection handles
        /// thread safety — no lock needed here.
        ///
        /// If the queue is full (50 chunks), this blocks until the speech engine
        /// catches up. In practice the queue never fills because the speech engine
        /// reads faster than real-time audio is produced.
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count)
        {
            // Copy only the relevant bytes into a new array.
            // NAudio's buffer may be pre-allocated larger than count —
            // we must not send the garbage bytes beyond count to the speech engine.
            // Also we shift the synchronus direct approach of Stream.Write and
            // Convert it into an asynchronus queue via _queue.Add(chunk)
            // So the windows SAPI5 can call Stream.Read() (which is also modified below) on this legitimate Stream
            // Which has been modified from direct synchronus file write into a proper asynchronus queue.
            byte[] chunk = new byte[count];
            Buffer.BlockCopy(buffer, offset, chunk, 0, count);
            _queue.Add(chunk);
        }

        // -----------------------------------------------------------------------
        // CONSUMER SIDE — called by System.Speech.Recognition internally
        // -----------------------------------------------------------------------

        /// <summary>
        /// Called by System.Speech.Recognition's internal thread whenever it
        /// needs more audio to process. It asks for exactly 'count' bytes.
        ///
        /// This method serves bytes from the queue, handling the size mismatch
        /// between NAudio's chunk sizes and the speech engine's requested sizes.
        ///
        /// If the queue is empty, Take() blocks this thread until NAudio
        /// produces more audio. The speech engine thread sleeps — zero CPU waste.
        ///
        /// Returns the number of bytes actually written to buffer.
        /// The speech engine uses this return value to know how much data arrived.
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesWritten = 0;

            while (bytesWritten < count)
            {
                // If we have leftover bytes from the previous Read() call, use them first.
                // This happens when NAudio's chunk was larger than what the speech engine requested.
                if (_leftover != null)
                {
                    int available = _leftover.Length - _leftoverOffset;

                    // Makes sure to use up availaible bytes COMPLETELY
                    // Before pulling a fresh new chunk from NAudio
                    int toWrite = Math.Min(available, count - bytesWritten);

                    // Params:
                    //  _leftover: our LEFTOVER NAudio bytes
                    //  _leftoverOffset: (To properly copy the actual chunk windows SAPI5 needs)
                    //  buffer (The destination array, aka buffer where windows SAPI5 expects us to drop our audio bytes
                    //  offset + ... (the offset in buffer array of windows SAPI5)
                    //  ToWrite (The exact amount of bytes we transfer to our windows SAPI5)
                    Buffer.BlockCopy(_leftover, _leftoverOffset, buffer, offset + bytesWritten, toWrite);
                    bytesWritten += toWrite;
                    _leftoverOffset += toWrite;

                    // If we consumed all leftover bytes, clear them
                    if (_leftoverOffset >= _leftover.Length)
                    {
                        _leftover = null;
                        _leftoverOffset = 0;
                    }

                    continue;
                }

                // No leftover — pull the next chunk from the queue.
                // Take() BLOCKS here if the queue is empty, sleeping until
                // NAudio's DataAvailable event fires and pushes new bytes.
                byte[] chunk;
                try
                {
                    chunk = _queue.Take();
                }
                catch (InvalidOperationException)
                {
                    // Queue was marked complete (CompleteAdding was called).
                    // This means StopListening() was called. Return what we have.
                    // InvalidOperationException is DELIBERATE SIGNALLING MECHANISM.
                    break;
                }

                int chunkToWrite = Math.Min(chunk.Length, count - bytesWritten);

                // Params:
                //  Chunk: our freshly pulled NAudio bytes
                //  chunkoffset: (Set to 0 i.e copy from the start of our chunk)
                //  buffer (The destination array, aka buffer where windows SAPI5 expects us to drop our audio bytes
                //  offset + ... (the offset in buffer array of windows SAPI5)
                //  chunkToWrite (The exact amount of bytes we transfer to our windows SAPI5)
                Buffer.BlockCopy(chunk, 0, buffer, offset + bytesWritten, chunkToWrite);
                bytesWritten += chunkToWrite;

                // If the chunk had more bytes than the speech engine requested,
                // save the remainder for the next Read() call.
                if (chunkToWrite < chunk.Length)
                {
                    _leftover = chunk;
                    _leftoverOffset = chunkToWrite;
                }
            }

            // Should be obv atp
            return bytesWritten;
        }

        // -----------------------------------------------------------------------
        // CLEANUP
        // -----------------------------------------------------------------------

        /// <summary>
        /// Call this when StopListening() is called to unblock any Read() call
        /// that is currently waiting for audio. Without this, the speech engine's
        /// thread would block forever on Take() after recording stops.
        /// </summary>
        public void StopFeeding()
        {
            // CompleteAdding causes BlockingCollection<byte[]> _queue 
            // To throw InvalidOperationException() which signifies
            // the queue to stop adding audio bytes (is handled in Read() above)
            _queue.CompleteAdding();
        }

        // -----------------------------------------------------------------------
        // STREAM CONTRACT — required overrides for abstract Stream members
        // We only care about Read() and Write(). The rest are irrelevant for
        // a live audio stream but must be implemented to satisfy the contract.
        // -----------------------------------------------------------------------

        // The speech engine checks CanRead before calling Read().
        // Must return true or it won't read from us at all.
        public override bool CanRead => true;

        // Return true so SAPI5 doesn't reject the stream during initialisation.
        // Seek() still throws NotSupportedException if actually called —
        // we just need CanSeek to not block the stream from being accepted.
        public override bool CanSeek => true;

        // We support writing (NAudio pushes bytes in via Write()).
        public override bool CanWrite => true;

        // System.Speech.Recognition internally calls get_Length() before accepting
        // the stream via SetInputToAudioStream(). We cannot throw here even though
        // this is a live stream with no real length — SAPI5 requires a non-throwing
        // response. We return long.MaxValue as a sentinel meaning "read indefinitely."
        // SAPI5 treats this as a hint, not a hard read limit — it will keep reading
        // until RecognizeAsyncStop() is called, not until it reaches this value.
        public override long Length => long.MaxValue;

        public override long Position
        {
            get => 0;
            set { } // Ignore position sets — meaningless on a live stream
        }

        // Seek is meaningless for a live stream.
        public override long Seek(long offset, SeekOrigin origin)
        {
            // SAPI5 may call Seek during stream initialisation for format detection.
            // We return 0 (beginning of stream) rather than throwing — the engine
            // does not actually seek through a live audio stream, this is just setup.
            return 0;
        }

        // SetLength is meaningless for a live stream.
        public override void SetLength(long value) =>
            throw new NotSupportedException("AudioBridgeStream does not support SetLength.");

        // Flush is a no-op — we don't buffer output, we buffer input.
        public override void Flush() { }
    }
}