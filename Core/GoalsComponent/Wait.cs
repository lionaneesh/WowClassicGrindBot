﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Core;

public sealed class Wait
{
    private readonly ManualResetEventSlim globalTime;
    private readonly CancellationToken token;

    public Wait(ManualResetEventSlim globalTime, CancellationTokenSource cts)
    {
        this.globalTime = globalTime;
        this.token = cts.Token;
    }

    public void Update()
    {
        globalTime.Wait();
        globalTime.Reset();
    }

    public bool Update(int timeoutMs)
    {
        bool result = globalTime.Wait(timeoutMs);
        if (!result)
        {
            return result;
        }

        globalTime.Reset();
        return result;
    }

    public void Fixed(int durationMs)
    {
        token.WaitHandle.WaitOne(durationMs);
    }

    [SkipLocalsInit]
    public bool Till(int timeoutMs, Func<bool> interrupt)
    {
        DateTime start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            if (interrupt())
                return false;

            Update();
        }

        return true;
    }

    [SkipLocalsInit]
    public float Until(int timeoutMs, Func<bool> interrupt)
    {
        DateTime start = DateTime.UtcNow;
        float elapsedMs;
        while ((elapsedMs = (float)(DateTime.UtcNow - start).TotalMilliseconds) < timeoutMs)
        {
            if (interrupt())
                return elapsedMs;

            Update();
        }

        return -elapsedMs;
    }

    public float UntilCount(int count, Func<bool> interrupt)
    {
        DateTime start = DateTime.UtcNow;
        for (int i = 0; i < count; i++)
        {
            if (interrupt())
                return (float)(DateTime.UtcNow - start).TotalMilliseconds;

            Update();
        }
        return -(float)(DateTime.UtcNow - start).TotalMilliseconds;
    }

    [SkipLocalsInit]
    public float Until(int timeoutMs, CancellationToken token)
    {
        DateTime start = DateTime.UtcNow;
        float elapsedMs;
        while ((elapsedMs = (float)(DateTime.UtcNow - start).TotalMilliseconds) < timeoutMs)
        {
            if (token.IsCancellationRequested)
                return elapsedMs;

            Update();
        }

        return -elapsedMs;
    }

    [SkipLocalsInit]
    public float Until(int timeoutMs, Func<bool> interrupt, Action repeat)
    {
        DateTime start = DateTime.UtcNow;
        float elapsedMs;
        while ((elapsedMs = (float)(DateTime.UtcNow - start).TotalMilliseconds) < timeoutMs)
        {
            repeat.Invoke();
            if (interrupt())
                return elapsedMs;

            Update();
        }

        return -elapsedMs;
    }

    [SkipLocalsInit]
    public float AfterEquals<T>(int timeoutMs, int updateCount, Func<T> func, Action? repeat = null)
    {
        DateTime start = DateTime.UtcNow;
        float elapsedMs;
        while ((elapsedMs = (float)(DateTime.UtcNow - start).TotalMilliseconds) < timeoutMs)
        {
            T initial = func();

            repeat?.Invoke();

            for (int i = 0; i < updateCount; i++)
                Update();

            if (EqualityComparer<T>.Default.Equals(initial, func()))
                return elapsedMs;
        }

        return -elapsedMs;
    }

    public void While(Func<bool> condition)
    {
        while (condition())
        {
            Update();
        }
    }
}
