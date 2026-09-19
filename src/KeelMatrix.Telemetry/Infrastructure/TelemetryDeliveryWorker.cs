// Copyright (c) KeelMatrix

using KeelMatrix.Telemetry.ProjectIdentity;
using KeelMatrix.Telemetry.Serialization;

namespace KeelMatrix.Telemetry.Infrastructure {
    /// <summary>
    /// Background worker responsible for telemetry planning (markers/events)
    /// and durable delivery (queue + HTTP).
    /// </summary>
    internal sealed class TelemetryDeliveryWorker : IDisposable {
        private readonly TelemetryRuntimeContext runtimeContext;
        private readonly RuntimeInfo runtimeInfo;
        private readonly IProjectIdentityProvider projectIdentityProvider;
        private ITelemetryQueue? queue;
        private readonly ITelemetrySender httpSender;

        private readonly SemaphoreSlim signal = new(0, 1);
        private readonly CancellationTokenSource cts = new();
        private int signalPending;

        // Signals set from calling threads (must be non-blocking to set).
        private int activationRequested; // 0/1
        private int heartbeatRequested;  // 0/1

        // Worker state (only touched on worker thread)
        private TelemetryDispatcher? dispatcher;

        private int hasPendingWork; // 0 = false, 1 = true

        private readonly object backoffLock = new();
        private TimeSpan currentBackoff = TimeSpan.Zero;

        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan QueueRecoveryPollInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan InitialQueueRecoveryDelay = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan MaxQueueRecoveryDelay = TimeSpan.FromSeconds(1);
        private const int MaxQueueRecoveryAttempts = 8;

        private int queueRecoveryResetRequested;
        private int queueInitializationFailures;
        private int queueWriteFailures;

        private static readonly ThreadLocal<Random> JitterRandom =
            new(() => new Random(unchecked((Environment.TickCount * 31) + Environment.CurrentManagedThreadId)));

#pragma warning disable S4487 // Unread "private" fields should be removed
        private readonly Task _workerTask; // left for observing a potential exception during debug
#pragma warning restore S4487

        internal TelemetryDeliveryWorker(TelemetryRuntimeContext runtimeContext, RuntimeInfo runtimeInfo)
            : this(runtimeContext, runtimeInfo, new ProjectIdentityProvider(runtimeContext, runtimeInfo)) {
        }

        internal TelemetryDeliveryWorker(
            TelemetryRuntimeContext runtimeContext,
            RuntimeInfo runtimeInfo,
            IProjectIdentityProvider projectIdentityProvider)
            : this(runtimeContext, runtimeInfo, projectIdentityProvider, new TelemetryHttpSender(runtimeContext.Url)) {
        }

        internal TelemetryDeliveryWorker(
            TelemetryRuntimeContext runtimeContext,
            RuntimeInfo runtimeInfo,
            IProjectIdentityProvider projectIdentityProvider,
            ITelemetrySender telemetrySender) {
            this.runtimeContext = runtimeContext;
            this.runtimeInfo = runtimeInfo;
            this.projectIdentityProvider = projectIdentityProvider ?? throw new ArgumentNullException(nameof(projectIdentityProvider));
            httpSender = telemetrySender ?? throw new ArgumentNullException(nameof(telemetrySender));

            _workerTask = Task.Run(RunAsync);

            AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
#if NET8_0_OR_GREATER
            AppDomain.CurrentDomain.DomainUnload += (_, _) => Dispose();
#endif

            // Wake immediately to process backlog (disk queue) even without requests.
            Signal();
        }

        /// <summary>
        /// Requests activation emission. Must not block.
        /// </summary>
        internal void RequestActivation() {
            // Set flag; no I/O.
            Interlocked.Exchange(ref activationRequested, 1);
            Interlocked.Exchange(ref queueRecoveryResetRequested, 1);
            Signal();
        }

        /// <summary>
        /// Requests heartbeat emission. Must not block.
        /// </summary>
        internal void RequestHeartbeat() {
            // Set flag; no I/O.
            Interlocked.Exchange(ref heartbeatRequested, 1);
            Interlocked.Exchange(ref queueRecoveryResetRequested, 1);
            Signal();
        }

        /// <summary>
        /// Signals the worker to wake up. Must not block.
        /// </summary>
        private void Signal() {
            if (Interlocked.Exchange(ref signalPending, 1) == 1)
                return;

            try {
                signal.Release();
            }
            catch {
                Volatile.Write(ref signalPending, 0);
            }
        }

        private async Task RunAsync() {
            var token = cts.Token;
            bool identitiesResolved = false;

            while (!token.IsCancellationRequested) {
                try {
                    // The timeout gives stale processing claims a bounded repeated recovery
                    // opportunity even when the process receives no new tracking call.
                    _ = await signal.WaitAsync(QueueRecoveryPollInterval, token).ConfigureAwait(false);
                    Volatile.Write(ref signalPending, 0);
                }
                catch {
                    break;
                }

                if (Interlocked.Exchange(ref queueRecoveryResetRequested, 0) == 1) {
                    queueInitializationFailures = 0;
                    queueWriteFailures = 0;
                }

                if (TelemetryConfig.IsTelemetryDisabled() || TelemetryConfig.ResolveRepositoryTelemetryDisableOnWorkerThread()) {
                    Interlocked.Exchange(ref hasPendingWork, 0);
                    ResetBackoff();
                    continue;
                }

                try {
                    runtimeContext.EnsureRootDirectoryResolvedOnWorkerThread();
                }
                catch {
                    // swallow
                }

                if (queue is null && queueInitializationFailures >= MaxQueueRecoveryAttempts)
                    continue;

                var telemetryQueue = GetQueueOnWorkerThread();
                if (telemetryQueue is null) {
                    queueInitializationFailures++;
                    if (queueInitializationFailures <= MaxQueueRecoveryAttempts) {
                        await ApplyQueueRecoveryDelay(queueInitializationFailures, token).ConfigureAwait(false);
                        Signal();
                    }

                    continue;
                }

                queueInitializationFailures = 0;

                // Resolve telemetry identities once on the worker thread (best-effort).
                if (!identitiesResolved && !TelemetryConfig.IsTelemetryDisabled()) {
                    try {
                        _ = projectIdentityProvider.EnsureResolvedOnWorkerThread();
                        identitiesResolved = true;
                    }
                    catch {
                        // Broken identity state must hard-disable telemetry so we never emit
                        // under a placeholder or partial telemetry identity.
                        TelemetryConfig.DisableTelemetryForCurrentProcess();
                    }
                }

                // Plan & enqueue new telemetry based on requests (marker I/O happens here, not on caller)
                bool requestsNeedRetry = false;
                try {
                    requestsNeedRetry = ProcessRequestsOnWorkerThread(telemetryQueue);
                }
                catch {
                    // swallow; telemetry must never impact host
                }

                if (requestsNeedRetry) {
                    queueWriteFailures++;
                    if (queueWriteFailures <= MaxQueueRecoveryAttempts) {
                        await ApplyQueueRecoveryDelay(queueWriteFailures, token).ConfigureAwait(false);
                        Signal();
                    }
                    else {
                        // Best-effort capacity is bounded. The request flags remain set as
                        // durable intent, but no further I/O is attempted until a later
                        // public request resets this budget.
                    }
                }
                else {
                    queueWriteFailures = 0;
                }

                // Deliver any queued items (including backlog from previous runs)
                if (TelemetryConfig.IsTelemetryDisabled()) {
                    // Policy: if disabled, do not send anything, including backlog.
                    // Also avoid touching the queue to prevent unintended I/O.
                    Interlocked.Exchange(ref hasPendingWork, 0);
                    ResetBackoff();
                    continue;
                }

                while (!token.IsCancellationRequested) {
                    if (TelemetryConfig.IsTelemetryDisabled()) {
                        Interlocked.Exchange(ref hasPendingWork, 0);
                        ResetBackoff();
                        break;
                    }

                    bool anyAttempted = false;
                    bool anyFailed = false;

                    try {
                        var claimedItems = telemetryQueue.TryClaim(4).ToList();
                        for (var index = 0; index < claimedItems.Count; index++) {
                            var item = claimedItems[index];

                            // Opt-out can change while an earlier request is in flight. Claims
                            // that have not started must be returned without counting a failure.
                            if (TelemetryConfig.IsTelemetryDisabled()) {
                                telemetryQueue.Release(item);
                                for (index++; index < claimedItems.Count; index++)
                                    telemetryQueue.Release(claimedItems[index]);

                                Interlocked.Exchange(ref hasPendingWork, 0);
                                ResetBackoff();
                                break;
                            }

                            anyAttempted = true;

                            try {
                                if (await httpSender.TrySendAsync(item.Envelope.PayloadJson, token).ConfigureAwait(false)) {
                                    telemetryQueue.Complete(item);
                                }
                                else {
                                    telemetryQueue.Abandon(item);
                                    anyFailed = true;
                                }
                            }
                            catch {
                                telemetryQueue.Abandon(item);
                                anyFailed = true;
                            }
                        }
                    }
                    catch { /* swallow */ }

                    if (!anyAttempted) {
                        Interlocked.Exchange(ref hasPendingWork, 0);
                        break;
                    }

                    if (anyFailed) {
                        await ApplyBackoff(token).ConfigureAwait(false);
                        Signal(); // ensure retry even without new enqueue
                        break;
                    }

                    ResetBackoff();
                }
            }
        }

        /// <summary>
        /// Runs only on the worker thread.
        /// Performs all I/O needed to decide whether to emit activation/heartbeat,
        /// serializes events, enqueues them durably, then commits marker files.
        /// </summary>
        private bool ProcessRequestsOnWorkerThread(ITelemetryQueue telemetryQueue) {
            if (TelemetryConfig.IsTelemetryDisabled())
                return false;

            // Drain request flags first to avoid any compute when nothing was requested.
            var doActivation = Interlocked.Exchange(ref activationRequested, 0) == 1;
            var doHeartbeat = Interlocked.Exchange(ref heartbeatRequested, 0) == 1;

            if (!doActivation && !doHeartbeat)
                return false;

            // Resolve telemetry identities on the worker thread (cached for process lifetime).
            ResolvedTelemetryIdentity identities;
            try {
                identities = projectIdentityProvider.EnsureResolvedOnWorkerThread();
            }
            catch {
                // Broken identity state must prevent all emission for this process.
                TelemetryConfig.DisableTelemetryForCurrentProcess();
                return false;
            }

            if (string.IsNullOrWhiteSpace(identities.InstallationHash)) {
                TelemetryConfig.DisableTelemetryForCurrentProcess();
                return false;
            }

            if (!identities.HasProjectIdentity)
                return false;

            // Create dispatcher/state on worker thread (marker I/O happens inside TelemetryState).
            dispatcher ??= new TelemetryDispatcher(runtimeContext, runtimeInfo, identities);

            // Needed for "activation suppresses heartbeat until next week".
            var currentWeek = TelemetryClock.GetCurrentIsoWeek();
            bool activationSentThisRun = false;
            bool requestsNeedRetry = false;

            if (doActivation) {
                try {
                    var evt = dispatcher.TryCreateActivationEvent();
                    if (evt != null) {
                        var json = TelemetrySerializer.Serialize(evt, runtimeContext.ToolName);
                        if (json != null) {
                            // Durable queue write + marker commit are I/O; safe here.
                            if (telemetryQueue.Enqueue(json)) {
                                dispatcher.CommitActivation();
                                activationSentThisRun = true;

                                // Suppress heartbeat for the activation week so the first heartbeat is NEXT week.
                                dispatcher.CommitHeartbeat(currentWeek);

                                Interlocked.Exchange(ref hasPendingWork, 1);
                            }
                            else {
                                // A failed/null queue is not an accepted event and must not
                                // commit either suppression marker.
                                Interlocked.Exchange(ref activationRequested, 1);
                                requestsNeedRetry = true;
                                queue = null;
                            }
                        }
                    }
                }
                catch {
                    // swallow
                }
            }

            if (doHeartbeat && !activationSentThisRun) {
                // Policy: if activation was sent now, do NOT send heartbeat this week.
                // Marker was already written above, so later calls this week are also suppressed.
                try {
                    var evt = dispatcher.TryCreateHeartbeatEvent();
                    if (evt != null) {
                        var json = TelemetrySerializer.Serialize(evt, runtimeContext.ToolName);
                        if (json != null) {
                            if (telemetryQueue.Enqueue(json)) {
                                dispatcher.CommitHeartbeat(evt.Week);
                                Interlocked.Exchange(ref hasPendingWork, 1);
                            }
                            else {
                                // Do not suppress a heartbeat that never reached durable storage.
                                Interlocked.Exchange(ref heartbeatRequested, 1);
                                requestsNeedRetry = true;
                                queue = null;
                            }
                        }
                    }
                }
                catch {
                    // swallow
                }
            }

            // If we just enqueued something, ensure delivery runs promptly.
            if (Volatile.Read(ref hasPendingWork) == 1) {
                Signal();
            }

            return requestsNeedRetry;
        }

        private ITelemetryQueue? GetQueueOnWorkerThread() {
            if (queue is not null)
                return queue;

            queue = DurableTelemetryQueue.CreateSafe(runtimeContext);
            return queue;
        }

        private static async Task ApplyQueueRecoveryDelay(int attempt, CancellationToken token) {
            try {
                var multiplier = 1 << Math.Min(attempt - 1, 3);
                var delay = TimeSpan.FromMilliseconds(
                    Math.Min(
                        MaxQueueRecoveryDelay.TotalMilliseconds,
                        InitialQueueRecoveryDelay.TotalMilliseconds * multiplier));
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch {
                // swallow
            }
        }

        private async Task ApplyBackoff(CancellationToken token) {
            TimeSpan delay;

            lock (backoffLock) {
                currentBackoff = currentBackoff == TimeSpan.Zero
                    ? InitialBackoff
                    : TimeSpan.FromMilliseconds(
                        Math.Min(currentBackoff.TotalMilliseconds * 2, MaxBackoff.TotalMilliseconds));

                delay = currentBackoff;

                // Add a jitter to prevent multiple processes from hammering the endpoint at exactly the same time.
                Random rnd = JitterRandom.Value!;
                var jitter = TimeSpan.FromMilliseconds(rnd.Next(0, 300));
                delay += jitter;
            }

            try {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch {
                // swallow
            }
        }

        private void ResetBackoff() {
            lock (backoffLock) {
                currentBackoff = TimeSpan.Zero;
            }
        }

        public void Dispose() {
            try {
                cts.Cancel();
                cts.Dispose();
                signal.Release();
            }
            catch (SemaphoreFullException) { /* swallow */ }
            catch { /* swallow */ }

            try {
                httpSender.Dispose();
            }
            catch {
                // swallow
            }
        }
    }
}
