using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Verse;

namespace Firefly
{
    // Owns only the two-call conversation. Pending lifetime, idempotent claiming, adapter
    // validation, and resuming the incident remain main-thread responsibilities of the tracker.
    public static class EventDeciderOrchestrator
    {
        // The tracker supplies the actual wall-clock deadline. This per-attempt ceiling prevents
        // a request from lingering substantially beyond that deadline inside HttpClient.
        private const int RequestTimeoutSeconds = 3;

        public static void RequestDecision(EventDecisionRequest request,
            Action<EventDecisionResult> onDecided)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (onDecided == null) throw new ArgumentNullException(nameof(onDecided));

            var threadIds = new HashSet<string>(
                request.ActiveThreads.Where(t => t != null && !t.Id.NullOrEmpty()).Select(t => t.Id),
                StringComparer.OrdinalIgnoreCase);
            string intentPrompt = BuildIntentPrompt(request);
            Log.Message($"[Firefly:{EventDeciderPrompts.IntentLabel}] Sending for {request.IncidentDefName}...");
            LLMClient.Send(
                EventDeciderPrompts.IntentLabel,
                EventDeciderPrompts.IntentSystemPrompt,
                intentPrompt,
                onSuccess: raw =>
                {
                    EventIntentResult intent = EventDecisionResponseParser.ParseIntent(raw, threadIds);
                    if (intent == null) return false;
                    if (intent.Intent != EventIntent.ExistingThread)
                    {
                        onDecided(EventDecisionResult.Untouched(intent));
                        return true;
                    }

                    RequestParameters(request, intent, onDecided);
                    return true;
                },
                onError: error =>
                {
                    Log.Warning($"[Firefly:{EventDeciderPrompts.IntentLabel}] Failed for " +
                        $"{request.IncidentDefName}; firing untouched: {error}");
                    onDecided(null);
                },
                timeoutSeconds: RequestTimeoutSeconds);
        }

        private static void RequestParameters(EventDecisionRequest request, EventIntentResult intent,
            Action<EventDecisionResult> onDecided)
        {
            var allowedFields = new HashSet<string>(request.AllowedFields.Keys,
                StringComparer.OrdinalIgnoreCase);
            string prompt = BuildParametersPrompt(request, intent);
            Log.Message($"[Firefly:{EventDeciderPrompts.ParametersLabel}] Sending for " +
                $"{request.IncidentDefName} / {intent.ThreadId}...");
            LLMClient.Send(
                EventDeciderPrompts.ParametersLabel,
                EventDeciderPrompts.ParametersSystemPrompt,
                prompt,
                onSuccess: raw =>
                {
                    EventDecisionResult result = EventDecisionResponseParser.ParseParameters(
                        raw, intent, allowedFields);
                    if (result == null) return false;
                    onDecided(result);
                    return true;
                },
                onError: error =>
                {
                    Log.Warning($"[Firefly:{EventDeciderPrompts.ParametersLabel}] Failed for " +
                        $"{request.IncidentDefName}; firing untouched: {error}");
                    onDecided(null);
                },
                timeoutSeconds: RequestTimeoutSeconds);
        }

        private static string BuildIntentPrompt(EventDecisionRequest request)
        {
            var builder = new StringBuilder();
            AppendIncident(builder, request);
            builder.AppendLine("=== ACTIVE THREADS ===");
            foreach (EventThreadContext thread in request.ActiveThreads)
            {
                if (thread == null || thread.Id.NullOrEmpty() || thread.Summary.NullOrEmpty()) continue;
                builder.AppendLine($"[{thread.Id}] {thread.Name}: {thread.Summary}");
            }
            if (!request.RecentEvents.NullOrEmpty())
            {
                builder.AppendLine().AppendLine("=== RECENT EVENTS ===");
                builder.AppendLine(request.RecentEvents.Trim());
            }
            return builder.ToString();
        }

        private static string BuildParametersPrompt(EventDecisionRequest request, EventIntentResult intent)
        {
            var builder = new StringBuilder();
            AppendIncident(builder, request);
            EventThreadContext thread = request.ActiveThreads.First(t =>
                string.Equals(t.Id, intent.ThreadId, StringComparison.OrdinalIgnoreCase));
            builder.AppendLine("=== CONNECTED THREAD ===");
            builder.AppendLine($"[{thread.Id}] {thread.Name}: {thread.Summary}");
            builder.AppendLine($"Connection: {intent.Reason}").AppendLine();
            if (!request.FactionContext.NullOrEmpty())
            {
                builder.AppendLine("=== ELIGIBLE FACTIONS ===");
                builder.AppendLine(request.FactionContext.Trim()).AppendLine();
            }
            builder.AppendLine("=== ALLOWED FIELDS ===");
            foreach (KeyValuePair<string, string> field in request.AllowedFields)
                builder.AppendLine($"{field.Key}: {field.Value}");
            return builder.ToString();
        }

        private static void AppendIncident(StringBuilder builder, EventDecisionRequest request)
        {
            builder.AppendLine("=== SELECTED INCIDENT ===");
            builder.AppendLine($"Type: {request.IncidentDefName}");
            if (!request.IncidentLabel.NullOrEmpty()) builder.AppendLine($"Label: {request.IncidentLabel}");
            if (!request.BaseParameters.NullOrEmpty()) builder.AppendLine(request.BaseParameters.Trim());
            builder.AppendLine();
        }
    }
}
