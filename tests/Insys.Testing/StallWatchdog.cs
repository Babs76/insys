/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Testing
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
using System.Diagnostics;

namespace Insys.Testing;

/// <summary>
/// Diagnostics for a test that stalls only under load: after <c>after</c>, and every 20 seconds while the test runs, it
/// writes what the test database is doing (sessions not idle, what they wait on, and who blocks them) and the thread
/// pool's state to the test output, so a CI failure shows where the time went.
/// </summary>
public sealed class StallWatchdog : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private StallWatchdog(TestDatabase database, TimeSpan after, string label)
    {
        var started = Stopwatch.StartNew();
        _loop = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(after, _stop.Token);
                while (!_stop.IsCancellationRequested)
                {
                    ThreadPool.GetAvailableThreads(out var workers, out _);
                    ThreadPool.GetMaxThreads(out var maxWorkers, out _);
                    var sessions = await database.ScalarAsOwner<string>("""
                        SELECT string_agg(format('[pid %s %s %s wait=%s/%s blocked_by=%s %ss] %s', pid, usename, state, wait_event_type, wait_event,
                                                 pg_blocking_pids(pid), round(extract(epoch FROM now() - query_start)), left(regexp_replace(query, '\s+', ' ', 'g'), 160)),
                                          E'\n')
                        FROM pg_stat_activity WHERE datname = current_database() AND state <> 'idle' AND pid <> pg_backend_pid()
                        """);
                    var connections = await database.ScalarAsOwner<string>("""
                        SELECT string_agg(format('%s %s: %s', usename, state, n), ', ' ORDER BY usename, state)
                        FROM (SELECT usename, coalesce(state, '-') AS state, count(*) AS n FROM pg_stat_activity
                              WHERE datname = current_database() AND pid <> pg_backend_pid() GROUP BY usename, state) c
                        """);
                    var report = $"STALL {label} at {started.Elapsed.TotalSeconds:F0}s ({DateTimeOffset.UtcNow:HH:mm:ss}Z): threads {ThreadPool.ThreadCount} (busy {maxWorkers - workers}), "
                                 + $"pending work items {ThreadPool.PendingWorkItemCount}\nconnections: {connections}\n{sessions ?? "(no active sessions)"}";
                    Console.WriteLine(report);
                    if (Environment.GetEnvironmentVariable("INSYS_STALL_LOG") is { Length: > 0 } file)
                    {
                        await File.AppendAllTextAsync(file, report + "\n\n", _stop.Token);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(20), _stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public static StallWatchdog Start(TestDatabase database, TimeSpan after, string label) => new(database, after, label);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }
}
