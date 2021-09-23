
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// The PooledQueueCache is a cache that is intended to serve as a message cache in an IQueueCache.
    /// It is capable of storing large numbers of messages (gigs worth of messages) for extended periods
    ///   of time (minutes to indefinite), while incurring a minimal performance hit due to garbage collection.
    /// This pooled cache allocates memory and never releases it. It keeps freed resources available in pools 
    ///   that remain in application use through the life of the service. This means these objects go to gen2,
    ///   are compacted, and then stay there. This is relatively cheap, as the only cost they now incur is
    ///   the cost of checking to see if they should be freed in each collection cycle. Since this cache uses
    ///   small numbers of large objects with relatively simple object graphs, they are less costly to check
    ///   then large numbers of smaller objects with more complex object graphs.
    /// For performance reasons this cache is designed to more closely align with queue specific data.  This is,
    ///   in part, why, unlike the SimpleQueueCache, this cache does not implement IQueueCache.  It is intended
    ///   to be used in queue specific implementations of IQueueCache.
    /// </summary>
    public class PooledQueueCache: IPurgeObservable
    {
        // linked list of message bocks.  First is newest.
        private readonly LinkedList<CachedMessageBlock> messageBlocks;
        private readonly CachedMessagePool pool;
        private readonly ICacheDataAdapter cacheDataAdapter;
        private readonly ILogger logger;
        private readonly ICacheMonitor cacheMonitor;
        private readonly TimeSpan purgeMetadataInterval;
        private readonly PeriodicAction periodicMonitoring;
        private readonly PeriodicAction periodicMetadaPurging;

        private readonly Dictionary<IStreamIdentity, (DateTime TimeStamp, StreamSequenceToken Token)> lastPurgedToken = new Dictionary<IStreamIdentity, (DateTime TimeStamp, StreamSequenceToken Token)>();

        /// <summary>
        /// Cached message most recently added
        /// </summary>
        public CachedMessage? Newest
        {
            get
            {
                if (IsEmpty)
                    return null;
                return messageBlocks.First.Value.NewestMessage;
            }
        }

        /// <summary>
        /// Oldest message in cache
        /// </summary>
        public CachedMessage? Oldest
        {
            get
            {
                if (IsEmpty)
                    return null;
                return messageBlocks.Last.Value.OldestMessage;
            }
        }

        /// <summary>
        /// Cached message count
        /// </summary>
        public int ItemCount { get; private set; }

        /// <summary>
        /// Pooled queue cache is a cache of message that obtains resource from a pool
        /// </summary>
        /// <param name="cacheDataAdapter"></param>
        /// <param name="logger"></param>
        /// <param name="cacheMonitor"></param>
        /// <param name="cacheMonitorWriteInterval">cache monitor write interval.  Only triggered for active caches.</param>
        /// <param name="purgeMetadataInterval"></param>
        public PooledQueueCache(
            ICacheDataAdapter cacheDataAdapter,
            ILogger logger,
            ICacheMonitor cacheMonitor,
            TimeSpan? cacheMonitorWriteInterval,
            TimeSpan? purgeMetadataInterval = null)
        {
            this.cacheDataAdapter = cacheDataAdapter ?? throw new ArgumentNullException("cacheDataAdapter");
            this.logger = logger ?? throw new ArgumentNullException("logger");
            this.ItemCount = 0;
            pool = new CachedMessagePool(cacheDataAdapter);
            messageBlocks = new LinkedList<CachedMessageBlock>();
            this.cacheMonitor = cacheMonitor;
            if (this.cacheMonitor != null && cacheMonitorWriteInterval.HasValue)
            {
                this.periodicMonitoring = new PeriodicAction(cacheMonitorWriteInterval.Value, this.ReportCacheMessageStatistics);
            }

            if (purgeMetadataInterval.HasValue)
            {
                this.purgeMetadataInterval = purgeMetadataInterval.Value;
                this.periodicMetadaPurging = new PeriodicAction(purgeMetadataInterval.Value.Divide(5), this.PurgeMetadata);
            }
        }

        /// <summary>
        /// Indicates whether the cache is empty
        /// </summary>
        public bool IsEmpty => messageBlocks.Count == 0 || (messageBlocks.Count == 1 && messageBlocks.First.Value.IsEmpty);

        /// <summary>
        /// Acquires a cursor to enumerate through the messages in the cache at the provided sequenceToken, 
        ///   filtered on the specified stream.
        /// </summary>
        /// <param name="streamIdentity">stream identity</param>
        /// <param name="sequenceToken"></param>
        /// <returns></returns>
        public object GetCursor(IStreamIdentity streamIdentity, StreamSequenceToken sequenceToken)
        {
            var cursor = new Cursor(streamIdentity);
            SetCursor(cursor, sequenceToken);
            return cursor;
        }

        private void ReportCacheMessageStatistics()
        {
            if (this.IsEmpty)
            {
                this.cacheMonitor.ReportMessageStatistics(null, null, null, this.ItemCount);
            }
            else
            {
                var newestMessage = this.Newest.Value;
                var oldestMessage = this.Oldest.Value;
                var now = DateTime.UtcNow;
                var newestMessageEnqueueTime = newestMessage.EnqueueTimeUtc;
                var oldestMessageEnqueueTime = oldestMessage.EnqueueTimeUtc;
                var oldestMessageDequeueTime = oldestMessage.DequeueTimeUtc;
                this.cacheMonitor.ReportMessageStatistics(oldestMessageEnqueueTime, oldestMessageDequeueTime, newestMessageEnqueueTime, this.ItemCount);
            }
        }

        private void PurgeMetadata()
        {
            var now = DateTime.UtcNow;
            var keys = new List<IStreamIdentity>();

            // Get all keys older than this.purgeMetadataInterval
            foreach (var kvp in this.lastPurgedToken)
            {
                if (kvp.Value.TimeStamp + this.purgeMetadataInterval < now)
                {
                    keys.Add(kvp.Key);
                }
            }

            // Remove the expired entries
            foreach (var key in keys)
            {
                this.lastPurgedToken.Remove(key);
            }
        }

        private void TrackAndPurgeMetadata(CachedMessage messageToRemove)
        {
            // If tracking of evicted message metadata is disabled, do nothing
            if (this.periodicMetadaPurging == null)
                return;

            var now = DateTime.UtcNow;
            var streamId = new StreamIdentity(messageToRemove.StreamGuid, messageToRemove.StreamNamespace);
            var token = this.cacheDataAdapter.GetSequenceToken(ref messageToRemove);
            this.lastPurgedToken[streamId] = (now, token);

            this.periodicMetadaPurging.TryAction(now);
        }

        private void SetCursor(Cursor cursor, StreamSequenceToken sequenceToken)
        {
            // If nothing in cache, unset token, and wait for more data.
            if (messageBlocks.Count == 0)
            {
                cursor.State = CursorStates.Unset;
                cursor.SequenceToken = sequenceToken;
                return;
            }

            LinkedListNode<CachedMessageBlock> newestBlock = messageBlocks.First;

            // if sequenceToken is null, iterate from newest message in cache
            if (sequenceToken == null)
            {
                cursor.State = CursorStates.Idle;
                cursor.CurrentBlock = newestBlock;
                cursor.Index = newestBlock.Value.NewestMessageIndex;
                cursor.SequenceToken = newestBlock.Value.GetNewestSequenceToken(cacheDataAdapter);
                return;
            }

            // If sequenceToken is too new to be in cache, unset token, and wait for more data.
            CachedMessage newestMessage = newestBlock.Value.NewestMessage;
            if (newestMessage.Compare(sequenceToken) < 0) 
            {
                cursor.State = CursorStates.Unset;
                cursor.SequenceToken = sequenceToken;
                return;
            }

            // Check to see if sequenceToken is too old to be in cache
            var oldestBlock = messageBlocks.Last;
            var oldestMessage = oldestBlock.Value.OldestMessage;
            if (oldestMessage.Compare(sequenceToken) > 0)
            {
                // Check if the sequenceToken correspond to the last message purged from cache
                var streamIdentity = new StreamIdentity(cursor.StreamIdentity.Guid, cursor.StreamIdentity.Namespace);
                if (this.lastPurgedToken.TryGetValue(streamIdentity, out var entry) && sequenceToken.Equals(entry.Token))
                {
                    // If it maches, then we didn't lose anything. Start from the oldest message in cache
                    cursor.State = CursorStates.Set;
                    cursor.CurrentBlock = oldestBlock;
                    cursor.Index = oldestBlock.Value.OldestMessageIndex;
                    cursor.SequenceToken = oldestBlock.Value.GetOldestSequenceToken(cacheDataAdapter);
                    return;
                }
                else
                {
                    throw new QueueCacheMissException(sequenceToken,
                        messageBlocks.Last.Value.GetOldestSequenceToken(cacheDataAdapter),
                        messageBlocks.First.Value.GetNewestSequenceToken(cacheDataAdapter));
                }
            }

            // Find block containing sequence number, starting from the newest and working back to oldest
            LinkedListNode<CachedMessageBlock> node = messageBlocks.First;
            while (true)
            {
                CachedMessage oldestMessageInBlock = node.Value.OldestMessage;
                if (oldestMessageInBlock.Compare(sequenceToken) <= 0)
                {
                    break;
                }
                node = node.Next;
            }

            // return cursor from start.
            cursor.CurrentBlock = node;
            cursor.Index = node.Value.GetIndexOfFirstMessageLessThanOrEqualTo(sequenceToken);
            // if cursor has been idle, move to next message after message specified by sequenceToken  
            if(cursor.State == CursorStates.Idle)
            {
                // if there are more messages in this block, move to next message
                if (!cursor.IsNewestInBlock)
                {
                    cursor.Index++;
                }
                // if this is the newest message in this block, move to oldest message in newer block
                else if (node.Previous != null)
                {
                    cursor.CurrentBlock = node.Previous;
                    cursor.Index = cursor.CurrentBlock.Value.OldestMessageIndex;
                }
                else
                {
                    cursor.State = CursorStates.Idle;
                    return;
                }
            }
            cursor.SequenceToken = cursor.CurrentBlock.Value.GetSequenceToken(cursor.Index, cacheDataAdapter);
            cursor.State = CursorStates.Set;
        }

        /// <summary>
        /// Acquires the next message in the cache at the provided cursor
        /// </summary>
        /// <param name="cursorObj"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        public bool TryGetNextMessage(object cursorObj, out IBatchContainer message)
        {
            message = null;

            if (cursorObj == null)
            {
                throw new ArgumentNullException("cursorObj");
            }

            var cursor = cursorObj as Cursor;
            if (cursor == null)
            {
                throw new ArgumentOutOfRangeException("cursorObj", "Cursor is bad");
            }

            if (cursor.State != CursorStates.Set)
            {
                SetCursor(cursor, cursor.SequenceToken);
                if (cursor.State != CursorStates.Set)
                {
                    return false;
                }
            }

            // has this message been purged
            CachedMessage oldestMessage = messageBlocks.Last.Value.OldestMessage;
            if (oldestMessage.Compare(cursor.SequenceToken) > 0)
            {
                throw new QueueCacheMissException(cursor.SequenceToken,
                    messageBlocks.Last.Value.GetOldestSequenceToken(cacheDataAdapter),
                    messageBlocks.First.Value.GetNewestSequenceToken(cacheDataAdapter));
            }

            // Iterate forward (in time) in the cache until we find a message on the stream or run out of cached messages.
            // Note that we get the message from the current cursor location, then move it forward.  This means that if we return true, the cursor
            //   will point to the next message after the one we're returning.
            while (cursor.State == CursorStates.Set)
            {
                CachedMessage currentMessage = cursor.Message;

                // Have we caught up to the newest event, if so set cursor to idle.
                if (cursor.CurrentBlock == messageBlocks.First && cursor.IsNewestInBlock)
                {
                    cursor.State = CursorStates.Idle;
                    cursor.SequenceToken = messageBlocks.First.Value.GetNewestSequenceToken(cacheDataAdapter);
                }
                else // move to next
                {
                    int index;
                    if (cursor.IsNewestInBlock)
                    {
                        cursor.CurrentBlock = cursor.CurrentBlock.Previous;
                        cursor.CurrentBlock.Value.TryFindFirstMessage(cursor.StreamIdentity, this.cacheDataAdapter, out index);
                    }
                    else
                    {
                        cursor.CurrentBlock.Value.TryFindNextMessage(cursor.Index + 1, cursor.StreamIdentity, this.cacheDataAdapter, out index);
                    }
                    cursor.Index = index;
                }

                // check if this message is in the cursor's stream
                if (currentMessage.CompareStreamId(cursor.StreamIdentity))
                {
                    message = cacheDataAdapter.GetBatchContainer(ref currentMessage);
                    cursor.SequenceToken = cursor.CurrentBlock.Value.GetSequenceToken(cursor.Index, cacheDataAdapter);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Add a list of queue message to the cache 
        /// </summary>
        /// <param name="messages"></param>
        /// <param name="dequeueTime"></param>
        /// <returns></returns>
        public void Add(List<CachedMessage> messages, DateTime dequeueTime)
        {
            foreach (var message in messages)
            {
                this.Add(message);
            }
            this.cacheMonitor?.TrackMessagesAdded(messages.Count);
            periodicMonitoring?.TryAction(dequeueTime);
        }

        private void Add(CachedMessage message)
        {
            // allocate message from pool
            CachedMessageBlock block = pool.AllocateMessage(message);

            // If new block, add message block to linked list
            if (block != messageBlocks.FirstOrDefault())
                messageBlocks.AddFirst(block.Node);
            ItemCount++;
        }

        /// <summary>
        /// Remove oldest message in the cache, remove oldest block too if the block is empty
        /// </summary>
        public void RemoveOldestMessage()
        {
            TrackAndPurgeMetadata(this.messageBlocks.Last.Value.OldestMessage);

            this.messageBlocks.Last.Value.Remove();
            this.ItemCount--;
            CachedMessageBlock lastCachedMessageBlock = this.messageBlocks.Last.Value;
            // if block is currently empty, but all capacity has been exausted, remove
            if (lastCachedMessageBlock.IsEmpty && !lastCachedMessageBlock.HasCapacity)
            {
                lastCachedMessageBlock.Dispose();
                this.messageBlocks.RemoveLast();
            }
        }

        private enum CursorStates
        {
            Unset, // Not yet set, or points to some data in the future.
            Set, // Points to a message in the cache
            Idle, // Has iterated over all relevant events in the cache and is waiting for more data on the stream.
        }

        private class Cursor
        {
            public readonly IStreamIdentity StreamIdentity;

            public Cursor(IStreamIdentity streamIdentity)
            {
                StreamIdentity = streamIdentity;
                State = CursorStates.Unset;
            }

            public CursorStates State;

            // current sequence token
            public StreamSequenceToken SequenceToken;

            // reference into cache
            public LinkedListNode<CachedMessageBlock> CurrentBlock;
            public int Index;

            // utilities
            public bool IsNewestInBlock => Index == CurrentBlock.Value.NewestMessageIndex;
            public CachedMessage Message => CurrentBlock.Value[Index];
        }
    }
}
