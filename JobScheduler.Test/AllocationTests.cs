﻿using JobScheduler.Test.Utils;
using JobScheduler.Test.Utils.CustomConstraints;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;

namespace JobScheduler.Test;

[SuppressMessage("Assertion", "NUnit2045:Use Assert.Multiple",
    Justification = "Multiple asserts are not appropriate as later code")]
internal class AllocationTests : SchedulerTestFixture
{
    public AllocationTests(int threads) : base(threads) { }

    private class TestClass
    {
        public string Data = "Some data here.";
    }

    private struct TestStruct
    {
        public long Data = 0xDEADBEEF;

        public TestStruct() { }
    }

    [Test]
    public void CreatingClassDoesAllocate()
    {
        // sanity test for allocation fixture
        Assert.That(() =>
        {
            _ = new TestClass();
        }, Is.AllocatingMemory());
    }

    [Test]
    public void CreatingStructDoesNotAllocate()
    {
        // sanity test for allocation fixture
        Assert.That(() =>
        {
            _ = new TestStruct();
        }, Is.Not.AllocatingMemory());
    }

    [Test]
    [TestCase(true)]
    [TestCase(false)]
    public void RegularJobDoesNotAllocate(bool manuallyComplete)
    {
        var job = new SleepJob(2); // allocates

        JobHandle handle = default;
        JobHandle handle2 = default;

        // we expect the very first job to allocate
        Assert.That(() =>
        {
            handle = Scheduler.Schedule(job);
        }, Is.AllocatingMemory());

        Assert.That(() => { Scheduler.Flush(); }, Is.Not.AllocatingMemory());
        if (manuallyComplete) Assert.That(() => { handle.Complete(); }, Is.Not.AllocatingMemory());
        else Thread.Sleep(100);
        Assert.That(() => { handle2 = Scheduler.Schedule(job); }, Is.Not.AllocatingMemory());
        Assert.That(() => { Scheduler.Flush(); }, Is.Not.AllocatingMemory());
        Assert.That(() => { handle2.Complete(); }, Is.Not.AllocatingMemory());
    }

    [Test]
    public void SimultaneousJobsDoNotAllocate()
    {
        var job = new SleepJob(5); // allocates

        JobHandle handle1 = default;
        JobHandle handle2 = default;
        // we expect the first 2 jobs to allocate
        Assert.That(() => { handle1 = Scheduler.Schedule(job); }, Is.AllocatingMemory());
        Assert.That(() => { handle2 = Scheduler.Schedule(job); }, Is.AllocatingMemory());

        // the rest of everything should not allocate
        Assert.That(() => { Scheduler.Flush();  }, Is.Not.AllocatingMemory());
        Assert.That(() => { handle1.Complete(); }, Is.Not.AllocatingMemory());
        Assert.That(() => { handle2.Complete(); }, Is.Not.AllocatingMemory());
        Assert.That(() => { handle1 = Scheduler.Schedule(job); }, Is.Not.AllocatingMemory());
        Assert.That(() => { handle2 = Scheduler.Schedule(job); }, Is.Not.AllocatingMemory());
        Assert.That(() => { Scheduler.Flush(); }, Is.Not.AllocatingMemory());
        Assert.That(() => { handle2.Complete(); }, Is.Not.AllocatingMemory());
        Assert.That(() => { handle1.Complete(); }, Is.Not.AllocatingMemory());
    }

    [Test]
    public void DependentJobsDoNotAllocate()
    {
        var job = new SleepJob(5); // allocates

        JobHandle handle1 = default;
        JobHandle handle2 = default;
        // we expect the first 2 jobs to allocate
        Assert.That(() => { handle1 = Scheduler.Schedule(job); }, Is.AllocatingMemory());
        Assert.That(() => { handle2 = Scheduler.Schedule(job, handle1); }, Is.AllocatingMemory());

        // the rest of everything should not allocate
        Assert.That(() => { Scheduler.Flush(); }, Is.Not.AllocatingMemory());
        Assert.That(() => { handle1.Complete(); }, Is.Not.AllocatingMemory());
        Assert.That(() => { handle2.Complete(); }, Is.Not.AllocatingMemory());
        Assert.That(() => { handle1 = Scheduler.Schedule(job); }, Is.Not.AllocatingMemory());
        Assert.That(() => { handle2 = Scheduler.Schedule(job, handle1); }, Is.Not.AllocatingMemory());
        Assert.That(() => { Scheduler.Flush(); }, Is.Not.AllocatingMemory());
        Assert.That(() => { handle2.Complete(); }, Is.Not.AllocatingMemory());
        Assert.That(() => { handle1.Complete(); }, Is.Not.AllocatingMemory());
    }


    [Test]
    public void CombinedJobsDoNotAllocate()
    {
        var job = new SleepJob(5); // allocates
        var handles = new JobHandle[2];
        JobHandle combined = default;

        // we expect the first 3 jobs to allocate
        Assert.That(() => { handles[0] = Scheduler.Schedule(job); }, Is.AllocatingMemory());
        Assert.That(() => { handles[1] = Scheduler.Schedule(job); }, Is.AllocatingMemory());
        Assert.That(() => { combined = Scheduler.CombineDependencies(handles); }, Is.AllocatingMemory());

        // the rest of everything should not allocate
        Assert.That(() => { Scheduler.Flush(); }, Is.Not.AllocatingMemory());
        Assert.That(() => { combined.Complete(); }, Is.Not.AllocatingMemory());
        Assert.That(() => { handles[0] = Scheduler.Schedule(job); }, Is.Not.AllocatingMemory());
        Assert.That(() => { handles[1] = Scheduler.Schedule(job); }, Is.Not.AllocatingMemory());
        Assert.That(() => { combined = Scheduler.CombineDependencies(handles); }, Is.Not.AllocatingMemory());
        Assert.That(() => { Scheduler.Flush(); }, Is.Not.AllocatingMemory());
        Assert.That(() => { combined.Complete(); }, Is.Not.AllocatingMemory());
        Assert.That(() => { handles[0].Complete(); }, Is.Not.AllocatingMemory());
        Assert.That(() => { handles[1].Complete(); }, Is.Not.AllocatingMemory());
    }
}