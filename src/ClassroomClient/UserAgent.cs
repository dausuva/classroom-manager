// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO.Pipes;
using System.Security.Principal;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Runs in the signed-in user's session with the user's normal (standard) token.
/// Connects to the service over a local pipe and executes the requests it forwards.
/// </summary>
sealed class UserAgent
{
    readonly CommandRouter _router = new(new ClientConfig(), forwarder: null, BlockHandler.ForHelper());

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", UserBridge.PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(5000, ct);
                // Authenticate before sending a hello or accepting any service-trusted request.
                PipePeerIdentity.VerifyService(pipe);

                var writeLock = new SemaphoreSlim(1, 1);
                await PipeFraming.WriteAsync(pipe, new PipeMessage
                {
                    Id = 0,
                    User = Environment.UserName,
                    Session = System.Diagnostics.Process.GetCurrentProcess().SessionId,
                }, ct);
                Log.Write("Connected to service.");
                HelperState.ServiceConnected();

                while (true)
                {
                    var msg = await PipeFraming.ReadAsync(pipe, ct);
                    if (msg.Req is null) continue;

                    _ = Task.Run(async () =>
                    {
                        Response response;
                        try
                        {
                            // The same-machine check already ran in the service's CommandRouter
                            // before it forwarded this request over the pipe; nothing reaches
                            // here that wasn't already allowed.
                            response = await _router.HandleAsync(msg.Req, sameMachine: false, CallerIdentity.Service, ct);
                        }
                        catch (Exception ex) { response = Response.Fail(ex.Message); }

                        await writeLock.WaitAsync(ct);
                        try { await PipeFraming.WriteAsync(pipe, new PipeMessage { Id = msg.Id, Res = response }, ct); }
                        catch { /* pipe closed; the read loop will notice */ }
                        finally { writeLock.Release(); }
                    }, ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // service not running yet, or connection dropped: retry
                HelperState.ServiceLost();
                Log.Write($"Service connection: {ex.GetType().Name} {ex.Message}");
            }

            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
        }
    }
}

/// <summary>Marshals work onto the WinForms UI thread of the user helper.</summary>
static class Ui
{
    static Control? _sync;

    public static void Init()
    {
        _sync = new Control();
        _ = _sync.Handle; // force handle creation on the UI thread
    }

    public static Task RunAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_sync is null) { tcs.SetException(new InvalidOperationException("UI not available.")); return tcs.Task; }
        _sync.BeginInvoke(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}
