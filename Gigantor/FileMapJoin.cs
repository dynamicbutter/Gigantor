using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.IO;

namespace Imagibee {
    namespace Gigantor {
        //
        // Base class for chunk processing very large files
        //
        // Creates a background manager thread that dispatches
        // additional background worker threads to partition the
        // file into chunks.  These chunks are mapped to a T result
        // by the implementation of the Map method.  Results are
        // then joined by the implementation of the Join method
        // according to the JoinMode.
        //
        // Begin the process by calling Start with the path of the
        // file to process.  All public methods and properties are
        // well behaved at any time.
        //
        // Exceptions during the background processing are caught
        // and stored in Error.  If Error is not empty or Cancelled
        // is true results are undefined.  Exceptions during Start
        // are not handled.
        //
        public abstract class FileMapJoin<T> : MapJoin<FileMapJoinData, T>, IBackground where T : struct, IMapJoinData
        {
            // Path of the last successfully started indexing operation
            public string Path { get; private set; } = "";

            public bool Running { get; private set; }

            public bool Cancelled { get; private set; }

            public string Error { get; private set; } = "";

            // Create a new instance
            //
            // filePath - the path to the file to process
            // progress - signaled each time a thread completes
            // joinMode - defines the map/join mode
            // chunkKiBytes - the chunk size in KiBytes that each worker works on
            // maxWorkers - optional limit to the maximum number of simultaneous workers
            public FileMapJoin(
                string filePath,
                AutoResetEvent progress,
                JoinMode joinMode,
                int chunkKiBytes,
                int maxWorkers=0,
                int overlap=0)
            {
                if (chunkKiBytes < 1) {
                    chunkKiBytes = 1;
                }
                Path = filePath;
                this.progress = progress;
                this.joinMode = joinMode;
                chunkSize = chunkKiBytes * 1024;
                this.maxWorkers = maxWorkers; //(maxWorkers == 1) ? 1:0;
                this.overlap = overlap;
                synchronize = new AutoResetEvent(false);
                cancel = new ManualResetEvent(false);
                resultQueue = new ConcurrentQueue<T>();
                jobQueue = new ConcurrentQueue<FileMapJoinData>();
                priorResult = new();
            }

            public void Start()
            {
                if (!Running) {
                    cancel.Reset();
                    Running = true;
                    Error = "";
                    joins = 0;
                    scheduledChunks = 0;
                    resultQueue.Clear();
                    jobQueue.Clear();
                    ThreadPool.QueueUserWorkItem((_) => ManageJobs(Path));
                }
            }

            public void Cancel()
            {
                if (Running) {
                    cancel.Set();
                }
            }


            //
            // PROTECTED INTERFACE
            //

            // The quantity of bytes that have been completed
            public long ByteCount { get { return Interlocked.Read(ref byteCount); } }
            protected long byteCount;

            // Normally false, this canel event can by set by the user calling Cancel,
            // or by the implementation Map or Join method calling cancel.Set.  The
            // base class manager thread periodically polls the event and, if it is
            // ever set, cancels the entire job and sets Cancelled property to true.
            // If necessary the implementation Map and Join methods can also check for
            // this event being set and exit early.
            protected readonly ManualResetEvent cancel;

            // When used from Join, this value contains the T result returned
            // from the prior call of Join
            protected T priorResult;

            // Partitioning size in bytes
            protected readonly int chunkSize;

            // Bytes of overlap between buffers
            protected int overlap;


            //
            // PRIVATE INTERFACE
            //

            void ManageJobs(string filePath)
            {
                try {
                    // Create all the chunk jobs (in order)
                    var chunkNum = 0;
                    FileInfo fileInfo = new(filePath);
                    //Logger.Log($"{filePath} is {fileInfo.Length} bytes");
                    for (long pos = 0; pos < fileInfo.Length; pos += chunkSize - overlap) {
                        jobQueue.Enqueue(
                            new FileMapJoinData()
                            {
                                Id = chunkNum++,
                                StartFpos = pos,
                            });
                    }
                    //Logger.Log($"{jobQueue.Count} chunks");
                    // Work until the queues are empty
                    while (jobQueue.Count != 0 ||
                           resultQueue.Count != 0 ||
                           scheduledChunks != 0) {
                        //Logger.Log($"manager {jobQueue.Count} {resultQueue.Count} {scheduledChunks}");
                        ScheduleChunks();
                        synchronize.WaitOne(1000);
                        JoinResults();
                        if (cancel.WaitOne(0)) {
                            Cancelled = true;
                            break;
                        }
                    }
                    Finish();
                }
                catch (Exception e) {
                    // In the background catch all exceptions, record the text
                    // for debugging, and abort the indexing process
                    Error = e.ToString();
                }
                Running = false;
                progress.Set();
            }

            void ScheduleChunks()
            {
                while (scheduledChunks < maxWorkers || maxWorkers < 1) {
                    if (jobQueue.TryDequeue(out FileMapJoinData job)) {
                        Interlocked.Add(ref scheduledChunks, 1);
                        ThreadPool.QueueUserWorkItem((_) => MapJob(job));
                        //Logger.Log($"scheduled chunk {job.Id}, currently {scheduledChunks} scheduled chunks");
                    }
                    else {
                        break;
                    }
                }
            }

            void JoinResults()
            {
                //var minWorkers = maxWorkers < 1 ? 0 : maxWorkers / 2;
                while (resultQueue.Count != 0) {
                    JoinNextResult();
                }
            }

            void JoinNextResult()
            {
                var progressMade = 0;
                List<T> resultBuf = new();
                while (resultQueue.TryDequeue(out T result)) {
                    resultBuf.Add(result);
                }
                if (resultBuf.Count > 0) {
                    resultBuf.Sort((a, b) => a.Id.CompareTo(b.Id));
                    if (joinMode == JoinMode.Reduce) {
                        throw new NotImplementedException();
                        //for (var i = 0; i < resultBuf.Count - 1; i++) {
                        //    var result1 = resultBuf[i];
                        //    var result2 = resultBuf[i + 1];
                        //    if (result1.Id == result2.Id - 1 &&
                        //        result1.Cycle == result2.Cycle) {
                        //        resultQueue.Enqueue(Join(result1, result2));
                        //        resultBuf.RemoveAt(i);
                        //        resultBuf.RemoveAt(i+1);
                        //        progressMade += 1;
                        //    }
                        //}
                    }
                    else if (joinMode == JoinMode.Sequential) {
                        var currentResult = resultBuf[0];
                        if (currentResult.Id == 0) {
                            priorResult = Join(currentResult, currentResult);
                            resultBuf.RemoveAt(0);
                            progressMade = 1;
                            joins++;
                        }
                        else if (joins != 0 && currentResult.Id == priorResult.Id + 1) {
                            priorResult = Join(priorResult, currentResult);
                            resultBuf.RemoveAt(0);
                            progressMade = 1;
                            joins++;
                        }
                    }
                    else { // JoinMode.None
                        progressMade = resultBuf.Count;
                        resultBuf.Clear();
                    }
                }
                foreach (var result in resultBuf) {
                    resultQueue.Enqueue(result);
                }
                if (progressMade != 0) {
                    Interlocked.Add(ref scheduledChunks, -progressMade);
                }
                progress.Set();
            }

            void MapJob(FileMapJoinData data)
            {
                try {
                    resultQueue.Enqueue(Map(data));
                }
                catch (Exception e) {
                    // In the background catch all exceptions, record the text
                    // for debugging, and abort the indexing process
                    Error = e.ToString();
                }
                synchronize.Set();
            }

            // private data
            readonly AutoResetEvent synchronize;
            readonly AutoResetEvent progress;
            readonly int maxWorkers;
            readonly ConcurrentQueue<FileMapJoinData> jobQueue;
            readonly ConcurrentQueue<T> resultQueue;
            int scheduledChunks;
            int joins;
        }

        // FileMapJoin job data
        public struct FileMapJoinData : IMapJoinData {
            public int Id { get; set; }
            public int Cycle { get; set; }
            public long StartFpos { get; set; }
        };
    }
}