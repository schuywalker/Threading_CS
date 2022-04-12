﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Authors: Kelsey McMahon, Schuyler Asplin, Whitney Bolar
// for CSCD371 .Net with Mark Michaelis and Grant Erickson

namespace Assignment;

public record struct PingResult(int ExitCode, string? StdOutput);

public class PingProcess
{
    private ProcessStartInfo StartInfo { get; } = new("ping");

    public PingResult Run(string hostNameOrAddress)
    {
        StartInfo.Arguments = hostNameOrAddress;
        StringBuilder? stringBuilder = null;
        void updateStdOutput(string? line) =>
            (stringBuilder??=new StringBuilder()).AppendLine(line);
        Process process = RunProcessInternal(StartInfo, updateStdOutput, default, default);
        return new PingResult( process.ExitCode, stringBuilder?.ToString());
    }

    public Task<PingResult> RunTaskAsync(string hostNameOrAddress)
    {
        Task<PingResult> task = Task.Run(() => Run(hostNameOrAddress)
            );

        task.Wait();
        return task;
    }

    async public Task<PingResult> RunAsync(string hostNameOrAddress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<PingResult> task = Task.Run(() => Run(hostNameOrAddress));
        return await task;
    }

    async public Task<PingResult> RunAsync(string hostNameOrAddress, IProgress<string?> progress, CancellationToken cancellationToken = default)
    {
        Task<PingResult> task = RunAsync(hostNameOrAddress, cancellationToken);
        progress.Report(task.Result.StdOutput);

        return await task;
    }

    async public Task<PingResult> RunAsync(params string[] hostNameOrAddresses)
    {
        StringBuilder? stringBuilder = new();

        ParallelQuery<Task<PingResult>> all = hostNameOrAddresses.AsParallel().Select(async item =>
        {
            Task<PingResult> task = Task.Run(() => Run(item));
            await task;
            return task.Result;
        });

        await Task.WhenAll(all);
        int total = all.Aggregate(0, (total, item) => total + item.Result.ExitCode);
        stringBuilder.Append(all.Aggregate("", (output, item) => output.Trim() + item.Result.StdOutput));

        return new PingResult(total, stringBuilder?.ToString().Trim());
    }

    async public Task<PingResult> RunLongRunningAsync(
        string hostNameOrAddress, CancellationToken cancellationToken = default)
    {
        Func<PingResult> getPingResult = delegate ()
        {
            StartInfo.Arguments = hostNameOrAddress;
            StringBuilder? stringBuilder = null;
            void updateStdOutput(string? line) =>
                (stringBuilder ??= new StringBuilder()).AppendLine(line);
            cancellationToken.ThrowIfCancellationRequested();
            Process process = RunProcessInternal(StartInfo, updateStdOutput, default, cancellationToken);
            return new PingResult(process.ExitCode, stringBuilder?.ToString());
        };

        Task<PingResult> task = Task.Factory.StartNew<PingResult>(getPingResult, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Current);

        return await task;
    }

    private Process RunProcessInternal(
        ProcessStartInfo startInfo,
        Action<string?>? progressOutput,
        Action<string?>? progressError,
        CancellationToken token)
    {
        var process = new Process
        {
            StartInfo = UpdateProcessStartInfo(startInfo)
        };
        return RunProcessInternal(process, progressOutput, progressError, token);
    }

    private Process RunProcessInternal(
        Process process,
        Action<string?>? progressOutput,
        Action<string?>? progressError,
        CancellationToken token)
    {
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += OutputHandler;
        process.ErrorDataReceived += ErrorHandler;

        try
        {
            if (!process.Start())
            {
                return process;
            }

            token.Register(obj =>
            {
                if (obj is Process p && !p.HasExited)
                {
                    try
                    {
                        p.Kill();
                    }
                    catch (Win32Exception ex)
                    {
                        throw new InvalidOperationException($"Error cancelling process{Environment.NewLine}{ex}");
                    }
                }
            }, process);


            if (process.StartInfo.RedirectStandardOutput)
            {
                process.BeginOutputReadLine();
            }
            if (process.StartInfo.RedirectStandardError)
            {
                process.BeginErrorReadLine();
            }

            if (process.HasExited)
            {
                return process;
            }
            process.WaitForExit();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"Error running '{process.StartInfo.FileName} {process.StartInfo.Arguments}'{Environment.NewLine}{e}");
        }
        finally
        {
            if (process.StartInfo.RedirectStandardError)
            {
                process.CancelErrorRead();
            }
            if (process.StartInfo.RedirectStandardOutput)
            {
                process.CancelOutputRead();
            }
            process.OutputDataReceived -= OutputHandler;
            process.ErrorDataReceived -= ErrorHandler;

            if (!process.HasExited)
            {
                process.Kill();
            }

        }
        return process;

        void OutputHandler(object s, DataReceivedEventArgs e)
        {
            progressOutput?.Invoke(e.Data);
        }

        void ErrorHandler(object s, DataReceivedEventArgs e)
        {
            progressError?.Invoke(e.Data);
        }
    }

    private static ProcessStartInfo UpdateProcessStartInfo(ProcessStartInfo startInfo)
    {
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.UseShellExecute = false;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;

        return startInfo;
    }
}