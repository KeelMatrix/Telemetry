// Copyright (c) KeelMatrix

namespace KeelMatrix.Telemetry.Infrastructure {
    internal interface ITelemetryQueue {
        bool Enqueue(string payloadJson);
        IEnumerable<DurableTelemetryQueue.ClaimedItem> TryClaim(int maxItems);
        void Abandon(DurableTelemetryQueue.ClaimedItem item);
        void Complete(DurableTelemetryQueue.ClaimedItem item);
    }

    internal sealed class NullTelemetryQueue : ITelemetryQueue {
        public bool Enqueue(string payloadJson) => false;
        public IEnumerable<DurableTelemetryQueue.ClaimedItem> TryClaim(int maxItems) { yield break; }
        public void Abandon(DurableTelemetryQueue.ClaimedItem item) { }
        public void Complete(DurableTelemetryQueue.ClaimedItem item) { }
    }
}
