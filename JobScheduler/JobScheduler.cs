using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace JobScheduler;

/// <summary>
/// A <see cref="JobScheduler"/> schedules and processes <see cref="IJob"/>s asynchronously. Better-suited for larger jobs due to its underlying events. 
/// </summary>
public class JobScheduler : IDisposable
{
    /// <summary>
    /// Tracks which thread the JobScheduler was constructed on
    /// </summary>
    private int MainThreadID { get; }

    // Tracks how many threads are active; interlocked
    private int _threadsActive = 0;

    // internally visible for testing
    internal int ThreadsActive => _threadsActive;

    /// <summary>
    /// Pairs a <see cref="JobHandle"/> with its <see cref="IJob"/>
    /// </summary>
    private readonly struct JobMeta
    {
        public JobMeta(in JobHandle jobHandle, IJob? job)
        {
            JobHandle = jobHandle;
            Job = job;
        }

        public JobHandle JobHandle { get; }
        public IJob? Job { get; }
    }

    /// <summary>
    /// Creates an instance of the <see cref="JobScheduler"/>
    /// </summary>
    /// <param name="threadPrefix">The thread prefix to use. The thread will be named "prefix0" for the first thread, "prefix1" for the second thread, etc.</param>
    /// <param name="threads">The amount of worker threads to use. If zero the scheduler will use the amount of processors available.</param>
    public JobScheduler(string threadPrefix, int threads = 0)
    {
        MainThreadID = Thread.CurrentThread.ManagedThreadId;

        if (threads <= 0) threads = Environment.ProcessorCount;

        // spawn all the child threads
        for (var i = 0; i < threads; i++)
        {
            var thread = new Thread(() => Loop(CancellationTokenSource.Token))
            {
                Name = $"{threadPrefix}{i}"
            };
            thread.Start();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsMainThread() => Thread.CurrentThread.ManagedThreadId == MainThreadID;

    // The main loop for each child thread
    private void Loop(CancellationToken token)
    {
        Interlocked.Increment(ref _threadsActive);
        // as long as we're not exiting, loop
        while (!token.IsCancellationRequested)
        {
            // wait for a signal that we should check the queue
            CheckQueueEvent.WaitOne();
            CheckQueueEvent.Reset();

            while (Jobs.TryDequeue(out var jobMeta))
            {
                // if we've got dependencies, we gotta resolve that first
                JobInfoPool.JobInfo info;
                // TODO: R/W lock
                lock (JobInfoPool)
                {
                    info = JobInfoPool.GetInfo(jobMeta.JobHandle.JobID);
                }

                // check multiple dependencies
                if (info.Dependencies is not null)
                {
                    foreach (var dependency in info.Dependencies)
                    {
                        dependency.Complete();
                    }
                }

                // check single dependency
                if (info.DependencyID != -1)
                {
                    Complete(info.DependencyID);
                }

                // it might be null if this is a job generated with CombineDependencies
                jobMeta.Job?.Execute();

                // todo: see if we can avoid locks
                // the purpose of this lock is to ensure that the Complete method always subscribes and listens to an existant signal.
                lock (JobInfoPool)
                {
                    // remove the job from circulation
                    var handle = JobInfoPool.MarkComplete(jobMeta.JobHandle.JobID);
                    // If JobScheduler.Complete was called on this job by a different thread, it told the job pool with Subscribe that we should ping,
                    // and that Complete would handle recycling. We notify the event here.
                    handle?.Set();
                }
            }
        }
        Interlocked.Decrement(ref _threadsActive);
        if (_threadsActive == 0)
        {
            // if we're the last thread active, we don't need this event
            // to unblock potentially active threads anymore.
            CheckQueueEvent?.Dispose();
        }
    }

    private JobHandle Schedule(IJob? job, JobHandle? dependency = null, JobHandle[]? dependencies = null)
    {
        if (!IsMainThread()) throw new InvalidOperationException($"Can only call {nameof(Schedule)} from the thread that spawned the {nameof(JobScheduler)}!");

        int jobID;
        lock (JobInfoPool)
        {
            jobID = JobInfoPool.Schedule(dependency?.JobID ?? -1, dependencies);
            var info = JobInfoPool.GetInfo(jobID);
        }

        var handle = new JobHandle(this, jobID);

        var jobMeta = new JobMeta(in handle, job);
        QueuedJobs.Add(jobMeta);
        return handle;
    }

    /// <summary>
    /// Schedules a job. It is only queued up, and will only begin processing when the user calls <see cref="Flush()"/>.
    /// </summary>
    /// <param name="job">The job to process</param>
    /// <param name="dependency">A job that must complete before this job can be run</param>
    /// <returns>Its <see cref="JobHandle"/>.</returns>
    public JobHandle Schedule(IJob job, JobHandle? dependency = null)
    {
        if (dependency is not null) CheckDependency(dependency.Value);
        return Schedule(job, dependency, null);
    }


    /// <summary>
    /// Combine multiple dependencies into a single <see cref="JobHandle"/> which is scheduled.
    /// </summary>
    /// <remarks>The user must transfer ownership of this array to the scheduler, up until after <see cref="Complete"/> is
    /// called on this task (or one of its dependants). If the data is modified, the dependency system will break.</remarks>
    /// <param name="dependencies">A list of handles to depend on.</param>
    /// <returns>The combined <see cref="JobHandle"/></returns>
    // TODO: consider doing some native allocation or cache to track these? would remove need to transfer ownership to user
    public JobHandle CombineDependencies(JobHandle[] dependencies)
    {
        foreach (var dependency in dependencies)
        {
            CheckDependency(dependency);
        }
        return Schedule(null, null, dependencies);
    }
    private void CheckDependency(JobHandle dependency)
    {
        if (!ReferenceEquals(dependency.Scheduler, this))
            throw new InvalidOperationException($"Job dependency was scheduled with a different {nameof(JobScheduler)}!");
    }

    /// <summary>
    /// Flushes all queued <see cref="IJob"/>'s to the worker threads. 
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Flush()
    {
        if (!IsMainThread()) throw new InvalidOperationException($"Can only call {nameof(Flush)} from the thread that spawned the {nameof(JobScheduler)}!");


        // QueuedJobs is guaranteed to be scheduled in FIFO dependency order
        // So all dependencies are guaranteed to be scheduled before their dependants
        foreach (var job in QueuedJobs)
        {
            lock (JobInfoPool)
            {
                JobInfoPool.MarkFlushed(job.JobHandle.JobID);
            }
            // because this is a concurrentqueue (linkedlist implementation under the hood)
            // we don't have to worry about ordering issues (if someone dequeues while we do this, it'll just take the first one we added, which is fine)
            Jobs.Enqueue(job);
        }
        
        // clear the the incoming queue
        QueuedJobs.Clear();

        // tell the child processes that we've updated the queue, in case they've stalled out
        CheckQueueEvent.Set();
    }

    /// <summary>
    /// Blocks the thread until the given job ID has been completed. Can be called from Jobs.
    /// </summary>
    /// <param name="jobID"></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Complete(int jobID)
    {
        ManualResetEvent handle;
        lock (JobInfoPool)
        {
            // if we're already done with this job we don't care about signals.
            if (JobInfoPool.IsComplete(jobID)) return;
            // if we never flushed the job, we'll be waiting forever!!!
            if (!JobInfoPool.IsFlushed(jobID)) throw new InvalidOperationException($"Cannot wait on a job that is not flushed to the workers! Call {nameof(Flush)} first.");
            // increments a subscription counter. This ensures that the job that completes this will ping the handle and won't dispose it yet.
            // If we didn't have this, there would be a race condition between multiple threads which wait for a job's completion.
            handle = JobInfoPool.SubscribeToHandle(jobID);
        }
        handle.WaitOne();

        lock (JobInfoPool)
        {
            // Return to pool. Ensures that nobody else is subscribed first, so this will always be the last person to have subscribed to a handle.
            JobInfoPool.ReturnHandle(jobID, handle);
        }
    }

    // Tracks the overall state of all threads; when canceled in Dispose, all child threads are exited
    private CancellationTokenSource CancellationTokenSource { get; } = new();

    // Informs child threads that they should check the queue for more jobs
    private ManualResetEvent CheckQueueEvent { get; } = new(false);

    // Jobs scheduled by the client, but not yet flushed to the threads
    private List<JobMeta> QueuedJobs { get; } = new();

    // Jobs flushed and waiting to be picked up by worker threads
    private ConcurrentQueue<JobMeta> Jobs { get; } = new();

    // Tracks each job from scheduling to completion; when they complete, however, their data is removed from the pool and recycled.
    private JobInfoPool JobInfoPool { get; } = new();

    /// <summary>
    /// Disposes all internals and notifies all threads to cancel.
    /// </summary>
    public void Dispose()
    {
        if (!IsMainThread()) throw new InvalidOperationException($"Can only call {nameof(Dispose)} from the thread that spawned the {nameof(JobScheduler)}!");
        // notify all threads to cancel
        CancellationTokenSource.Cancel(false);
        QueuedJobs.Clear();
        Jobs.Clear();
        // In case some thread is waiting on the queue, bump them out.
        // They will then dispose this event, finishing the Dispose().
        // At that point, all references should be dead and the GC should take over, if the user nukes their own reference.
        CheckQueueEvent.Set();
    }
}